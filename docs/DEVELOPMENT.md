# ModderLords: developer notes

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
| `src/ModderLords.Core` | Pure logic: paths, launch plan, engine process, save prep, log classifier |
| `src/ModderLords.Cli` | Command-line driver (used for spikes and headless operation) |
| `src/ModderLords.App` | WPF desktop app (Phase 2) |
| `src/ModderLords.Hook` | Optional `DOTNET_STARTUP_HOOKS` assembly, only if Phase 1 shows the engine cannot resolve mod dependencies on its own |
| `tests/ModderLords.Core.Tests` | xUnit tests |

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
  then each mod's bins, then the game client bin). No game code is patched.
- **Load order** (`LoadOrder`): official host sequence `Native, SandBoxCore, Sandbox, <community>, <Coop id>, DedicatedServer.Windows`,
  community block sorted by BUTR's `ModuleSorter`, Harmony first, StoryMode/CustomBattle/BirthAndDeath treated as
  satisfied (they never exist on a server).

Gotchas found on the way:

- The workshop build's server Coop folder carries the id **`CoopNightly`** (empty submodule list; the server core loads
  Coop itself). The token must use the id from `SubModule.xml`, not the folder name.
- The engine's `AssemblyLoader` eagerly tries every referenced assembly by bare file name and logs
  `Messagebox [ERROR] ... Cannot load:` for each miss before resolving it properly. The console classifies those as warnings.
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

## Compat verification logging (2026-09-02)

- `BehaviorGate` installs counting postfixes on each gated campaign behaviour's common event handlers
  (OnDailyTick / OnHourlyTick / OnWeeklyTick / settlement / raid / loaded) and counts invocations per (type, method).
- The submodule tick calls `Bridge.VerificationSummary()` every 30 s and logs it. A dedicated server counts up once a
  player joins and time runs; a client should stay at 0 for gated behaviours (proof the gate held). Empty server = 0
  because campaign time is paused with no players.
- Look in `Configs\ModLogs\ModderLords.Compat-server.log` and `-client.log` for
  `verification (server): GarrisonDailyBehavior.OnDailyTick=N ...` vs `verification (client): ... ran 0 times`.
