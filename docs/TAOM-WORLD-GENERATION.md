# TAOM dedicated-server world generation

## If you are hosting the mod

You do not need to read or run this document. It records a maintainer-only proof run, not a setup step. Do not copy
the game's `AssetPackages` into `DsAssetPackages`, do not replace files in the installed server, and do not run the
PowerShell/Python commands below as part of a normal install.

In the launcher build containing the automatic TAOM preparation, the Host button handles both cases. Install Bannerlord
Coop and the TAOM mod pack normally, select **TAOM**, **TAOM_Map** and **LOTRLOME_Armory**, and leave the save name
empty for a new campaign. The launcher creates the campaign in its private data folder, then starts a separate server
process against that save. To use an existing campaign, select it in the Saves tab or use **Import client save** when it
only exists in the game's own save folder. No user action involving `AssetPackages` or `DsAssetPackages` is required.

The missing-animation lines that appear in the proof console are headless diagnostic warnings. They do not mean that a
user should fetch or copy an asset folder, and they do not prove that every client animation or battle visual works on
the server. They are retained in the diagnostic log so unsupported content is visible to maintainers.

The isolated experiment generated two distinct worlds directly in the dedicated server,
then a separate server process loaded the first exact automatic save and reached `SERVING` on TAOM's map.
This does not test client joining or gameplay synchronization. The vanilla checkpoint is
[PR #44](https://github.com/PlueRyvius/ModderLords/pull/44).

## What was required

1. Include **LOTRLOME_Armory**, not just TAOM and TAOM_Map. It defines the `sauron` race.
   Without it, character XML loading throws at `lord_1_17`; Bannerlord swallows that
   exception and leaves later templates uninitialized. The apparent Ghilman hero loop
   was a downstream symptom of incomplete module data.
2. Supply simulation assets in `DsAssetPackages`. Loading whole client TPACs crashed the
   headless engine. The diagnostic converter retains Skeleton, SkeletalAnimation,
   AnimationClip and PhysicsShape assets, with original opaque metadata and payload
   bytes. It excludes rendering assets and validates every retained payload after writing.
   The tested installation produced nine packages totaling 78,439,919 bytes. TAOM itself
   contributed no matching assets; Armory and TAOM_Map supplied them. Missing-animation
   warnings still appear, so this is not a claim that all client assets are supported.

   Those warnings were measured on 2026-09-11 and are **not** evidence that the projection
   strips something it should keep. Of 130 distinct names an engine run complained about,
   **120 exist as an asset record in no installed package at all** — not in the client
   `AssetPackages` for Native, SandBox, TAOM, TAOM_Map, TAOM.Dependencies or
   LOTRLOME_Armory, and not in the server's own `Native\DsAssetPackages`. They are
   dangling references in `LOTRLOME_Armory\ModuleData\action_sets.xml`, which the server
   receives intact. The remaining 10 all *are* simulation types the projection retains,
   and are present where the server can reach them: 9 in the server's stock Native, and
   `warg_attack_stand` as an AnimationClip in Armory's `pack7.tpac`, which is projected.
   There is nothing to widen `SimulationTypes` to include.

   `MissingAssetProbe` (in `ModderLords.Core.Tests`) reproduces this against a real install:
   set `MODDERLORDS_PROBE_LOG` to an `rgl_log_errors_*.txt` and `MODDERLORDS_PROBE_ROOTS`
   to a `;`-separated list of module roots. Note that a byte-substring search over a `.tpac`
   will wrongly report these names as present — they appear inside other records as
   dependency references. The probe parses the resource table instead.

   What the warnings *did* cost was the run itself: the engine wrote a full crash report
   (~540 MB) for each one, 227 of them in a single 15-minute attempt, which starved world
   creation before it armed. `HeadlessDebugManager.ReleaseNativeAssertions` now calls
   `SetCreateDumpOnWarnings(false)` and `SetCrashOnWarnings(false)`. It deliberately does
   **not** disable dump generation outright: a genuine native crash must still leave a dump.
   `Preflight.RotateCrashDirs` caps the folder instead.
3. Give the creation-only finalization prefix priority over TAOM's character-creation
   prefix, and keep `IsLoaded` false until the creation process saves and exits. The
   ordinary server's bootstrap save must not be mistaken for the generated world.
4. Use TAOM's actual map. The official headless scene implementation `A.G` otherwise
   reads its own vanilla `Main_map` and hardcodes vanilla bounds. The diagnostic map
   projection removes client entities and rendered-terrain XML, while retaining the
   original navigation, binary terrain, flora, atmosphere and paths. An explicitly enabled
   runtime patch supplies bounds from TAOM's border entities and terrain dimensions.
   It verifies the navmesh hash and target signatures and applies only to the supported
   `DedicatedServer.Core` headless scene type. Creation and reload both need this map.

The runtime still uses the official headless scene initialization. No installed game,
server or Coop DLL is rewritten. The map files are projected into a private overlay for
the TAOM launch and reused by later starts; vanilla launches do not create this projection.
Original saves, profiles and distance caches are preserved.

## Reproduce in a fresh workspace

Use PowerShell 7, the .NET SDK and Python 3.9 or newer. Run from this checkout; **do not
build its ordinary module output**, which may be linked into an installed server.
`Setup` builds an isolated source copy instead. Supply `-InstalledServer` and
`-InstalledGame` if their paths differ from the harness defaults.

```powershell
$work = 'D:\Design\Bannerlord Mods\_taom-proof-new'
$python = 'C:\path\to\python.exe'
pwsh -File tools/spike/Run-Vanilla.ps1 -Stage Setup -Recipe TaomFull -Workspace $work
pwsh -File tools/spike/Prepare-TaomHeadless.ps1 -Workspace $work -Python $python
# Use the exact Main_map path printed by Prepare-TaomHeadless:
$scene = 'D:\Design\Bannerlord Mods\_taom-proof-new\headless-prepared-TIMESTAMP\Main_map'
pwsh -File tools/spike/Run-Vanilla.ps1 -Stage All -Recipe TaomFull `
    -Workspace $work -HeadlessScene $scene -Name taom_fresh_001 -NoBuild
```

`All` runs a vanilla load control, fresh TAOM creation and exact reload, a second fresh
creation and reload, another vanilla control, and creation timeout cleanup. Default
timeout is 900 seconds. The ports are 7299/4299, checked for availability before launch;
server discovery is disabled and each run uses a private password. Only experiment-owned
processes are cleaned up. Existing output names are rejected before launch.

Individual `Create` and `Reload` stages accept the same options. `Control` always uses
vanilla modules and ignores `-HeadlessScene`. Do not use the reduced `Taom` recipe as a
full TAOM proof. Do not load a TAOM save without its map and module recipe.

The underlying CLI selection is:

```text
--mods TAOM.Dependencies:DependencyOnly,LOTRLOME_Armory:Run,TAOM:Run,TAOM_Map:Run
--game <workspace>\Game --data-dir <workspace>\data --mod-distance-cache
--create-world <unique-name> --create-world-timeout 900 --world-log <fresh-run-log>
```

The harness additionally stages/restores the headless map and sets
`MODDERLORDS_HEADLESS_MAP` to its validated metadata file for that child process.

## Evidence and limits

Tested Native version: `v1.4.8.118999`; CoopNightly `v0.1.5.0`; TAOM/TAOM_Map
`v2.0.27.0`; Armory/TAOM.Dependencies `v2.0.26.0`.

- Fresh campaign: 236 clans and 988 settlements; actual scene type `A.G`.
- TAOM navmesh CRC: **3101457840**, versus vanilla **1465536726**.
- Bounds: `(62,0)` to `(1700,1550)`, maximum height `646.01`, terrain `1600 x 1600`.
- `taom_real_map_14`: 10,859,133-byte save, exit 11; separate exact reload reached
  `SERVING` and exited 0. The earlier `taom_all_assets_13` used vanilla geometry and is
  only an intermediate result, despite passing save/reload checks.
- `taom_real_map_15`: 10,870,050-byte save, exit 11; separate exact reload reached
  `SERVING` and exited 0. Its world ID `eu5K0rSdDdjR` differs from the first world's
  `olFFXMs0JSOp`; the preserved save hashes also differ.
- The automatic launcher path was then exercised twice with fresh names:
  `auto_taom_20260910e` and `auto_taom_20260910f` each reached `map-ready`, wrote a
  new save and exited 11; separate reloads of both reached `SERVING` and exited 0.
  Their logs and saves are retained under the local `_taom-world-proof` workspace,
  with the machine's client `AssetPackages` absent from the server overlay.
- Six Python tests cover opaque asset preservation, bad package rejection, map projection,
  bounds extraction and refusal to overwrite outputs. The 328 core tests pass.
- Live existing-name and create/load conflict checks both returned exit 2 with the server
  configuration unchanged.
- The final vanilla control reached `SERVING` with vanilla geometry and both experimental
  patches disabled. Creation timeout exited 12 without a save. Corrupted map metadata was
  rejected before creation; all five replaced map files were restored byte-for-byte and
  no experiment engine remained running.

Per-attempt evidence lives under `<workspace>\runs`: `arguments.json`, `stdout.log`,
`worldcreate.log`, `result.json`, save headers and hashes, and preserved generated saves.
Reload records its input hash before the host can save again on shutdown. Generated
game data and extracted game assets are local artifacts, not included in this repository.

The projection and runtime bounds patch remain diagnostic. Multiplayer clients, battles,
long-running simulation and distribution of a production server package are untested.
Early native failures have exit codes and engine logs; they do not have captured dumps.

## Comparing a release run with the maintainer proof

When the packaged launcher stays on **Creating TAOM World**, use the comparison tool. It first takes a read-only
snapshot of the release, profile, installed server, selected mod sources, current data and running processes. It then
creates a new isolated session under `-Workspace`; the live server, its ports, saves, profiles and overlay are never
stopped, deleted or rewritten.

```powershell
pwsh -File tools/diagnostics/Compare-ReleaseRun.ps1 -Stage Capture
pwsh -File tools/diagnostics/Compare-ReleaseRun.ps1 -Stage All -Case ExactUser `
    -SaveName release094_exact -EnginePort 7368 -JoinPort 4368
pwsh -File tools/diagnostics/Compare-ReleaseRun.ps1 -Stage All -Case ComputedOrder `
    -SaveName release094_computed -EnginePort 7369 -JoinPort 4369
```

`-SaveName` is also accepted as the legacy `-Name` alias. Omit `-EnginePort` and `-JoinPort` to have the tool pick
unused private ports; supplying them makes the run reproducible and fails early if either is occupied.

`ExactUser` copies the selected profile data and preserves its configured manual order. `ComputedOrder` holds every
other input constant and lets the module manifests choose the order. `CleanState` omits existing saves, crash-loop
markers and the generated overlay. `Baseline`/`Version` use the copies under `-BaselineWorkspace` when available.
`ExistingSave` reloads `-ExistingSaveName` from the copied server data. `Package` records release hashes without
starting the engine. `Vanilla` is an optional stock-module creation/reload control. `Version` is marked unsupported
when `-BaselineWorkspace` does not contain both pinned `DedicatedServer` and `Game` trees; it never silently substitutes
the installed version. `-Stage Matrix` runs all seven TAOM cases and writes `comparison.json`. The `Package` case also
compares the release hook, server compatibility DLL, recipes and compatibility database with the corresponding
baseline payload when it is present.

Each creation attempt uses a unique save, private Steam visibility, alternate ports, a 15-minute world-creation
deadline and a 45-second cleanup margin. It records `stdout.log`, `stderr.log`, `worldcreate.log`, the engine logs,
the creation manifest, process information, module order, input hashes, save hash and the exact reload result. Native
exit codes remain visible. A failed case is retained so the matrix can continue; the script exits 1 if any case fails.

The desktop launcher writes the same creation manifest and three capped logs to
`%LOCALAPPDATA%\ModderLords\logs\creation-<session>.{json,log,stdout.log,stderr.log}` and shows the current phase and
elapsed time beside its status. The release payload can be selected for a diagnostic CLI run with `--bundle-root`;
ordinary launches keep their existing lookup defaults.

The capture stage may run while the desktop launcher is open. Engine stages refuse to start while any
`ModderLords.exe` is running because Bannerlord's native crash watchdog uses a global named event; concurrent engines
can otherwise fail before the first world-creation phase with `0xC0000409`. Close the launcher (and any server it owns)
before `Create`, `Reload` or `Matrix`; the tool never closes it for you.
