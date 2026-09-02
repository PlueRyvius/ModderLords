# Modular Bannerlords Coop

A launcher for the **Bannerlord Coop dedicated server** that lets you run it with community mods,
using the pristine official server package and the mods where they already live on disk.
It replaces the need for third-party wrappers that patch files inside the Steam workshop folder.

Status: Phase 0 complete (direct engine launch verified). See `docs/` for the design.

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

## CLI (Phase 0)

```
dotnet run --project src/ModularCoop.Cli -- launch [--root <DedicatedServer>] [--modules A,B,C] [--save NAME] [--port 7210] [--region EU] [--dry-run] [--quiet-engine] [--stop-after SECONDS]
```

## Licensing note

Bannerlord Coop is source-available, not open source. This project launches its binaries and relies only on their public
contracts (command line, config files, module manifests). No Coop or third-party launcher code is included.
