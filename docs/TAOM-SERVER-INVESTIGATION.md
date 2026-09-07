# Running TAOM (and other client-packaged mods) on the dedicated server

Investigation notes, 2026-09-07. Written down because two plausible-sounding diagnoses were both
wrong, and the evidence that settled it is easy to lose when launch logs rotate.

## Symptom

The dedicated server never reached `SERVING` with The Age of Machinery enabled. Without it the same
server package reached `SERVING` normally.

## Two wrong diagnoses

**1. "`Cannot load: 0Harmony.dll` is the cause."** It is not. The engine's `AssemblyLoader` eagerly
tries every referenced assembly by bare filename against `engine\bin\Win64_Shipping_Server\`, which
contains no `0Harmony.dll`, `DryIoc.dll`, `SandBox.dll` or any GauntletUI/View assembly. Every one of
those names is resolved by the startup hook microseconds later. `LogClassifier` already classifies
these as `Probe` for exactly this reason. They appear in successful runs too.

**2. "TAOM is view-bound, so it cannot run headless."** Superficially compelling: `TAOM\SubModule.xml`
tags `TAOM.SubModule` with `DedicatedServerType=none`, and `AssemblyScan` reports that `TAOM.dll`
constructs `GauntletLayer`, `GauntletMovie`, `MapScreen`, `MissionView` and `ScreenBase`. But that is
not what killed it.

Also worth recording: the "successful" control log used for comparison contained **no TAOM modules at
all**, so it never established that the TAOM loadout had ever worked. Check the `Command Args` line
before treating a log as a control.

## The actual cause

With the hook writing a sidecar log, flushing, and unwinding `InnerException`:

```
TAOM.SubModule.OnSubModuleLoad
  -> Bannerlord.UIExtenderEx.UIExtender.Create("TAOM")
  -> UIExtender static ctor Harmony-patches Managed::ApplicationTick
  -> HarmonyLib.HarmonyException: Patching exception
     inner: System.MissingMethodException: Method not found:
            'Void System.Reflection.Emit.ILGenerator.MarkSequencePoint(...)'
  -> System.TypeInitializationException
  -> process dies, 0xE0434352
```

`ILGenerator.MarkSequencePoint` is a .NET Framework API. It does not exist on the server's bundled
**.NET 6.0.36**. It is reached through the MonoMod that Harmony ILMerges into `0Harmony.dll`.

The trap: **Coop and TAOM.Dependencies both ship a `0Harmony.dll` reporting FileVersion 2.4.2.0, and
they are different builds.**

| copy | size | takes the MarkSequencePoint path |
|---|---|---|
| `Modules\Coop\bin\Win64_Shipping_Server\0Harmony.dll` | 2,333,696 | no |
| `Modules\TAOM.Dependencies\bin\Win64_Shipping_Server\0Harmony.dll` | 2,461,696 | yes |

Same nominal version, different hashes. Version numbers are not sufficient to tell these apart.

`HookSetup.SearchDirs` already puts the stock server bins first so shared libraries come from Coop's
copies, "never from an older one a mod happens to bundle". `StartupHook.Resolve` was quietly undoing
that by prepending the **requesting assembly's own folder**: MCM and `TAOM.Dependencies` live next to
TAOM's `0Harmony.dll`, so they pulled in the broken build.

### Fix

`StartupHook` keeps requester-folder-first for a mod's private helper DLLs, but skips it for shared
runtime plumbing (`0Harmony`, `MonoMod.*`, `Serilog*`, `Newtonsoft.Json`), which now takes the search
list as ordered, so the server's own copy wins.

With that in place TAOM loads headless: no `TypeInitializationException`, no `HarmonyException`, and
the server goes from dying at 7 seconds to `phase:"boot"` then `phase:"loading"`.

## The remaining blocker: DsAssetPackages

TAOM now gets into save loading and then hits **104 missing animations** (`elephant_rider_*`,
`howdah_*`, the Mumakil content) and **52 blocking `Always Ignore?` message boxes**, after which the
engine exits -1.

The dedicated-server build of the engine loads **`DsAssetPackages`**, not `AssetPackages`. Stock
Native ships only the former:

```
Modules\Native\DsAssetPackages\animations.tpac        596 MB
Modules\Native\DsAssetPackages\animation_clips.tpac   131 MB
Modules\Native\AssetPackages\                         does not exist
```

TAOM, TAOM_Map and LOTRLOME_Armory ship only client `AssetPackages`, so their animations and
skeletons are invisible to the server.

**The animations come from LOTRLOME_Armory, not TAOM.** `elephant_rider_hit` and the rest are
declared in `LOTRLOME_Armory\ModuleData\action_sets.xslt` and the binaries live in that mod's
`AssetPackages\pack1.tpac` (1.17 GB). `action_sets.xslt` is reached through `<Xmls>`, which the
server always processes, so the declarations load while the animation data cannot. Note that
`DependencyOnly` does **not** help here: it removes `<SubModule>` elements only, and LOTRLOME_Armory
has none to begin with. The only ways out are to suppress the asset error or to leave that mod off
the server.

### Dead end: do not map AssetPackages onto DsAssetPackages

Tried, reverted. The engine does read the mapped folder, and the log confirms it with
`Loading packages $BASE/Modules/TAOM.Dependencies/DsAssetPackages...` -- but these are full client
render packs:

| module | AssetPackages |
|---|---|
| TAOM | 189 MB (1 pack) |
| TAOM_Map | ~13 GB (5 packs) |
| LOTRLOME_Armory | ~10 GB (9 packs) |

Feeding those to a headless server caused a **native access violation (0xC0000005)** during
`Registering items`, and it failed *earlier* than without the mapping. `DsAssetPackages` is a curated
server subset produced by TaleWorlds' asset pipeline; it cannot be synthesised from client packs.

## Measured: dropping LOTRLOME_Armory clears the animation failures

Launching `TAOM.Dependencies:DependencyOnly, TAOM:Run, TAOM_Map:Run` with no LOTRLOME_Armory gives
**zero** "Could not find animation" lines and no `Always Ignore?` modals, confirming that mod as the
sole source of all 104. The server then runs for minutes rather than seconds and gets as far as
loading the campaign map.

It still does not reach `SERVING`. It ends up in a loop:

```
[DedicatedServer] CreateMapScene -> DedicatedServerMapScene
[DedicatedServer] DedicatedServerMapScene.Load (proven native set)
[DedicatedServer] reading Main_map...
[DedicatedServer] Main_map read OK, navmesh CRC=1465536726
[DedicatedServer] settlement distance cache: ..\..\Modules\SandBox/ModuleData\DistanceCaches\settlements_distance_cache_Default.bin
```

repeated until the run was stopped. `Main_map` reads successfully every time, so this is not an
asset failure. TAOM_Map replaces the campaign map, and the settlement distance cache being read is
**SandBox's** `settlements_distance_cache_Default.bin`, not one belonging to TAOM_Map -- worth
checking whether the map and the cache disagree, and whether the loop is Coop retrying campaign
initialisation.

This is the next thing to investigate, and it is unrelated to the asset packages.

## Open lead

Make the engine's asset-failure modal non-fatal so missing client-only assets degrade instead of
aborting. Not yet attempted.

Evidence that this is safer than it first sounds:

- The failure happens during **save load**, not in a battle. The engine is resolving TAOM's
  monster/skeleton animation references while initialising the world. The server never needs to
  *play* those animations to reach `SERVING`.
- Coop's `Missions.dll` defines `MissionBehavior`, `MissionNetwork` and `BattleMission` types but
  contains **no `AgentApplyDamageModel`**, which suggests it does not run authoritative combat damage
  server-side. In a campaign coop of this shape, battles run on the joining players' clients, and
  those clients have TAOM's full client `AssetPackages`, so the animations exist where they are
  actually rendered and simulated.

This has not been proven end to end. Confirm it by reaching `SERVING` and then fighting a battle
involving Mumakil.

## Reproducing

Dry-run a profile without touching anything:

```bash
dotnet src/ModderLords.Cli/bin/Release/net10.0/ModderLords.Cli.dll launch --profile default --dry-run
```

Bounded diagnostic launch. It passes no `/coopvisibility`, so the server is not advertised, and it
self-terminates:

```bash
dotnet src/ModderLords.Cli/bin/Release/net10.0/ModderLords.Cli.dll launch --mods "TAOM.Dependencies:DependencyOnly,TAOM:Run,TAOM_Map:Run,LOTRLOME_Armory:Run" --port 7299 --stop-after 300
```

Set `MODDERLORDS_HOOK_FIRSTCHANCE=1` to log every thrown exception, including handled ones. It is
noisy but it is what exposed the chain above. The hook also writes
`%LOCALAPPDATA%\ModderLords\logs\hook-<stamp>.log`, which survives a crash that truncates stdout.

## Regression checks worth repeating

The known-good stack must still reach `SERVING`:

```bash
dotnet src/ModderLords.Cli/bin/Release/net10.0/ModderLords.Cli.dll launch --mods "Bannerlord.Harmony:DependencyOnly,Bannerlord.ButterLib:DependencyOnly,Bannerlord.UIExtenderEx:DependencyOnly,Bannerlord.MBOptionScreen:DependencyOnly,CoopModPatch:Run,ImprovedGarrisons:Run,ModularSmithing2:Run" --port 7299 --stop-after 200
```

Expect `phase:"serving"`, a `SERVING` line, and exit 0. Allow about 60 seconds; a short
`--stop-after` cuts it off during loading and looks like a failure.

Also diff `launch --dry-run` output against `origin/main` for `default`, `layer1` and `profile3`.
Only `MODDERLORDS_HOOK_LOG` should differ.

## Gotchas collected along the way

- A curated compat record beats a static scan. `AssemblyScan` reports `NeedsReview` for
  ImprovedGarrisons, which reaches `SERVING` behind the bundled guards, because its view references
  sit in code paths a headless server never enters. `OverlayPlanner` therefore suppresses the
  client-only warning when a record's `DefaultRole` is `Run`.
- `CompatDb.KeepForDependencyOnly()` is a **global union** across every record, matched with
  `StringComparer.Ordinal`. A keep-list entry added for one mod applies to all of them.
- `MainViewModel` never rewrites mod entries that already exist in a profile, so changing a default
  role in the database does nothing to a profile that already lists the mod.
- `ManifestRewriter` only ever removes `<SubModule>` elements. `<Xmls>`, `<Id>` and `<Version>` are
  untouched, which is why `DependencyOnly` keeps a content mod's world data and its Coop handshake
  identity while loading none of its code.
