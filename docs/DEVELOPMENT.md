> Release 1.0.2 runtime boundary: the classifier/recipe development history below includes experimental schema-v2/v3 machinery. Those generated transformations are now diagnostic only, including SyncState; neither the Experimental switch nor a schema-v1 label authorizes them. `RecipeSet.Build` emits explicit legacy gates plus settings/scene/trace data; `BuildDiagnostic` is study output. New transformations require validated compiled operation contracts. See [OPERATION-COMPATIBILITY.md](OPERATION-COMPATIBILITY.md).

﻿# ModderLords: developer notes

A launcher for the **Bannerlord Coop dedicated server** that lets you run it with community mods,
using the pristine official server package and the mods where they already live on disk.
It replaces the need for third-party wrappers that patch files inside the Steam workshop folder.

Status: Phase 1 complete (community mods load on the pristine server through junctions + a resolver hook).

## What it does

- Launches the official Coop dedicated server engine directly with your own module list and load order.
- Never copies mods into the server. Mods are exposed to the engine through NTFS junctions and a small
  rewritten copy of each `SubModule.xml` kept in `%LOCALAPPDATA%\ModderLords`.
- Owns `server-config.json` rendering instead of regex-editing it.
- Warns about save/mod version drift but never blocks a launch and never rewrites save bytes.
- Streams the engine console with classification (module load, server, Coop, warnings, errors, milestones).
- Exports the exact client mod list players need (Coop requires an exact id+version match for community mods).

## Layout

| Project | Purpose |
|---|---|
| `src/ModderLords.Core` | The mod loader: module catalogue, load order, profiles, the overlay, the compat database, mod-list export, `LauncherData.xml`, launching the player's own game |
| `src/ModderLords.Coop` | Hosting the dedicated server: finding the workshop package, the headless engine and its process, server and gameplay config, save prep, the live-settings channel |
| `src/ModderLords.Cli` | Command-line driver (spikes, headless operation, and `play`) |
| `src/ModderLords.App` | WPF desktop app |
| `src/ModderLords.Hook` | `DOTNET_STARTUP_HOOKS` assembly that resolves mod assemblies for the headless engine |
| `src/ModderLords.Compat` | net472 in-game module: headless guards (server only) |
| `src/ModderLords.CompatSync` | net472 in-game module: the shared settings-sync module, with no reference to Coop |
| `src/ModderLords.CompatSync.Coop` | net472 adapter that does touch Coop's assemblies, loaded by hand at runtime |
| `tests/ModderLords.Core.Tests` | xUnit tests (covers both libraries) |

**Core must not reference Coop.** That is the whole point of the split: the mod loader has to build, test and
ship on a machine that has never installed Coop, and Player mode must have no accidental path into server code.
The compiler enforces the direction. Where the dependency naturally wanted to point the wrong way it was
inverted rather than allowed: `GamePaths` owns the Steam library scan and `ServerPaths` forwards to it,
`SaveHeaderReader` owns the name of the template save that `SavePreparer` copies, and `ProfileStore` owns the
paths of the sidecar files it deletes. `ModuleSelectionResult` is the shared shape both launch paths produce, so
the mod-list export and the `LauncherData.xml` reconciliation no longer derive from a *server* plan.

`ModderLords.CompatSync.Coop` compiles against Coop's own assemblies, which ship inside the workshop item rather
than on NuGet. `Directory.Build.props` resolves that folder and sets `HasCoopAssemblies`; App and Cli reference
the project only when it is true. So CI builds the whole app without a Coop install, and
`scripts\package-release.ps1` refuses to package a build that is missing the adapter.

## Verified facts about the official server (2026-09-02, Coop v0.1.4, Bannerlord 1.4.8)

- Package location: `steamapps\workshop\content\261550\3770450698\DedicatedServer`. Contents: `BannerlordCoopServer.exe`
  (console host), `engine\` (headless engine with bundled .NET), `release-info.txt`, `server-data\`.
- The host starts the engine as: working directory `engine\bin\Win64_Shipping_Server`, command
  `engine\dotnet\dotnet.exe TaleWorlds.Starter.DotNetCore.dll _MODULES_*Native*DedicatedServer.Windows*SandBoxCore*SandBox*Coop*_MODULES_ /dedicatedcustomserver 7210 EU 0`
  with environment `DOTNET_ROOT`, `DOTNET_MULTILEVEL_LOOKUP=0`, `BANNERLORD_USER_DIR=<data dir>`, `COOP_DATA_DIR=<CoopData>`.
- The engine core accepts `/coopsave <name>`, `/cooppassword <pw>`, `/coopvisibility public|friends_only|none` on that command line.
- The engine reads console commands from stdin (`stop` shuts down cleanly with exit code 0).
- A save named on the command line must already exist in `<data dir>\Game Saves\`; bootstrapping a new world from
  `default_new_game.sav` is the console host's job, so this launcher does it too (`SavePreparer`).
- Exit codes: 0 clean stop, 2 load failure/timeout, 3 fatal while serving, 4 Coop module verification failed.
- `server-config.json` keys the pristine core understands: `saveName, port, password, autosaveMinutes, logFile, steam, traceTick, tracePublish, traceBandits`.
  Keys `autoRestart, autoRestartHours, allowClientTimeControls, battleSize` are ignored by the pristine core (they come from third-party launcher extensions).
- Engine module discovery only scans `engine\Modules\*`; a module must appear there (a junction works).
- Submodule DLLs are looked up in `<module>\bin\Win64_Shipping_Server\`, then `engine\bin\Win64_Shipping_Server\`.
- Submodule tags `DedicatedServerType=none` / `IsNoRenderModeElement=false` make the engine skip that submodule on the server.

## Verified facts about the player's own game (2026-09-06, Bannerlord 1.4.8.119303)

Measured on a real install, twice, before any of the client-launch code was written.

- `bin\Win64_Shipping_Client\Bannerlord.exe` accepts the same `_MODULES_*A*B*_MODULES_` token the TaleWorlds
  launcher builds, as its only argument.
- **That token completely replaces `Configs\LauncherData.xml`.** Modules with `IsSelected=false` there loaded
  because the token named them (ButterLib, UIExtenderEx, MCM, ImprovedGarrisons); modules with `IsSelected=true`
  did not load because the token omitted them (ModularSmithing2, CustomBattle). The file was byte-identical
  (same MD5, same mtime) before and after both launches — the game never writes it, so neither do we.
- **Workshop ids resolve without help.** Every community mod on the test machine lives in
  `steamapps\workshop\content±550\<item id>` and none of them are in the game's `Modules` folder, yet naming
  them by id in the token was enough. This is the opposite of the dedicated server, which is a separate Steam app
  (1863440) with no Steam integration and genuinely can only see `$BASE\Modules` — hence the junction overlay
  there, and hence no overlay here.
- **Order is passed through exactly and it matters.** A first run deliberately placed the frameworks after the
  official modules; ButterLib opened a modal "Bannerlord.ButterLib is loaded after the Native! ... It's strongly
  recommended to terminate the game now" and the game stopped there. Re-running with the frameworks ahead of
  Native — which is what `LoadOrder` already computes, from `ModulesToLoadAfterThis` — reached the main menu with
  no complaint. `LoadOrder.Profile.Client` exists for exactly this: same sorter, no phantom modules (StoryMode and
  friends are really installed), nothing pinned after the mods.
- A module id may contain spaces ("Heal on Kill"); only `*` is special, so `ClientLaunchPlan.Validate` rejects
  that character and nothing else.

Consequence for the code: `ClientLaunchSession` reuses `ModuleCatalog`, `LoadOrder` and `LaunchSession.Select`,
and uses none of the overlay, the resolver hook, the compat modules or the server config. Those exist to make mods
survive a headless engine, which is not a problem the player's game has.

## Releases and the in-app updater

**The step-by-step release procedure is [RELEASING.md](RELEASING.md).** Follow it; this section is the background.

The updater (`src/ModderLords.Core/Updates`) trusts a narrow contract, so every release must keep it:

- **Tag** `vX.Y.Z` — exactly three numbers. A suffix (`-test`, `-rc1`) is never offered.
- **One asset** named exactly `ModderLords-X.Y.Z.zip`, produced by `scripts/package-release.ps1 -Version X.Y.Z`, with
  `ModderLords.exe` at the zip root. The installer refuses a zip without it.
- **A normal release**, not a prerelease or draft: the check reads `/releases/latest`, which skips both. Publish
  anything experimental as a **prerelease** and no installed copy will see it.
- GitHub's per-asset `sha256:` digest is required: the installer refuses a download it cannot verify.

Testing the update path without publishing: set `MODDERLORDS_UPDATE_FEED` to a local JSON file shaped like the
releases API response, whose `browser_download_url` is a local zip path. Dev (Debug) builds never install an update in
place; they open the release page, so test with a packaged Release build.

The install swaps files in place: the running exe is renamed to `ModderLords.exe.old` (deleted on the next start),
replaced items move to `.previous\` inside the install folder, and any failure part-way restores the folder.

## Licensing note

Bannerlord Coop is source-available, not open source. This project launches its binaries and relies only on their public
contracts (command line, config files, module manifests). No Coop or third-party launcher code is included.

## Phase 1 result (2026-09-02): community mods load on the pristine server

Verified with ModularSmithing2, ImprovedGarrisons, HealOnKill, CoopModPatch (run on server) plus Bannerlord.Harmony,
ButterLib, UIExtenderEx and MCM (dependency-only): the engine reaches `SERVING`, MS2's coop adapter reports
`coop compat OK: 15/15`, CoopModPatch arms its ImprovedGarrisons patches, and a save written without those mods
loads with the engine's "module mismatch ... Forcing load anyway" warning. The only change inside the Steam folder is
one junction per mod under `engine\Modules`.

How it works:

- **Overlay** (`ModderLords.Core.Overlay`): a mod that already ships `bin\Win64_Shipping_Server` and no client-only tags gets a
  direct junction. Everything else gets a shadow folder in `%LOCALAPPDATA%\ModderLords\overlay\<profile>\<Id>` holding a
  rewritten `SubModule.xml` (id and version untouched), `bin\Win64_Shipping_Server` junctioned to the mod's client bin,
  and every other folder junctioned as-is. Roles: `Run` strips the `DedicatedServerType`/`IsNoRenderModeElement` tags,
  `DependencyOnly` removes the submodules (MCM's headless settings core is allow-listed), `AsShipped` changes nothing.
- **Hook** (`ModderLords.Hook`, loaded via `DOTNET_STARTUP_HOOKS`): the engine only probes its own bin for referenced
  assemblies, so a last-resort `AssemblyResolve` handler searches `MODDERLORDS_SEARCH_DIRS` (Coop's server bin first,
  then each mod's bins, then the game client bin). No game code is patched. Everything it prints is flushed and
  mirrored to the sidecar file named by `MODDERLORDS_HOOK_LOG` (`%LOCALAPPDATA%\ModderLords\logs\hook-<stamp>.log`),
  and unhandled exceptions plus `ProcessExit` report the last assembly requested and the last one actually loaded.
  That pair is the only breadcrumb left when the engine dies inside the UI stack without unwinding.
- **Load order** (`LoadOrder`): official host sequence `Native, SandBoxCore, Sandbox, <community>, <Coop id>, DedicatedServer.Windows`,
  community block sorted by BUTR's `ModuleSorter`, Harmony first, StoryMode/CustomBattle/BirthAndDeath treated as
  satisfied (they never exist on a server).

Gotchas found on the way:

- The workshop build's server Coop folder carries the id **`CoopNightly`** (empty submodule list; the server core loads
  Coop itself). The token must use the id from `SubModule.xml`, not the folder name.
- The engine's `AssemblyLoader` eagerly tries every referenced assembly by bare file name and logs
  `Messagebox [ERROR] ... Cannot load:` for each miss before resolving it properly. The console classifies those as warnings.
- A mod can declare itself client-only (`DedicatedServerType=none`) and mean it. `Run` strips that tag, and if the mod's
  code constructs `GauntletLayer`/`MissionView`/`ScreenBase` the engine pulls the render stack into a headless process
  and dies with **no managed exception and no `SERVING`** - the log just stops. `OverlayPlanner` now scans such a mod
  (`AssemblyScan`) and warns before launch; `DependencyOnly` is the way out, since `ManifestRewriter` only ever removes
  `<SubModule>` elements and leaves `<Xmls>` alone, so a content mod keeps serving its data with none of its code.
- A `Resolving` handler on the default load context runs before every other resolver and would hand Coop an older Serilog
  bundled by ButterLib. The hook uses `AppDomain.AssemblyResolve` only, with the requester's folder and Coop's bin first.

CLI:

```
dotnet run --project src/ModderLords.Cli -- catalog
dotnet run --project src/ModderLords.Cli -- sync   --mods Bannerlord.Harmony:DependencyOnly,ModularSmithing2,HealOnKill
dotnet run --project src/ModderLords.Cli -- launch --mods Bannerlord.Harmony:DependencyOnly,ModularSmithing2,HealOnKill --save "29 August 26"
dotnet run --project src/ModderLords.Cli -- sync   --remove-all
```

## Phase 2 (2026-09-02): profiles, config rendering, desktop app

- `ModderLords.App` (`ModderLords.exe`): Mods tab (enable, role, order, load-order preview, messages), Saves tab
  (save headers read from the `.sav` files, diff against the profile, never blocks), Server tab (rendered into
  `server-config.json` with a backup under `config-backups`), Console tab (classified, filterable, searchable, command
  entry, clean Stop over stdin), Players tab (mod list for players + "check my client" against `LauncherData.xml`).
- Profiles live in `%LOCALAPPDATA%\ModderLords\profiles\<name>.json`; each profile has its own overlay folder.
- `LaunchSession` is the single path from profile to running engine, shared by the app and the CLI.
- Launch logs: `%LOCALAPPDATA%\ModderLords\logs\launch-<timestamp>.log`.

## Phase 3 (2026-09-02): drift, re-sync, gameplay settings

- Drift banner: when a mod's installed version differs from what the profile last launched with, a banner names the mods
  and reminds that players must update and a running server needs a restart.
- "Re-sync junctions" recreates the links under `engine\Modules` after a workshop update or a Steam re-download of the server.
- Gameplay tab edits `CoopData\mod-config.json` value by value, keeping the Coop mod's comments (backup under `config-backups`).
- `docs/ROADMAP.md` records the future direction, including generalized mod-compatibility assistance.

## Headless compatibility: where to start reading

Most compatibility problems on the dedicated server are one shape — the server answers a query with a default and
says nothing, so a mod gets a plausible wrong answer instead of a failure.

- **`docs/HEADLESS-MAP-ANSWERS.md`** is the inventory of every such member, what each returns, and the method that
  found them (decompile `DedicatedServer.Core`; `SandBox.dll` is not obfuscated, so read the two side by side).
  Start here before investigating a mod that misbehaves only on the server.
- **`docs/FIELD-BATTLE-TERRAIN.md`** is the worked example: a null `byte[]` behind one query crashed every client
  entering a field battle, plus the wrong turns taken on the way, which are worth not repeating.

Server-side switches, all read from the launcher's environment (the launch log lists inherited `MODDERLORDS_*`
variables, marked, so "was it actually set?" is answerable from the log):

| Variable | Default | What it does |
|---|---|---|
| `MODDERLORDS_MAP_PATCH_RESTORE` | on | Answers `GetMapPatchAtPosition` from the engine's own battle-scene index map. Without it every field battle loads one arbitrary battle terrain. `0` disables. |
| `MODDERLORDS_STUB_WARNINGS` | on | Warns once per run when a mod reads a query the server does not implement. `0` silences. |
| `MODDERLORDS_HEADLESS_MAP_BOUNDS_CHECK` | on | Checks the loaded scene's own border markers against the bounds in effect, and warns loudly if they disagree. Replaced the navmesh hash check and the `Exit(12)`. `0` disables. |
| `MODDERLORDS_ASSERT_THROTTLE` | on | Forwards each failed assert site the first 20 times, then once per 10,000, and reports the busiest held-back sites every 30 s. A new site always gets through. Added after one pathfinding assert repeated ~7,300×/s, wrote 2.7 GB and cut the tick rate to 15/s. `0` disables. |
| `MODDERLORDS_TERRAIN_PROBE` | off | Samples the map scene to a CSV; `scripts/Compare-TerrainProbe.ps1` diffs a server's against a client's. |
| `MODDERLORDS_MAPSCENE_CENSUS` | off | Counts which map-scene members are actually called, and names those never called. |
| `MODDERLORDS_HEADLESS_MAP_KEEP_TERRAIN` | off | Keeps the scene's `<terrain>` descriptor. Measured safe; not currently needed. |
| `MODDERLORDS_BATTLE_SCENE_PICK` | on | The launcher lists scenes shipped without a terrain shader cache in `recipes.json` (`ExcludedBattleScenes`), and the server never chooses one for a field battle (every client loads the server's choice and crashes on those; vanilla ships `battle_terrain_020` and `battle_terrain_a` that way). `0` disables. Details: `docs/FIELD-BATTLE-TERRAIN.md`, 2026-09-15. |
| `MODDERLORDS_TRACE_MODS` | off | Ground truth for the authority classifier: `TAOM;ImprovedGarrisons` makes the launcher list every entry point of those mods in `recipes.json` (`TraceRoots`); both the server and every client then count how often each one runs and write `ModLogs\ModderLords.Compat-trace-{server,client}.jsonl` every 30 s. Compare with `trace-diff`. Read by the launcher (the recipe carries it to clients), so set it in the launcher's environment. |

### Smoke-testing a server without the GUI

`ModderLords.Cli launch --stop-after N` runs a round unattended: it waits for SERVING, then sends `stop` and
exits with the engine's code. Useful for repeated launches.

**It does not currently host TAOM.** Measured 2026-09-12, with the same binaries the GUI served on minutes
earlier:

| | `CreateMapScene` calls | `guard: ShowInquiry by TAOM` | SERVING |
|---|---|---|---|
| GUI Host button | 1 | no | **yes** |
| CLI ad-hoc `launch --mods ...` | **17,128** | **yes** | no, exit -1 after 15 min |

The ad-hoc run ends in the `CreateMapScene` loop that `compat-db.json` already describes as "the campaign state
machine re-entering after a swallowed exception", and the one line the GUI run does not have is TAOM raising an
inquiry during campaign init. This matches what `docs/TAOM-WORLD-GENERATION.md` records of every earlier ad-hoc
attempt — they "stalled in campaign init" — so it is a long-standing gap in that path, not a regression.

Until it is fixed, smoke-test TAOM through the launcher. The CLI is fine for vanilla and for world creation,
which is what it was built for.

## Phase 4 (2026-09-02): hardening

- `Preflight`: refuses to launch when the join or engine UDP port is in use, an engine from this package is already
  running, or the hook DLL is missing; warns on low disk. Shown in the console before "Preparing".
- Overlay apply is per-mod crash-safe: a failing mod is skipped with a warning and its half-built link removed; the
  state file is always written.
- Launch logs rotate (newest 20 kept). Window title shows the version.
- Integration tests create real junctions and shadow folders in a temp tree (Windows only).
- Release packaging: `scripts\package-release.ps1 -Version x.y.z` -> `artifacts\ModderLords-x.y.z.zip`.

## Compat Layer 0 (2026-09-02): server guards + assembly scan

- `src/ModderLords.Compat`: a net472 Bannerlord module (id `DedicatedServer.ModderLordsCompat`, exempt from Coop's
  client match because of the `DedicatedServer.` prefix). On a dedicated server it Harmony-patches
  `MBInformationManager.ShowMultiSelectionInquiry`, `InformationManager.ShowTextInquiry/ShowInquiry/ShowTooltip`
  and `ScreenManager.Push/CleanAndPush/ReplaceTop/Pop/CleanScreens`: inquiries are answered (first option / default
  text / affirmative), screens are swallowed, first hit per calling mod is logged. Every target is resolved by name and
  skipped when missing. Off outside a dedicated server. Verified: ImprovedGarrisons + HealOnKill + MS2 reach SERVING
  without CoopModPatch, 9 guards active, 0 errors.
- `ModderLords.Core.Compat.AssemblyScan`: IL-metadata scan (System.Reflection.Metadata) of each submodule DLL for UI /
  StoryMode assembly references, guarded member calls, and hard UI type refs (GauntletLayer, ScreenBase, MapScreen...).
  Verdict shown in the Mods tab and by `ModderLords.Cli launch` output. Nothing is loaded or executed.
- Launcher: `Profile.CompatGuards` (default on) appends the compat module as an AsShipped direct junction; CLI flag `--compat`.
- Note: the resolver hook already serves the StoryMode/CustomBattle client assemblies from the game install when a mod
  references them, so the "reference-only shim" from the plan was not needed for the tested mods.

## Compat Layer 0 (2026-09-02): server guards + assembly scan

- `src/ModderLords.Compat`: a net472 Bannerlord module (id `DedicatedServer.ModderLordsCompat`, exempt from Coop's
  client match because of the `DedicatedServer.` prefix). On a dedicated server it Harmony-patches
  `MBInformationManager.ShowMultiSelectionInquiry`, `InformationManager.ShowTextInquiry/ShowInquiry/ShowTooltip`
  and `ScreenManager.Push/CleanAndPush/ReplaceTop/Pop/CleanScreens`: inquiries are answered (first option / default
  text / affirmative), screens are swallowed, first hit per calling mod is logged. Every target is resolved by name and
  skipped when missing. Off outside a dedicated server. Verified: ImprovedGarrisons + HealOnKill + MS2 reach SERVING
  without CoopModPatch, 9 guards active, 0 errors.
- `ModderLords.Core.Compat.AssemblyScan`: IL-metadata scan (System.Reflection.Metadata) of each submodule DLL for UI /
  StoryMode assembly references, guarded member calls, and hard UI type refs (GauntletLayer, ScreenBase, MapScreen...).
  Verdict shown in the Mods tab and by `ModderLords.Cli launch` output. Nothing is loaded or executed.
- Launcher: `Profile.CompatGuards` (default on) appends the compat module as an AsShipped direct junction; CLI flag `--compat`.
- Note: the resolver hook already serves the StoryMode/CustomBattle client assemblies from the game install when a mod
  references them, so the "reference-only shim" from the plan was not needed for the tested mods.

## Compat Layer 2a (2026-09-02): MCM settings sync

- `src/ModderLords.CompatSync`: community module `ModderLords.Compat` (in the handshake, so both sides run it). One
  assembly, copied to both bin folders. Compiled against Coop's client assemblies from the local workshop subscription
  (`CoopRefDir`), `Private=false`: nothing of Coop's is redistributed.
- Coop discovers `IHandler` implementations by namespace prefix (`Coop.Core.Server.*` / `Coop.Core.Client.*`) and
  constructs them from its Autofac container when a session starts; the two handlers live in those namespaces (the
  same convention ModularSmithing2's adapter uses). Messages are `[ProtoContract]` types; Coop's type mapper picks up
  every ProtoContract in the AppDomain, wire id = hash of the full type name (never rename them).
- `McmBridge`: reflection over `MCM.Abstractions.BaseSettingsProvider.Instance.SettingsDefinitions` ->
  `SettingPropertyGroups` (recursive) -> `SettingProperties[].PropertyReference.Value`; snapshot = `id\tvalue` lines for
  bool/number/string/enum. Client applies through the same references so MCM's change notifications fire.
- Flow: client sends `NetworkRequestSettingsSnapshots` on `CampaignReady`; server answers per settings object; the
  submodule's 3-second tick re-captures on the server and broadcasts changed objects.
- `CoopProbe` checks every seam by name before any Coop type is JIT-compiled. Lesson learned the hard way:
  `Type.GetMethod(name)` on an overloaded method throws `AmbiguousMatchException`, and an exception escaping
  `OnSubModuleLoad` kills the engine with 0xE0434352 and no message. The hook now prints unhandled and (with
  `MODDERLORDS_HOOK_FIRSTCHANCE=1`) first-chance exceptions to stdout; the engine's stderr is not captured.
- Verified server side: module loads, handler armed, snapshots for `MCM_v5` and `HealOnKillSettings_v2` broadcast.
  Client side needs a player with the module installed.

## Compat Layer 1 (2026-09-02): server-only behaviours via recipes

- `AssemblyScan` now lists `CampaignBehaviorBase` and `MissionLogic/MissionBehavior/MissionNetwork` subclasses per mod
  (type definitions with base-type names, one level of mod-internal inheritance). Gotcha: `<Module>` and interfaces
  have a nil `BaseType`; reading it throws "Read out of bounds", so check `IsNil` first.
- `ProfileMod.ServerAuthoritative` + Mods tab column "Server-only logic" + "Behaviours" count column.
- `RecipeSet` (Core): `recipes.json` written into the bundled `ModderLords.Compat` folder by `LaunchSession.Prepare`
  (the server's copy through the junction). Schema: `Mods[] { Id, CampaignBehaviors[], MissionBehaviors[] }`.
- Adapter: client requests `NetworkRequestCompatRecipes` as soon as its handler is constructed (before the campaign
  loads); server replies with the JSON; `BehaviorGate` (Harmony, in the adapter DLL) prefixes each campaign behaviour's
  `RegisterEvents` with "skip when `ModInformation.IsClient`" and prefixes `Mission.AddMissionBehavior` to drop gated
  mission behaviour types on clients. A recipes.json shipped inside the client's module copy is applied first if present.
- CLI: `launch --profile <name>` runs the exact app path (overlay, config, recipes, engine).
- Verified server side with profile `layer1` (IG: 6 campaign behaviours, HealOnKill: 1 mission behaviour). Client side
  needs a player: expect `[ModderLords.Compat] server recipes: 6 campaign behaviour(s) gated ...` in
  `Configs\ModLogs\ModderLords.Compat-client.log`, then `RegisterEvents skipped on client: ...` lines.

## Supported tier (decided 2026-09-15)

Full generalisation is not the target. The tier the automatic fixes promise: **campaign behaviours plus settings**,
whose player actions call a behaviour method with a town, party or hero (ImprovedGarrisons is the archetype). Screen-
driven actions (view models, services), console commands, mission code and per-object mod state are **report only**
and need a per-mod adapter. Judge new analysis work by whether it raises the `trace-diff` numbers for tier-one mods
(step 8), not by TAOM coverage.

**Two-player gate.** Nothing that widens server-side player semantics (PlayerScope, "any player's" rewrites) proceeds
until this has passed once with a real second player in a **different clan**: profile1 with IG + TAOM, each player
owning a castle. (a) An IG per-castle setting set by player A applies only to A's castle on the server, B's unchanged;
(b) B's relay for A's town is rejected (`relay rejected ... does not own`); (c) state sync reaches both clients;
(d) `trace-diff` run with two clients. No second player was available on 2026-09-15.

**Fragility note.** The module reaches two Coop internals by reflection (`ResolvedMainHeroContext.ResolvedMainHero`,
the `Campaign.PlayerDefaultFaction` setter) and transpiles mod methods under Coop's gates. A Coop update can disable
compat silently (the probe pattern degrades to "disabled" in the log); check the compat logs after every Coop update.
Raising this with the Coop maintainers was deferred by the maintainer on 2026-09-15.

## Experimental compatibility behind a switch (2026-09-16)

The maintainer paused the per-mod work on 2026-09-16: steps 4–9 fit one installed mod shape (ImprovedGarrisons), which
is chasing specific solutions rather than general ones. Everything that changes how a mod's own code runs is now behind
**Server tab → Advanced → Experimental compatibility**, app-wide (`UiState.ExperimentalCompat`), **off by default**.

- **Off means off at launch, not only hidden.** `LaunchSession.ServerOnlyMods` is empty, so `recipes.json` carries no
  gates, postfix removals, player-check rewrites, relays or state sync, and `MODDERLORDS_TRACE_MODS` is ignored.
  Server-only logic ticks stay in the profile and a launch prints `experimental compatibility is off: Server-only logic
  for ... is ignored`; the Settings-sync refusal does not fire for ignored ticks.
- **Hidden when off:** the Server-only logic, Behaviours and Server verdict columns and the Behaviours… / Authority…
  buttons (`MainViewModel.ShowExperimentalCompat` = Host mode and the switch).
- **Unchanged (general):** server guards, MCM settings sync, battle-scene exclusion, the Compat column and records.
- Parked until the maintainer resumes it: Phase B (instance-field state through Coop's AutoSync) and the two-player gate.
  A first IG trace run on 2026-09-15 (Server-only logic on) showed gates holding (5,549 client skips, 0 runs), 8/8 relays
  confirmed and trace install in 233 ms; totals not written up.

## Authority classifier, step 1 (2026-09-14): Coop sink catalogue

Goal of the classifier: decide from IL, without game launches, which parts of a mod must run only on the server and
which stay with the player. Step 1 reads what Coop itself already decides.

- `ModderLords.Core.Compat.Authority.CoopSinks.ScanDll(GameInterface.dll)` walks Coop's Harmony prefixes (metadata +
  method bodies, nothing loaded). A bool prefix that calls `ModInformation.get_IsServer/get_IsClient` or
  `CallOriginalPolicy` is a gate: `ClientSkip` (exactly `return IsServer`), `Conditional` (branches, e.g.
  RecruitmentCampaignBehavior), `Policy`. A plain `return true` prefix is not a gate. Targets come from class- and
  method-level `[HarmonyPatch]` (type, name, `MethodType` getter/setter).
- Also collected: `AutoSyncRegistry.AddField/AddProperty` members (`SyncedMembers`), members named inside transpilers
  (`InterceptedMembers`, e.g. `Hero.VolunteerTypes`), and `TargetMethods()` yields. Iterator bodies live in nested
  `<Method>d__N` types and are attributed back to their method.
- `CoopSinks.Load` caches by SHA-256 in `%LOCALAPPDATA%\ModderLords\cache\coop-sinks-<hash>.json`.
- Coop v0.1.5: 49 gated behaviours, 360 blocked methods, 366 synced members, 39 intercepted, 164 TargetMethods targets.
- Harmony still runs postfixes when a prefix skips the original, so a mod postfix on a gated method runs on clients.
  Later steps (mod call graph, verdicts, generated recipes) build on this catalogue.

## Authority classifier, step 2 (2026-09-14): mod call graph and entry points

- `ModAnalysis.Analyse(mod)` → `ModCodeModel`: per method (id `Type::Name`, overloads share one) its calls, `ldftn`
  delegate targets, iterator/async bodies, field writes/reads, and `Opaque` when it invokes through reflection.
  `Implementers` + `Dispatch(callee)` resolve interface/virtual calls to mod implementations, which also covers
  DryIoc-style `Register<IFoo, Foo>` wiring (TAOM hooks).
- Roots, each with a `RootTrigger`:
  - `CampaignEvents.X.AddNonSerializedListener(this, handler)` → `Simulation`, or `Session` for load/session events.
  - `AddGameMenuOption` / `AddPlayerLine` / `AddDialogLine` delegates by slot: condition → `Query`, consequence →
    `PlayerInput` (a `null` delegate keeps its slot); `AddGameMenu`/`AddWaitGameMenu` init and tick → `Presentation`.
  - `[HarmonyPatch]` methods (kind from attribute or conventional name) and manual `harmony.Patch(original, prefix,
    postfix, …)` calls (slot gives the kind; RTSCamera applies 52 this way) → `Patch` with the resolved target.
  - Overrides by base-chain family: `…Model` → `Query`, `MissionLogic/MissionBehavior` → `Mission`, `MissionView`,
    screens and UI handlers → `Presentation`, `MBSubModuleBase` → `Lifecycle`; ViewModel `Execute*` and console commands
    → `PlayerInput`.
- A mod whose DLLs all fail to parse is `NotAnalysable` (KingdomPlus: corrupt metadata tables).
- Local probe results: TAOM 14,599 methods / 756 roots (Patch 197, Query 156, PlayerInput 103, Simulation 96);
  MyLittleWarband 29 roots; ImprovedGarrisons 88; RTSCamera 122 (52 manual patches).
- `ldftn` targets are kept in `MethodNode.Delegates`, not `Calls`: registering a handler is not running it. Array element
  writes (`holder.Array[i] = v`) count as writes to the most recently loaded array-typed field.

## Authority classifier, step 3 (2026-09-14): verdicts, report, CLI

- `AuthorityScan.Classify(model, coopCatalogue)` → `AuthorityReport` with one `RootVerdict` per entry point: verdict,
  one-line reason, evidence path (≤5 methods ending in the deciding effect), and `Opaque` when reflection is reached.
- The walk (BFS from the root, following interface/virtual dispatch and non-root delegates, capped at 5,000 methods)
  collects: calls Coop blocks on clients (plus `Apply*` on action classes Coop gates), writes to members Coop syncs or
  intercepts, other writes/setters on TaleWorlds/SandBox world types, `MBRandom`, UI/InformationManager calls, mod-owned
  field writes/reads, and authority checks (`IsAuthority`, `IsServer`, `ShouldDeferToHost`, …) in the root or one call
  below it.
- A patch takes its trigger from its target: `*_on_consequence` → PlayerInput, `*_on_condition` → Query, `*_on_init/tick`,
  VMs, views, screens → Presentation, `…Model` → Query, missions/agents/formations → Mission, otherwise unknown.
- Verdicts: `ServerOnly`, `NeedsStateSync` (also writes mod state that a PlayerInput/Presentation/Query root reads),
  `NeedsRelay` (player action that is blocked or changes world state locally), `LeakingPostfix` (postfix/finalizer on a
  method Coop skips on clients), `AlreadyHandled` (self-checks authority, or the target's behaviour is gated), `Both`,
  `Local`, `Review` (queries or mission code that change state, prefixes beside Coop's own gate, TargetMethods patches).
- CLI: `authority --mod ID [--all] [--json] [--out PATH]` prints action-needed verdicts with reasons and paths.
- Gotchas found building it: `System.Object::.ctor` must not dispatch (it fanned out to every constructor in the mod
  and made every walk swallow the whole assembly); record all of a method's effects before queueing its callees, or
  the visit cap can hide an authority check.
- Golden probe (installed mods): TAOM TroopWeight shed postfix → AlreadyHandled; CultureConversionBehavior handlers →
  AlreadyHandled; AllianceCampaignBehavior.StartAlliance postfix → LeakingPostfix. MyLittleWarband RecruitProductionPatch
  → AlreadyHandled, CustomTroopWagePatch → Both, RecruitPatch2 (recruit menu consequence writing Hero.VolunteerTypes)
  → NeedsRelay. KingdomPlus → not analysable.
- Shared state for NeedsStateSync = a mod field read by PlayerInput/Presentation/Query roots, not written by them, and
  written by at most max(5, roots/20) roots. Without the writer cap TAOM's logger fault flag made 56 handlers
  NeedsStateSync and hid the ServerOnly ones (1 → 27 once capped). `TaleWorlds.CampaignSystem.GameMenus.*` counts as
  presentation, so menu conditions setting `MenuCallbackArgs` are not Review.
- Only `callvirt` edges dispatch to overrides (`MethodNode.VirtualCalls`): `base.OnEndMission()` is a plain `call`, and
  dispatching it made a dozen TAOM mission behaviours inherit SiegeDismount's inventory writes. A patch on a
  `…ViewModel` (or an `Execute*` on a `…VM`) is player input — check it before the `…Model` → Query rule.
- Current results: TAOM ServerOnly 27, NeedsRelay 33, NeedsStateSync 3, LeakingPostfix 1, AlreadyHandled 61, Review 31
  (e.g. FieldCamp's hourly tick calls Coop-blocked roster/party actions — TAOM hides Make Camp in co-op for this reason).
  MyLittleWarband NeedsRelay 2, NeedsStateSync 1; ImprovedGarrisons ServerOnly 3, NeedsStateSync 7, NeedsRelay 17.

## Authority classifier, step 4 (2026-09-14): generated recipes and client gates

- Ticking **Server-only logic** now uses the code analysis (maintainer's choice: replace, no new UI). At launch
  `LaunchSession.WriteRecipes` loads Coop's catalogue (`CoopSinks.Load`, cached) and classifies each flagged mod; the
  console shows `authority <id>: …` and `recipe <id>: N handler(s) server-only, M leaking postfix(es) removed on clients`.
- `recipes.json` schema 2: per mod `Handlers[]` ("Type::Method") and `Unpatch[] { Target, Patch }`, filled from
  `ServerOnly`/`NeedsStateSync` Simulation/Session roots and `LeakingPostfix` roots. `CampaignBehaviors` stays empty for
  generated mods. `ClientSideBehaviors` still excludes a behaviour, including its lambda handlers (`Type+<>c`).
  NeedsRelay/Review are only reported in `Notes` ("… need a server relay (details: authority --mod X)").
- Fallback to whole-behaviour gating when Coop's GameInterface.dll is not found, analysis throws, or the mod is not
  analysable (KingdomPlus) — the recipe note says so.
- The server always loads the launcher's own `compat\ModderLords.Compat` and `compat\DedicatedServer.ModderLordsCompat`
  (`LaunchSession.WithCompat`), because `recipes.json` is written into that copy. Found 2026-09-14: on a PC that hosts
  and plays, the Nicks profile had ticked the copy Launch client installs into the game's Modules; the server junctioned
  that one, logged "no recipes.json", and gated nothing. A ticked copy is now swapped for the bundled one with a console
  note.
- Client module: `RecipeGates` (Harmony only, no game/Coop types) — `SkipHandlers` prefixes each handler with
  "return false on a client"; `RemovePostfixes` on a client detaches the named postfix/finalizer from its target via
  `Harmony.GetPatchInfo` + `Unpatch`. `BehaviorGate.Apply` calls both and appends counts to its log line.
- `RecipeGatesTests` run that file against real Harmony (Lib.Harmony 2.4.2 in the test project) with NoInlining targets:
  handler skipped on client only, postfix kept on server and removed on client, missing ids reported not thrown.

## Authority classifier, step 5 (2026-09-14): accuracy pass, UI kept on clients

Proven on ImprovedGarrisons (IG), whose player GUI sets per-castle options the server's ticks read.

- **Coop gate kinds split by the prefix's IL** (`CoopSinks.Classify`, catalogue schema 2): `Publishes` (calls
  `MessageBroker.Publish`: Coop sends its own request), `ClientDeny` (client branch returns false: cheats),
  `ClientLocal` (client branch returns true, nothing published: the change stays on that client). Debug-build IL
  (`stloc/ldloc` before the branch) is handled. On Coop 0.1.5: LeaveSettlementAction = Publishes,
  CheckCheatUsage = ClientDeny, ItemRoster.AddToCounts = ClientLocal.
- **Player actions** (`Decide`): a Publishes gate → AlreadyHandled, ClientDeny → Local ("players cannot use it");
  `GameStateManager.PushState`, `GameMenu.ActivateGameMenu/SwitchToMenu/ExitToLast`, `PlayerEncounter.LeaveEncounter/
  Finish` are navigation, not world changes; pushing `QuestsState` (which Coop never opens) → Review. TAOM's cheat
  commands no longer show as NeedsRelay.
- **New roots**: delegates created in ViewModel types ("UI callback": IG's slider/toggle lambdas) and delegates passed
  to `InquiryData`/`MultiSelectionInquiryData`/`TextInquiryData` ("popup callback": IG's fortify/escort choices).
- **New verdict `PlayerStateUnsynced`**: a player action writes mod state that Simulation/Session code reads (plumbing
  limit applies; view-model/screen fields excluded). A constructor filling in its own fields (directly or through its
  own setters) is initialisation, not a writer — otherwise every settings object with defaults looks like plumbing.
  IG: every Recruitment/Training/Guards option (e.g. `GarrisonSettings.MaxRecruitThreshold`).
- **Flags** on gated handlers (`RootVerdict.Flags`): `HostIsOnlyPlayer` (reads `Hero.MainHero`, `Clan.PlayerClan`,
  `MainParty`: on a Coop server that is the host; IG's `GetTownSettings` treats every player castle as an NPC's),
  `PopupLost` (shows an inquiry from server-only code, so no player sees it), `RegistersUI`.
- **UI-preserving gates** (maintainer's choice: keep UI, split the handler): a ServerOnly/NeedsStateSync handler that
  calls `CampaignGameStarter.AddGameMenu*/AddPlayerLine/AddDialogLine`, `ScreenManager.PushScreen/AddGlobalLayer` or
  creates a `GauntletLayer` is not gated. `HandlerSplit` descends its callees and puts the ones doing server work in
  `RootVerdict.GateInstead` when each is void, not an entry point, not a ctor/property, and not reached by
  player-facing code; otherwise the handler becomes Review and keeps running on clients. `RecipeSet` writes
  `GateInstead` into `Handlers[]` in place of the handler (no schema or client-module change: `SkipHandlers` gates any
  method id). Before this, IG's `GarrisonPartyBehavior.OnGameOpen` was skipped on clients, which removed the keep's
  "Improved Garrison" option and the garrison dialog lines; now only `OnGameStartSetAllIGParties` is skipped.
- Whole-behaviour fallback recipes say that menus they register are hidden on clients.
- CLI `authority` and the Authority window list flags and "gated instead" methods.

## Authority classifier, step 6 (2026-09-14): "is this the player's?" asks about any player on the server

Follows `docs/PLAYER-CONTEXT-SPIKE.md`. On a Coop server `Hero.MainHero`, `Clan.PlayerClan` and `MainParty` are the
host, so single-player mods treat every player's castle, clan or party as an NPC's.

- **Analysis** (`PlayerComparisonShapes`, counted per method in `ModAnalysis.ReadMethod` → `MethodNode.PlayerComparisons`):
  a player getter, optionally one property step (`Hero.Clan`, `Hero.PartyBelongedTo`, `MobileParty.Party/LeaderHero/
  ActualClan`, `PartyBase.MobileParty/LeaderHero`), then `ceq`, `beq`, `bne_un`, `op_Equality`, `op_Inequality`,
  `Equals` or `ReferenceEquals`. The other operand must already be on the stack (`owner == Hero.MainHero`); the
  getter-first form, null checks and Kingdom/MapFaction comparisons are left alone.
- `AuthorityReport.PlayerComparisonMethods`: such methods reached from Simulation, Session or Query (game model) roots.
- **Recipe schema 3**: `Mods[].PlayerComparisons[]`. Clients ignore it.
- **Server module** (`ServerSettingsHandler.Wire` → `ServerPlayerChecks.Apply`): `PlayerComparisonRewriter` (Harmony
  only, same shape table) transpiles each listed method, replacing the getter(+step)+comparison with a call answering
  "does this belong to any player?": Coop's public `IsPlayerHero` / `IsPlayerParty`; clans by "any hero in the clan
  is a player's" (Coop's `IsPlayerClan` is internal). Labels on replaced instructions move to the new call. Log line:
  `player checks: N method(s) now ask about any player (M comparison(s) rewritten), K not found`.
- `PlayerComparisonTests` runs the analysis and the real-Harmony rewrite on the same fixtures and checks the counts
  agree and the rewritten methods answer for any player.
- Module version 0.1.2 (the server module behaves differently; clients auto-install on Launch client).
- Installed mods (2026-09-14): ImprovedGarrisons 23 methods, TAOM 33, Europe1100 2, MyLittleWarband 0.

## Authority classifier, step 7 (2026-09-14): player actions relayed to the server

A player's click on a mod screen only changed their own game's copy of the mod (IG's "Order to patrol", every garrison
setting). The server now runs the same call as that player.

- **Relay point** (`AuthorityScan.RelayPoints`, `RootVerdict.RelayVia`, `AuthorityReport.RelayMethods`): for a
  PlayerInput root that is NeedsRelay or PlayerStateUnsynced, the shallowest mod method under it that does the work and
  can be sent: every parameter a primitive/string or a game object (Town, Settlement, Hero, Clan, MobileParty,
  CharacterObject, ItemObject, CultureObject), at least one of them something a player owns, one overload, an instance
  the server can find (static, static Instance/Current, or a CampaignBehaviorBase), not a lambda/ctor/accessor, and not
  a method that opens a popup or registers UI (the popup's own callback is the real action). Parameter types come from
  `MethodNode.ParameterTypes` (`SignatureTypeNames`). The button lambda itself reads screen state (IG's selected town);
  the method it calls takes the town.
- **Recipe schema 3** gains `Mods[].Relays[]`.
- **Client** (`RelayGates`, Harmony only): a prefix on each relay method sends `NetworkRelayInvoke { Method, Kinds,
  Values }` and still lets the player's game run it. Only the outermost relayed call on a thread is sent (IG's patrol
  order reaches `SellPrisoners`, also a relay point); a finalizer unwinds the count on throw. `RelayCodec` encodes
  primitives invariantly and game objects by StringId (a Town by its settlement's).
- **Server** (`ServerRelay`, from `ServerSettingsHandler`): allow-list = this server's own recipe; 5 requests/s per
  player; the sender's hero/party/clan from Coop's `IPlayerManager` + `IObjectManager`; every ownable argument must
  belong to that clan, and at least one must be named; runs on `GameThread.RunSafe` inside a scope that sets
  `Game.PlayerTroop` (so `Hero.MainHero`), the campaign's `MainParty` and `PlayerDefaultFaction` (`Clan.PlayerClan`,
  internal, via reflection) and Coop's resolved main hero, restoring them in `Dispose`. Replies `NetworkRelayResult`.
  Logs: client `relay sent <method> #n (args)` / `relay ran on the server: …` / `relay rejected by the server: … (why)`;
  server `relay ran|rejected <method> #n: <reason>`.
- **Coalescing** (`RelayCoalescer`, flushed every 0.25 s by `SyncSubModule` → `Bridge.RelayTick` →
  `ClientSettingsHandler.FlushRelays`, not the 3 s settings tick): live test
  2026-09-14 — dragging IG's max-upgrade-tier slider fired its setter 15 times in a second, the server's 5/s limit
  rejected the last value (10) and kept 5. Clients now hold relays per action + game-object arguments and send only the
  latest once it has been still for 300 ms; the server limit is 20/s as a backstop.
- Not relayed, and not detected as unsynced either: IG's template edits (`ExecuteAdd/Remove/Delete` change
  `TrainingTemplate` through dictionary calls, not field writes, and name no town). Template changes stay on the client.
- IG: 43 actions relayed through 33 methods (guard orders, recruitment/training/guard settings); TAOM and
  MyLittleWarband: none (their actions don't reach a sendable, ownable method).
- Module version 0.1.4 (0.1.3 was the first live test; bumped so Launch client replaces it with the coalescing build).

## Authority classifier, step 8 (2026-09-15): ground truth, verdicts against what actually ran

The review of steps 1–7 found the classifier tuned on two mods with nothing measuring its recall: its default verdict
(Local, "no world change found") hides false negatives, and in game a false negative is silent divergence, not a crash.
Step 8 measures it from one host + client session.

- **Launcher.** `MODDERLORDS_TRACE_MODS=TAOM;ImprovedGarrisons` at launch makes `LaunchSession.WriteRecipes` analyse those
  mods (ticked Server-only or not) and write every root's id into `Mods[].TraceRoots` (`RecipeSet`, additive; schema stays
  3). The recipe is what already reaches both sides, so no client-side switch is needed. Console line:
  `trace <id>: N entry point(s) counted on both sides`.
- **Module, both sides** (`RootTracer`, Harmony-only like `RecipeGates`, unit-tested with real Harmony): a prefix at
  `Priority.First` on every listed method counts the call and always returns true. A client call to a method that a
  handler gate skips is counted as a gated skip, not a run (the gate's prefix returns false after ours). Abstract,
  generic-definition and bodiless methods are skipped and counted as not found; per-method patch failures are counted, not
  thrown. Installed from `BehaviorGate.InstallTrace` (client: `Apply`; server: `ServerSettingsHandler.Wire`), log line
  `trace: N entry point(s) traced (M not found, K not patchable) in X ms`. Counters are keyed by `MethodBase`, no strings
  in the hot path; overloads merge into one id at snapshot time, as the classifier's ids do. Every 30 s (and at
  unload) `Bridge.TraceFlush` appends cumulative lines to
  `Documents\Mount and Blade II Bannerlord\Configs\ModLogs\ModderLords.Compat-trace-{client|server}.jsonl`:
  `{"t":123.4,"method":"Type::Method","ran":42,"gatedSkips":0,"firstSeen":3.1,"lastSeen":118.9}`; the last line per method
  wins. The 30 s verification line gains `trace: N method(s) fired, R run(s), S gated skip(s) (...)`. Counters are per game
  process: restart between measured runs.
- **CLI** `trace-diff --mod ID --trace-server FILE --trace-client FILE [--coop-client-log FILE] [--all] [--json] [--out PATH]`
  (`TraceDiff` in Core, pure). Each root is judged by the claim its verdict makes per side:

  | Verdict | Trigger | Server | Client |
  |---|---|---|---|
  | ServerOnly, NeedsStateSync | Simulation, Session | ran | not ran (`GateInstead` roots: the gated methods are judged instead) |
  | ServerOnly, NeedsStateSync | other | ran | no claim |
  | NeedsRelay | any | no claim | ran |
  | PlayerStateUnsynced | any | not ran | ran |
  | LeakingPostfix | Patch | ran | not ran (still firing = the unpatch did not hold) |
  | Both | any | ran | ran |
  | Local, AlreadyHandled, Review | any | no claim | no claim, except Simulation/Session code that ran on a client and never on the server |

  Outcomes: `Expected`, `FalseNegativeCandidate` (the trace contradicts the claim), `Untestable` (an action-needed
  verdict whose root never fired), `NeverExercised`. Per verdict: soundness = expected / (expected + contradicted), with
  untestable reported beside it, never folded in (TAOM's 756 roots will not all fire in one session). Traced methods that
  are not roots of the report are listed as stale. `--coop-client-log` buckets `Coop_client.log` error lines into 30 s
  windows (clock zero = the first line of `ModderLords.Compat-client.log` next to the client trace) and shows how many
  errors landed in the window a root last fired in, to line the residual ~3,700 errors per session up with the code
  that ran.
- Tests: `RootTracerTests` (real Harmony: counts, overload merge, unpatchable skipped, gated-skip split, jsonl round-trip
  into `TraceDiff.ParseTrace`), `TraceDiffTests` (one case per matrix cell, totals, error windows), recipe round-trip.
  Smoke: `trace-diff` on the installed ImprovedGarrisons with a fabricated trace produced the expected contradiction.
- Module version 0.1.5.
- **Live procedure:** set `MODDERLORDS_TRACE_MODS` (User env; remove it afterwards, the launch log marks inherited
  switches), host, join, play 15 min without field battles, copy `Coop_client.log` before relaunching, then run
  `trace-diff` and read the contradicted rows first. **Record the totals here.** They decide how much further analysis
  work is worth; the target tier is behaviour-plus-settings mods (ImprovedGarrisons), not TAOM's screen-driven actions.
- Not yet run live (2026-09-15).
## Field battles (2026-09-15): the server never chooses a scene without a terrain shader cache

Follow-up "every client accepts the server's battle scene choice" from 2026-09-14, resolved by reading Coop 0.1.5:
the server already builds the mission record once and sends it, scene name included, to every client, so the
remaining crash is the server picking a sackless scene. `BattleSceneCache.Scan` (Core) lists them from the selected
modules' `SceneObj` folders at launch (last module in load order wins), `RecipeSet.ExcludedBattleScenes` carries the
list, `BattleScenePick` (server module) postfixes Coop's `FieldBattleMissionInitializer.Create` and swaps a listed pick
for one of the same candidate tier via `BattleScenePolicy.Replace` (FNV-1a of the battle's terrain seed; tests in
`BattleSceneTests`). Module version 0.1.6. Full account in `docs/FIELD-BATTLE-TERRAIN.md`. Not yet run live.

## Authority classifier, step 9 (2026-09-15): static mod state follows the server

NeedsStateSync and PlayerStateUnsynced named the shared fields only in their reason text; nothing carried the values.
A relayed setting reached the server, but no other client ever saw it, and server-only code's results stayed invisible
to players.

- **Analysis.** `ModCodeModel.Fields` records, for every mod-defined field touched, whether it is static and its
  declared type (`IlReader.FieldFacts`, FieldDef signature decoded with `SignatureTypeNames`). `RootVerdict.SharedState`
  lists the fields behind a NeedsStateSync / PlayerStateUnsynced verdict in full ("Ns.Type.field").
  `AuthorityReport.SyncStateMembers` = those that are **static** and of a kind `ValueConverter` serialises (bool, number,
  string, or an enum the mod defines). Instance fields (IG's per-town settings objects) are named but not synced: they
  need Coop's AutoSync seam (phase 2, `docs/COMPAT-PLAN.md`).
- **Recipe** `Mods[].SyncState[]` (schema stays 3, additive; a mod with only SyncState is kept). CLI `authority` prints
  `state sync: N static field(s)` and per root `shared state: ... (synced | not syncable)`; the Authority window shows
  the fields in the details pane.
- **Module.** `RecipeStateSource : ISettingsSource` (CompatSync, no game/Coop/Log dependency, unit-tested) exposes the
  listed static fields as one settings object per declaring type, id `state:<Ns.Type>`, through the existing
  settings-sync channel: the server's 3 s diff broadcast and the join-time snapshot carry them unchanged
  (`NetworkSettingsSnapshot`), clients apply them (`ClientSettingsHandler.HandleSnapshot` / `ApplyPending`) and never
  send them back. The list reaches the server from its own recipes.json (`SettingsHints.Load`) and the client from the
  server's recipe (`BehaviorGate.Apply`, log `N static field(s) of mod state follow the server (...)`). Read-only,
  unsupported or missing fields are counted as "not found yet" and retried on the tick. Never persisted: the save is
  the truth. Module version 0.1.7.
- Installed mods (2026-09-15): ImprovedGarrisons 1 static field (ImprovedGarrisons.Main._configurationSettingsIsOpen), TAOM 0, MyLittleWarband 0. Nearly all shared state is instance fields on mod objects (IG per-town GarrisonSettings, MLW CustomUnit, TAOM registries), so this step ships the channel and proves the shape; the value needs the AutoSync seam for instance fields. Not yet run live.

## Compat verification logging (2026-09-02)

- `BehaviorGate` installs counting postfixes on each gated campaign behaviour's common event handlers
  (OnDailyTick / OnHourlyTick / OnWeeklyTick / settlement / raid / loaded) and counts invocations per (type, method).
- The submodule tick calls `Bridge.VerificationSummary()` every 30 s and logs it. A dedicated server counts up once a
  player joins and time runs; a client should stay at 0 for gated behaviours (proof the gate held). Empty server = 0
  because campaign time is paused with no players.
- Look in `Configs\ModLogs\ModderLords.Compat-server.log` and `-client.log` for
  `verification (server): GarrisonDailyBehavior.OnDailyTick=N ...` vs `verification (client): ... ran 0 times`.
