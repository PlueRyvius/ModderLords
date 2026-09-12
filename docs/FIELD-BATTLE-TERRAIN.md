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

## CONFIRMED 2026-09-12: the stripped server scene answers terrain queries with nothing

Measured with `TerrainProbe` (below), 2304 grid samples on TAOM's map, server against single-player client.
Same map on both sides: identical borders `62,0..1700,1550`, identical terrain size `1600,1600`, identical
navmesh face count `19396`.

What **agrees** on all 2304 samples — the control that makes the rest meaningful:

| Query | Result |
|---|---|
| `GetTerrainTypeAtPosition` | identical everywhere |
| `GetFaceIndex` (index, group) | identical everywhere |
| `GetEnvironmentTerrainTypesCount`'s `out currentPositionTerrainType` | identical everywhere |

What **disagrees**, and how:

| Query | Server (stripped) | Client (full) |
|---|---|---|
| `GetEnvironmentTerrainTypesCount` return list | **empty on all 2304** | 49 entries on all 2304 |
| `GetMapPatchAtPosition().sceneIndex` | **0 on all 2304** | 181 distinct values (123, 124, 125, 126, 130, 255, …) |
| `GetMapPatchAtPosition().normalizedCoordinates` | `0,0` (98–99% of rows) | real coordinates |
| `GetHeightAtPoint` height | **0 on all 2304** | real heights; 144 genuine zeros |
| `GetHeightAtPoint` return | **`true` on all 2304** | `true` on 2160 |
| `GetSnowAmountAtPosition` | differs on 30% | — |
| `GetRainAmountAtPosition` | differs on 22% | — |
| `GetFaceIndex().FaceIslandIndex` | `-1` in most rows | real island ids |

The split is exactly along the projection boundary. `navmesh.bin` is projected intact, so every answer derived
from the navmesh matches. `terrain`, `layers` and `nodes` are stripped, so every answer derived from terrain is
empty or zero — **and the server reports success while returning it.** `GetHeightAtPoint` returns `true` with a
height of 0 on every single sample. Nothing throws, nothing logs, and the first thing that notices is D3D.

Two of these are the direct candidates for `create_texture_array`:

1. **The environment terrain-type list is empty.** The client gets a 49-entry neighbourhood sample; the server
   gets none. A battle composites one texture-array slice per terrain type in that neighbourhood. Zero types is
   a zero-length array, and `CreateTexture2D` with an array size of 0 fails with exactly "The parameter is
   incorrect."
2. **Every map patch reports `sceneIndex` 0.** On the client the same 2304 positions produce 181 distinct
   scene indices. This is the patch-to-scene selector, and on the server it is a constant.

### Measured 2026-09-12, second run: the battle does not ask for the terrain-type list

With the stub spike correctly targeted (see the implementation note below — the first attempt patched methods
nothing calls), a full field battle produced:

```
05:05:10.925  terrain stub: first height-of-zero success reported as a failure instead
05:05:10.930  [Coop] [BattleSync] Using "FieldBattleMissionInitializer" for battle mission initializer
...
              terrain stub: filled 0 empty type list(s), corrected 2 false height success(es)
```

`GetHeightAtPoint` is called **five milliseconds before** the field-battle initializer — it is squarely on the
battle path, and the patch demonstrably works. `GetEnvironmentTerrainTypesCount` was called **zero times** in the
entire run.

So the empty list from *that* member is not what the battle consumes, and candidate 1 above is not disproved but
mis-aimed: `IMapScene` has a second, separate member, `GetEnvironmentTerrainTypes` (no "Count"), which the probe
never sampled and the spike never patched. `A.G` re-implements that one too.

The lesson is a method, not a fact: measuring one member at a time and inferring from silence has now cost two
runs. `MapSceneCallCensus` (`MODDERLORDS_MAPSCENE_CENSUS=1`) counts calls to every terrain-related `IMapScene`
member at once and reports which were never called, so the next run names the consumer instead of guessing it. It
changes no answers, so it can run alongside a spike.

### Measured 2026-09-12, third run: what a field battle actually asks the map scene

The census, over a full field battle:

```
map scene census: GetSiegeCampFrames=221 GetHeightAtPoint=2 GetSnowAmountAtPosition=1
                  GetAtmosphereStates=1 GetMapPatchAtPosition=1 GetRainAmountAtPosition=1;
                  never called: GetEnvironmentTerrainTypes, GetEnvironmentTerrainTypesCount,
                  GetFaceTerrainType, GetGroundNormal, GetTerrainHeightAndNormal, GetTerrainSize, ...
```

**The environment terrain-type list is never asked for, in either form.** Candidate 1 is dead — measured this
time, not inferred from a mis-aimed patch. `GetSiegeCampFrames` is campaign-AI background noise, not the battle.

What the battle asks, once each, at mission start:

| Member | Server's answer (measured) |
|---|---|
| **`GetMapPatchAtPosition`** | **`sceneIndex` 0 everywhere; the client produces 181 distinct values** |
| `GetHeightAtPoint` (×2) | 0 everywhere, returning `true` |
| `GetSnowAmountAtPosition` | differs from the client on 30% of samples |
| `GetRainAmountAtPosition` | differs on 22% |
| `GetAtmosphereStates` | never probed |

So `GetMapPatchAtPosition` is now the candidate, and it is the *only* one of the battle's queries still returning
garbage: the stub already corrected the height answers in this very run and the crash was unchanged.

This reverses an earlier judgement recorded above — `sceneIndex` was set aside as "probably a symptom" because
scene *selection* is in the ruled-out table. Selection being right does not mean the patch data is unused.
`TerrainReplayExperiment` with its default `FIELDS=patch` tests exactly this, by serving the client's own
recorded `sceneIndex` and `normalizedCoordinates`.

Still not shown: that the client's crash consumes any of these rather than computing its own terrain.

## The original hypothesis (now confirmed — kept for the reasoning)

Field battles derive terrain from the campaign map; village raids load a pre-built named scene. That is
exactly the axis the failure splits on.

In single-player the client derives battle terrain from its own complete campaign map. In coop the server is
authoritative — and the server's map scene is the **stripped** one. `HeadlessMapProjection` drops
`terrain`, `layers`, `nodes` and `outer_mesh` (the landscape renderer deadlocks server-side; TAOM's own
tooling strips identically, see `New-StrippedMapScene.ps1` in their host pack).

So the server can answer "which scene" correctly, but whatever it reports about **terrain layers** at a map
position comes from a scene that no longer has any. The client is then asked to build a texture array whose
slices do not agree, and D3D refuses.

This was written as a hypothesis, before instrumentation. It held — see the section above for the measurement.

## Recommended path

**1. Prove it before building anything.** ✅ Done 2026-09-12 — the result is the section above. Three guesses in
this investigation looked equally plausible and were wrong; the cheap confirmations are what moved it forward
each time.

The instrument for this exists now: **`TerrainProbe`** in `ModderLords.CompatSync` (the `ModderLords.Compat`
module, which is enabled on both the server and every client). It samples the campaign map scene through
`IMapScene` — terrain type, navmesh face, height, map patch, environment terrain counts, snow and rain — and
writes one CSV. Run it on the server and in single-player on the same map, then diff.

```
set MODDERLORDS_TERRAIN_PROBE=1            # or a full path; "1" writes to Configs\ModLogs\terrain-probe-<side>.csv
set MODDERLORDS_TERRAIN_PROBE_GRID=48      # optional, samples per axis (default 48 → 2304 rows)
set MODDERLORDS_TERRAIN_PROBE_AT=x,y       # optional extra exact positions, "x,y;x,y" — the battle position
```

It arms on the settings tick, fires once as soon as `Campaign.Current.MapSceneWrapper` exists, and is a no-op
when the variable is unset. Everything is bound by reflection against the interface, because the server's
implementation is the obfuscated `A.G` inside `DedicatedServer.Core`. A bind failure throws at setup and is
logged rather than producing a CSV of empty columns that reads like an answer.

Then:

```
.\scripts\Compare-TerrainProbe.ps1 -Server terrain-probe-server.csv -Client terrain-probe-client.csv
```

It checks map identity first — borders, terrain size, both scene CRCs, navmesh face count — and refuses to
let a row comparison be read when the two runs were not on the same map. Below that it reports a
disagreement rate per column and the first differing samples.

**If every column agrees, the hypothesis is dead** and the crash is not the server misreporting terrain.
If they disagree, the columns that disagree name the fix.

**2. Restore the terrain data the stripped scene lost** — not by un-stripping the scene (that reintroduces the
deadlock the stripping exists to avoid), but by answering the terrain queries from `terrain.bin`, which *is*
projected and complete (56 MB, byte-identical to source). The server has the data; the scene object it queries
no longer exposes it.

The measurement narrows this to three members, in priority order:

1. `GetEnvironmentTerrainTypesCount` — must return the neighbourhood list, not an empty one. Note that its
   `out` parameter is already correct, so only the list needs sourcing.
2. `GetMapPatchAtPosition` — must return the real `sceneIndex` and `normalizedCoordinates`, not 0.
3. `GetHeightAtPoint` — should return the terrain height, and in the meantime should at least return `false`
   rather than `true` with a height of 0. A query that admits it cannot answer is recoverable; one that lies
   is not.

`GetTerrainTypeAtPosition` and `GetFaceIndex` need no work: they already match the client everywhere.

### Before that: two spikes settle whether the client consumes these answers at all

What is proved is that the server's answers are empty. What is **not** proved is that the client's crash consumes
*these* answers rather than deriving terrain on a path of its own. `terrain.bin` is a 56 MB format and parsing it
is days of work, all of it wasted if the answer is no. Both spikes live in the guards module (always in the
server's load order), are off unless their own variable is set, and must never ship enabled.

**Spike 1 — fabricate the missing list** (`TerrainStubExperiment`). Asks the narrow question: is an empty
terrain-type list the cause? It needs no terrain data, because the server already answers the *current* terrain
type correctly everywhere — so a neighbourhood of that type is free.

```
set MODDERLORDS_TERRAIN_STUB=1          # or a neighbourhood size; 1 means 49, what the client returns
```

An empty list becomes N copies of the current type, and `GetHeightAtPoint` reports failure instead of `true`
with a height of 0.

**Spike 2 — replay ground truth** (`TerrainReplayExperiment`). Isolates `sceneIndex`, which spike 1 deliberately
leaves alone. Fabricating a patch index would be guessing, and a wrong index selects a wrong scene — a different
failure that muddies the result. So this answers from a *recorded single-player client probe CSV* instead, which
is ground truth at 48 samples per axis (~34 map units per cell: useless for gameplay, ample for "does the battle
load").

```
set MODDERLORDS_TERRAIN_REPLAY=...\terrain-probe-client.csv
set MODDERLORDS_TERRAIN_REPLAY_FIELDS=patch          # default; also height, env, or all
```

Run them **one at a time**, so a change in behaviour names which value mattered. Both print a line every 30
seconds saying how many answers they actually changed — a spike that patched the wrong thing and served nothing
is the failure most easily mistaken for a disproved hypothesis, so check that count is non-zero before believing
a negative result.

Reading the outcome:

| Result | Meaning |
|---|---|
| Spike 1 fixes the battle | The empty type list is the cause. Source that list from `terrain.bin` and stop. |
| Spike 2 (`patch`) fixes it | `sceneIndex` is the cause, despite scene *selection* already being ruled out. |
| Only `FIELDS=all` fixes it | Several answers are needed together; bisect from there. |
| Neither changes anything | The client is not consuming these. Stop working on the map scene and instrument what Coop sends on a field-battle start instead. |

Implementation note, and a correction worth not repeating. `SandBox.MapScene` declares these queries
**non-virtual**, from which the first version of these spikes concluded that the host's `A.G` could not take them
over, and patched SandBox's declarations. Wrong: `A.G` cannot *override* them, but it **re-implements the
interface**, which has the same effect. It carries its own sealed explicit `IMapScene.GetMapPatchAtPosition`,
`GetEnvironmentTerrainTypesCount` and `GetHeightAtPoint`, forwarding to obfuscated methods of its own
(`MapPatchData A(ref CampaignVec2)`). The interface slots point at those, so patches on SandBox's copies never
fired — a spike ran through a whole battle and served zero answers.

`MapSceneTarget.Implementations` therefore resolves through the runtime **interface map**, which names whatever
the type genuinely dispatches to without depending on a method name, and also patches any same-signature sibling
on `A.G` in case the host calls the inner method directly. Every postfix is idempotent, so more than one firing
is harmless.

Two rules follow for anything else patched in `DedicatedServer.Core`:

- Resolve targets through the interface map, never by assuming an inherited declaration is the one that runs.
- Bind by-ref patch arguments **positionally** (`__0`, `__1`). Obfuscation strips parameter names — `A.G`'s copy
  declares its out parameter with no name at all — so name-bound patch arguments fail silently.

Note also what `A.G` re-implements: exactly the three queries measured to return nothing, and none of the ones
measured to be correct. The obfuscated class is its own map of where the terrain data went.

**3. Keep it general.** The rule to aim for is "a mod that replaces the campaign map keeps its terrain
queries answerable headlessly" — not anything TAOM-shaped. The world-map grid textures went in by naming
convention rather than a per-mod offset table for the same reason.

## Useful assets

- **`Mapping.txt`** — a full deobfuscation map for `DedicatedServer.Core`, shipped in TAOM's host pack
  (`Artifacts/TAOM-Coop-Wrapper/*/Mapping.txt`). This is how `A.G` was identified as
  `DedicatedServerMapSceneCreator` and `A.f` as `ConsoleDebugManager`. Invaluable; the core is otherwise
  obfuscated with encrypted string literals.
- **`TerrainProbe`** — see step 1 above. `MODDERLORDS_TERRAIN_PROBE`, and
  `scripts\Compare-TerrainProbe.ps1` to diff two of its CSVs.
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
