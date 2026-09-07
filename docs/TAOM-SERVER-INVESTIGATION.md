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

repeated until the run was stopped. `Main_map` reads successfully every time with a constant
`navmesh CRC=1465536726`, and the scene comes from `DedicatedServer.Windows`, not TAOM_Map, so this
is not an asset failure and not a map failure.

**Resolved 2026-09-07: it is a save/module mismatch.** The chain, from
`Logs	aom_debug_2026-09-07_04-53-48.log`:

1. Coop logs `Save "saveauto1" module mismatch` for **7 removed** modules and **3 added**, then
   `Forcing load anyway`. `saveauto1` was built by the *non-TAOM* server.
2. Its ObjectManager then cannot register hundreds of `MobileParty` objects, and the engine reports
   ~37 `Null object reference found with ID: <settlement>_comp_*`.
3. At tick 1629 the state machine enters `LoadVisualsThirdState` and the tick counter never advances.
4. `GameStateManager.OnTick_Patch2` throws `NullReferenceException`; TAOM's crash handler catches it,
   auto-accepts its own inquiry and lets the tick return, so the state is re-entered forever.
5. After ~30 s it becomes `IOException 0x80070020` on `settlements_distance_cache_Default.bin`,
   because each failed iteration leaks the handle. From there it cannot recover.

So a TAOM server needs a world **created with TAOM loaded**. `SavePreparer.EnsureExists` only ever
copies `default_new_game.sav`, which is a pre-baked *vanilla* world
(`Modules = Native;SandBoxCore;Sandbox;Coop`). Nothing in the repo or the server package generates a
world.

## Can the server create its own world? No.

Answered from assembly metadata alone, no test launch (`WorldGenerationProbe`):

| | |
|---|---|
| `DedicatedServer.Core.dll` | no world-creation surface at all — zero matches for character creation, new-game or save-manager names |
| `SandBox.SandBoxGameManager` | public, but `LaunchSandboxCharacterCreation`, the only method that starts a campaign, is **private instance** |
| `SkipCharacterCreationInternal` | instance method on an **internal**, Autofac-registered type, and gated on `InCharacterCreationIntro` — it dismisses a creation already in progress, it does not make a world |

Driving any of that means Harmony-patching the official host from outside, which this project does
not do. GameInterface also ships `StartCharacterCreation`, `CharacterCreationHandler` and
`ClientCharacterCreationState`: in Coop's design character creation arrives as a message **from a
client**. Seeding the server from a client-built world is with the grain, not a workaround.

Hence `SavePreparer.ImportFrom`, reachable as `saves` / `import-save` in the CLI and as an expander
on the Saves tab. Note that a client save legitimately lacks the server-side modules, but it must
list a Coop module or the world was not built for coop; the import warns when it does not. (The only
client save on this machine lists `[ModularSmithing2]` and nothing else, so this is not hypothetical.)

## Measured: the asset modals cannot be suppressed, and would not help

`Debug.DebugManager` is a settable property, so `HeadlessDebugManager` decorates the manager the host
installed and swallows `ShowMessageBox`. Two things came out of testing it.

**It has to be re-installed.** The wrapper goes on during `OnSubModuleLoad`; about four thousand log
lines later the host prints `managed DebugManager installed` and assigns its own, discarding it long
before the campaign loads. Last writer wins, so it is re-asserted on tick. Measured in sequence:
ours at 1774, the host's `A.e` at 5955, ours again at 6001, then `SERVING`.

**It does not reach these modals.** With LOTRLOME_Armory enabled the run produces 137
"Could not find animation" warnings and 68 `Always Ignore?` prompts — the counts vary between runs,
the first recorded 104 and 52 — and **none** arrive at `ShowMessageBox`. They are raised by the native rgl layer and printed straight to stdout, in the same
`Messagebox [...] message:` shape as the loader's `Cannot load:` probes.

So the engine-side toggles are wired instead —
`Utilities.SetAssertionsAndWarningsSetExitCode(false)` and `SetCrashOnAsserts(false)`, applied at
install and again once the host has finished its own debug setup. They apply successfully, and the
run then **stops exiting through the assertion path and dies in native code instead**, with an
`Rgl.pdb` / `FairyTale.Library.pdb` stack, before the loading steps begin.

**The modal was a symptom.** LOTRLOME_Armory is not recoverable by suppressing it, which is
consistent with the access violation from mapping its `AssetPackages`. Leave it off the server. The
earlier hope — that battles run client-side, so missing server animations are survivable — remains
untested, because the server cannot get far enough to test it.

Both toggles change engine behaviour globally, so they are applied only on a dedicated server, where
the prompt is unanswerable by construction. The known-good stack was re-run with every guard active:
`SERVING`, exit 0.

## Diagnostics added, and what they now say

The whole investigation cost two blind five-minute runs, so the failures were made legible first:

- **Pre-flight save/module check** (`SaveModuleCheck`) — warns, never blocks. Two things it had to
  get right: `Coop` is a community module by `CommunityModuleIds`' definition but is stock in the
  server package, so an unfiltered check reports "Coop is missing" on *every* launch; and the
  documented repro command names no save at all, so Coop falls back to its autosave `saveauto1`.
  Without resolving that implicit case the check would have missed its own motivating bug.
- **Stall detector** (`LoadStallDetector`) — keys on the `loading step @tick` *state names*, not the
  tick number, because the tick advancing while the state does not is exactly the loop.
- **`LogClassifier`** — `RGL WARNING` missed because the `Warning` test is case-sensitive, nothing
  matched `Messagebox [Always Ignore?]` or Coop's `module mismatch`, and a swallowed crash changed
  category with whichever exception it named because the exception regex did not allow a dotted
  namespace (`System.IO.IOException`).

Run against the real failure, the same command that previously produced 5m35s of silence now says:

```
WARNING save 'saveauto1' does not match this module set: 7 module(s) it was built with are not in
this launch (...); 3 module(s) in this launch are not in the save (TAOM.Dependencies, TAOM, TAOM_Map).
  (no save was named, so Coop will load its autosave 'saveauto1')
WARNING no loading progress for 90s: stuck at
  'manager=FinishLoadingFifthStep gameType=LoadVisualsThirdState'.
```

The first line appears before the engine starts.

## What is still unproven

Reaching `SERVING` with TAOM needs a world built by a client running TAOM, and creating one means
playing through character creation. Until that exists:

- no TAOM server has reached `SERVING`, so `TAOM:Run` is only known to **load**, not to serve;
- the client handshake and `ModuleValidator` pass are untested;
- whether TAOM battles work without server-side animations is untestable.

## Reproducing

See which worlds exist on each side, and seed the server from one the game built:

```bash
dotnet src/ModderLords.Cli/bin/Release/net10.0/ModderLords.Cli.dll saves
```

```bash
dotnet src/ModderLords.Cli/bin/Release/net10.0/ModderLords.Cli.dll import-save --from "My TAOM Campaign"
```

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
