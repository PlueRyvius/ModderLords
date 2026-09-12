# Field battles crash the client — handoff

Status as of 2026-09-12. Everything else on the TAOM path works: world generation, serving, joining,
village-raid battles. This is the one remaining failure, and it is ours to fix.

## The failure

A client entering a **field battle** dies in:

```
ERROR: rglGPU_device::create_texture_array failed at d3d_device_->CreateTexture2D!
The parameter is incorrect.
```

with a pure `TaleWorlds.Native` stack. The server is unaffected — it logs
`[BattleSync] Using "FieldBattleMissionInitializer"`, builds troop reserves, admits the controller to the
instance, and keeps pulsing normally.

## What is already ruled out

Do not re-investigate these; each was tested and eliminated.

| Hypothesis | Why it is wrong |
|---|---|
| Missing world-map grid textures | Added (`worldmap_battle_scene_grid` + colorgrade). Projection verified rebuilt — `pack4.tpac`, 276 KB, 1043 assets. Crash unchanged. |
| Missing shader cache | The village raid that **worked** had 120 `Missing shader from sack` lines; the field battle that crashed had 21. More misses in the working case. |
| Wrong battle scene chosen | `battle_terrain_a` is a legitimate TAOM entry — its own `sp_battle_scenes.xml` maps `map_indices="0, 2, 136, 118"` to it. The server reads that file; selection works. |
| A TAOM client-side asset fault | **The same field battle works in single-player.** Confirmed by the user. It is coop-specific. |
| Coop's ObjectManager id failures | 10,827 of them stream through a completely healthy session. Noise, not cause. |
| The map is wrong | Both phases read navmesh CRC 3101457840 in the working runs. `MapIdentityCheck` guards this now. |

## The leading hypothesis

Field battles derive terrain from the campaign map; village raids load a pre-built named scene. That is
exactly the axis the failure splits on.

In single-player the client derives battle terrain from its own complete campaign map. In coop the server is
authoritative — and the server's map scene is the **stripped** one. `HeadlessMapProjection` drops
`terrain`, `layers`, `nodes` and `outer_mesh` (the landscape renderer deadlocks server-side; TAOM's own
tooling strips identically, see `New-StrippedMapScene.ps1` in their host pack).

So the server can answer "which scene" correctly, but whatever it reports about **terrain layers** at a map
position comes from a scene that no longer has any. The client is then asked to build a texture array whose
slices do not agree, and D3D refuses.

This is a hypothesis, not a finding. It has not been instrumented.

## Recommended path

**1. Prove it before building anything.** Three guesses in this investigation looked equally plausible and
were wrong; the cheap confirmations are what moved it forward each time.

Find what the server actually reports for terrain at the battle position. `DedicatedServer.Core`'s name heap
contains `GetTerrainTypeAtPosition`, `TaleWorlds.CampaignSystem.Map.IMapScene.GetEnvironmentTerrainTypes`,
`GetEnvironmentTerrainTypesCount`, `GetMapPatchAtPosition`, `GetHeightAtPoint`. Patch or log those in
`ModderLords.Compat` on a field-battle start and compare against the same calls on a single-player client at
the same position. If they disagree, the hypothesis holds and the disagreement names the fix.

**2. If confirmed, the fix is most likely to restore terrain data the stripped scene lost** — not to un-strip
the scene (that reintroduces the deadlock the stripping exists to avoid), but to supply terrain-type answers
from `terrain.bin`, which *is* projected and complete (56 MB, byte-identical to source). The server has the
data; the scene object it queries no longer exposes it.

**3. Keep it general.** The rule to aim for is "a mod that replaces the campaign map keeps its terrain
queries answerable headlessly" — not anything TAOM-shaped. The world-map grid textures went in by naming
convention rather than a per-mod offset table for the same reason.

## Useful assets

- **`Mapping.txt`** — a full deobfuscation map for `DedicatedServer.Core`, shipped in TAOM's host pack
  (`Artifacts/TAOM-Coop-Wrapper/*/Mapping.txt`). This is how `A.G` was identified as
  `DedicatedServerMapSceneCreator` and `A.f` as `ConsoleDebugManager`. Invaluable; the core is otherwise
  obfuscated with encrypted string literals.
- **`MissingAssetProbe`** / `HeadlessAssetProjection.Inventory` — what a package actually contains
  (`MODDERLORDS_PROBE_LOG`, `MODDERLORDS_PROBE_ROOTS`).
- **`ModuleDependencyProbe`** — what a module references (`MODDERLORDS_DEPS`). Note its limit: it answers
  assembly references only. It reported `TAOM.CoopCompat` as independent of `DedicatedServer.Core`, which was
  true and still misleading — that module's dependency is a hash-verified runtime handshake.
- The launch log now records everything, and `<DataDir>\logs\worldcreate-*.log` holds the creation phases.

## What not to do

- **Do not enable `TAOM.CoopCompat`.** It waits for a hash-qualified wrapper to register pre-save and
  pre-serving guards during `OnSubModuleLoad`; on the stock server nothing does, and it refuses by waiting —
  a healthy-looking 64 fps server that never serves. Recorded in `compat-db.json`.
- **Do not un-strip the server map scene.** The stripping is load-bearing.
- **Do not disable dump generation outright.** Warnings-only, or a real crash leaves no evidence.

## Open, unrelated

- **The wrong-map incident.** One run generated on TAOM's map and served on the stock one. Never reproduced,
  cause never found; `MapIdentityCheck` catches it if it recurs.
- **Profile persistence.** `profile2.json` on disk was last written 2026-09-02 with `"Mods": []` despite
  `Launch()` calling `ProfileStore.Save(Profile)` every launch. Not investigated.
- **Distance cache default.** `UseModDistanceCache` defaults false, so a server runs vanilla settlement
  distances even when a mod ships its own — TAOM_Map does. TAOM's tooling removes the vanilla cache to stop it
  shadowing. Suggested rule: if a selected mod ships `DistanceCaches`, use it. Changes a default, so it needs
  a decision.
- **World creation should use a reduced module set.** A sync/compat layer has no business loading while a
  world is being generated. Caveat to check first: Coop validates module lists between peers and the save
  records which modules made it, so dropping one during creation may produce a save whose header disagrees
  with the serving order.
