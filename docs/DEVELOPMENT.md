# Modular Bannerlords Coop: developer notes

A launcher for the **Bannerlord Coop dedicated server** that lets you run it with community mods,
using the pristine official server package and the mods where they already live on disk.
It replaces the need for third-party wrappers that patch files inside the Steam workshop folder.

Status: Phase 1 complete (community mods load on the pristine server through junctions + a resolver hook).

## What it does

- Launches the official Coop dedicated server engine directly with your own module list and load order.
- Never copies mods into the server. Mods are exposed to the engine through NTFS junctions and a small
  rewritten copy of each `SubModule.xml` kept in `%LOCALAPPDATA%\ModularCoop`.
- Owns `server-config.json` rendering instead of regex-editing it.
- Warns about save/mod version drift but never blocks a launch and never rewrites save bytes.
- Streams the engine console with classification (module load, server, Coop, warnings, errors, milestones).
- Exports the exact client mod list players need (Coop requires an exact id+version match for community mods).

## Layout

| Project | Purpose |
|---|---|
| `src/ModularCoop.Core` | Pure logic: paths, launch plan, engine process, save prep, log classifier |
| `src/ModularCoop.Cli` | Command-line driver (used for spikes and headless operation) |
| `src/ModularCoop.App` | WPF desktop app (Phase 2) |
| `src/ModularCoop.Hook` | Optional `DOTNET_STARTUP_HOOKS` assembly, only if Phase 1 shows the engine cannot resolve mod dependencies on its own |
| `tests/ModularCoop.Core.Tests` | xUnit tests |

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

- **Overlay** (`ModularCoop.Core.Overlay`): a mod that already ships `bin\Win64_Shipping_Server` and no client-only tags gets a
  direct junction. Everything else gets a shadow folder in `%LOCALAPPDATA%\ModularCoop\overlay\<profile>\<Id>` holding a
  rewritten `SubModule.xml` (id and version untouched), `bin\Win64_Shipping_Server` junctioned to the mod's client bin,
  and every other folder junctioned as-is. Roles: `Run` strips the `DedicatedServerType`/`IsNoRenderModeElement` tags,
  `DependencyOnly` removes the submodules (MCM's headless settings core is allow-listed), `AsShipped` changes nothing.
- **Hook** (`ModularCoop.Hook`, loaded via `DOTNET_STARTUP_HOOKS`): the engine only probes its own bin for referenced
  assemblies, so a last-resort `AssemblyResolve` handler searches `MODULARCOOP_SEARCH_DIRS` (Coop's server bin first,
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
dotnet run --project src/ModularCoop.Cli -- catalog
dotnet run --project src/ModularCoop.Cli -- sync   --mods Bannerlord.Harmony:DependencyOnly,ModularSmithing2,HealOnKill
dotnet run --project src/ModularCoop.Cli -- launch --mods Bannerlord.Harmony:DependencyOnly,ModularSmithing2,HealOnKill --save "29 August 26"
dotnet run --project src/ModularCoop.Cli -- sync   --remove-all
```

## Phase 2 (2026-09-02): profiles, config rendering, desktop app

- `ModularCoop.App` (`ModularBannerlordsCoop.exe`): Mods tab (enable, role, order, load-order preview, messages), Saves tab
  (save headers read from the `.sav` files, diff against the profile, never blocks), Server tab (rendered into
  `server-config.json` with a backup under `config-backups`), Console tab (classified, filterable, searchable, command
  entry, clean Stop over stdin), Players tab (mod list for players + "check my client" against `LauncherData.xml`).
- Profiles live in `%LOCALAPPDATA%\ModularCoop\profiles\<name>.json`; each profile has its own overlay folder.
- `LaunchSession` is the single path from profile to running engine, shared by the app and the CLI.
- Launch logs: `%LOCALAPPDATA%\ModularCoop\logs\launch-<timestamp>.log`.

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
- Release packaging: `scripts\package-release.ps1 -Version x.y.z` -> `artifacts\ModularBannerlordsCoop-x.y.z.zip`.
