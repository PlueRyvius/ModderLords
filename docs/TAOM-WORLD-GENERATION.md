# TAOM dedicated-server world generation

## If you are hosting the mod

You do not need to read or run this document. It records a maintainer-only proof run, not a setup step. Do not copy
the game's `AssetPackages` into `DsAssetPackages`, do not replace files in the installed server, and do not run the
PowerShell/Python commands below as part of a normal install.

In release `v0.9.2`, the Host button is one-click for a vanilla fresh world and for loading an existing compatible
save. A TAOM host should install Bannerlord Coop and the TAOM mod pack normally, then select a TAOM save in the Saves
tab. If the save only exists in the game's own save folder, use **Import client save** first. Fresh TAOM generation is
not wired into the release launcher yet; the commands in this file are for reproducing the engineering proof only.

The missing-animation lines that appear in the proof console are headless diagnostic warnings. They do not mean that a
user should fetch or copy an asset folder, and they do not prove that every client animation or battle visual works on
the server. They are retained in the diagnostic log so unsupported content is visible to maintainers.

The isolated experiment generated two distinct worlds directly in the dedicated server,
then separate server processes loaded each exact save and reached `SERVING` on TAOM's map.
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
server or Coop DLL is rewritten. The map files are replaced only inside the diagnostic
package for a run, with per-run backups restored after process cleanup. Normal launches
do not enable the map experiment. Original saves, profiles and distance caches are preserved.

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
