# Field battles crash the client — handoff

**STATUS 2026-09-12: field battles work. One known gap remains.** Two independent faults were stacked.

1. The server answered `sceneIndex` 0 for the whole map, so **every** field battle anywhere loaded one arbitrary
   battle terrain. Fixed by `MapPatchRestore`, which is now on by default.
2. That arbitrary scene — and the scene at the one position every early test used — are the only **two** of
   TAOM's **81** selectable battle scenes shipped without a terrain shader cache. Those two still crash the
   client. The other 79 load.

Confirmed by prediction: with the restore on, a battle away from the test position resolved to index 46
(`battle_terrain_biome_046`, 2.8 MB shader cache) and **loaded**. Four for four across the session.

**An earlier confident claim to have solved this was retracted mid-session** — the retraction is kept below,
because the reasoning that produced it is worth not repeating. A confident mid-session claim that it was has been retracted — read
"Retraction" below before acting on any earlier section. What is established: the server's stripped map scene
returned `sceneIndex` 0 everywhere, and that is now fixed (`MapPatchRestore` reads the engine's real 1024x1024
index map). The client still crashes.

Status of the rest of the TAOM path: world generation, serving, joining and village-raid battles all work.

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
| Missing shader cache (as a *count*) | The village raid that **worked** had 120 `Missing shader from sack` lines; the field battle that crashed had 21. More misses in the working case. **But the count argument was too coarse** — see "the two battle scenes that crash are the only two without a shader cache" below. The *shipped per-scene* sack is the discriminator; the user-level cache under `ProgramData` is build-keyed and empty for every battle scene, so clearing it changes nothing. |
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

### 2026-09-12, fourth run: serving *a* `sceneIndex` loaded the battle — but see the retraction below

`TerrainReplayExperiment` with its default `FIELDS=patch`, serving the client's own recorded map patch:

```
05:23:08.367  terrain replay: first map patch served (sceneIndex 66)
05:23:08.389  [Coop] [BattleMissionLifecycle] Sending attack mission start: mapEvent="MapEvent_Created_1323"
05:23:16.907  [Coop] Controller entered instance "MapEvent_Created_1323"
              terrain replay: served 2 patch, 0 height, 0 env answer(s)
```

**The field battle loaded.** No `create_texture_array`, no client crash. At the time this read as the whole
causal chain closing. **It was not** — see the retraction below. The value served, 66, was not the correct one
for that position: the replay's 34-unit grid returned a neighbouring cell.

Note what was **not** served: height, and the environment terrain-type lists (`0 height, 0 env`). Neither was
needed. The stub spike had already corrected the height answers in an earlier run without changing the crash,
and the census showed the type lists are never read at all. The map patch was the whole of it.

#### Two things this run also surfaced

**It loaded slowly — minutes, not seconds.** Over the same window `GetFaceTerrainType` ran to 47.9 million calls
and was still climbing at ~95,000/second *after* the battle ended, with the server at 37–44 fps against its
usual 63. In the crashing runs that member was never called once. So reaching a working battle has uncovered a
scan that the crash used to pre-empt.

Be careful attributing all of that to the game: the census postfix is itself on `GetFaceTerrainType`, and the
claim in `MapSceneCallCensus` that its watch list holds "nothing on a hot path" is wrong for that member. Re-run
the replay **without** the census before believing any figure for how slow this really is.

**The load time is worth testing against `FIELDS=all`.** Height is still answered as 0-with-`true`, and a scan
hunting for usable ground is a plausible reading of 47 million face queries. Serving the recorded heights too is
a one-variable change from here.

#### What the real fix now has to do — and try the cheap one first

Step 2 below narrows to one member: `GetMapPatchAtPosition` must return the true `sceneIndex` and
`normalizedCoordinates`. `GetHeightAtPoint` and the terrain-type lists are off the critical path for this crash,
though height may matter for the load time above.

**Before writing a `terrain.bin` parser, read what the projection actually does.** `HeadlessMapProjection.Prepare`
does two separable things:

1. `entities.RemoveNodes()` — empties every `game_entity` from the scene. Plainly about rendering.
2. `root.Element("terrain").Remove()` — removes the `<terrain>` descriptor (`node_size`, `node_dimension_x/y`).

Only the second causes this bug, and **`terrain.bin` is copied into the projection intact** by the same method
(it is in `BinaryFiles`). The data is already sitting next to the scene the server loads; what was removed is the
eight-attribute element that declares it. Whether the headless engine can carry that descriptor without reaching
the landscape renderer it deadlocks in has never been tested apart from entity removal — "the stripping is
load-bearing" was recorded about the pair, not about each half.

```
set MODDERLORDS_HEADLESS_MAP_KEEP_TERRAIN=1
```

keeps the descriptor and strips entities as before. The projection cache records which rule built it, so flipping
the switch rebuilds rather than silently reusing the other one.

**Tested 2026-09-12. Half good, half not.**

- **The descriptor does not deadlock the server.** `prepared a private headless map WITH its terrain descriptor
  kept` → `SERVING` at 05:36:47 → client joined → `FieldBattleMissionInitializer` at 05:39:14, server healthy
  throughout. "The stripping is load-bearing" was true of the pair; it is the **entity removal** that carries it,
  not the terrain element. That rule can be narrowed.
- **It changes no answers.** Probed on the same run, on the right map (19,396 faces): `sceneIndex` 0 on all 2304
  samples, `normalizedCoordinates` 0 on all 2304, height 0 on all 2304, terrain-type lists still empty. Byte for
  byte the same as with the element removed. The client crashed identically.

So declaring the terrain is not enough to populate it headlessly — the engine evidently builds that data on a path
the dedicated server does not run. **The fix has to supply the answers itself**, and the cheap path is closed.

### Where the data actually lives

- `scene.xscene` contains the string "patch" **zero** times. It is not in the scene XML.
- `TAOM_Map` ships no `map_patch*` file of any kind, and neither does Native.
- Which leaves `Main_map	errain.bin` (56,114,607 bytes) or `SceneEditData\Main_map	errain_ed.bin`.

### The answer, from SandBox's own source (2026-09-12)

`SandBox.dll` is not obfuscated. Decompiled, the entire query is:

```csharp
public MapPatchData GetMapPatchAtPosition(in CampaignVec2 position)
{
    if (_battleTerrainIndexMap != null)
    {
        int x = MathF.Floor(position.X / _terrainSize.X * _battleTerrainIndexMapWidth);
        int y = MathF.Floor(position.Y / _terrainSize.Y * _battleTerrainIndexMapHeight);
        int i = (MBMath.ClampIndex(y, 0, H) * W + MBMath.ClampIndex(x, 0, W)) * 2;
        byte b = _battleTerrainIndexMap[i + 1];
        return new MapPatchData {
            sceneIndex = _battleTerrainIndexMap[i],
            normalizedCoordinates = new Vec2((b & 0xF) / 15f, ((b >> 4) & 0xF) / 15f) };
    }
    return default(MapPatchData);        // sceneIndex 0, coordinates 0,0
}
```

**The server was never computing a wrong answer.** `_battleTerrainIndexMap` is null and it returns the default
struct — which is exactly the `sceneIndex` 0 and `0,0` coordinates measured at all 2304 samples. It also explains
why the coordinates observed on the client were always multiples of 1/15: they are two 4-bit halves of one byte.

And the array is filled, in `MapScene.AfterLoad`, by:

```csharp
MBMapScene.GetBattleSceneIndexMap(_scene, ref _battleTerrainIndexMap, ref width, ref height);
```

`TaleWorlds.MountAndBlade.MBMapScene.GetBattleSceneIndexMap` is a **public managed API over the native scene**,
present in the engine the dedicated server already runs, and in the reference assemblies. Nothing needs decoding:
ask the engine for the same bytes the client asks for, and answer with the same arithmetic.

`MapPatchRestore` (`MODDERLORDS_MAP_PATCH_RESTORE=1`) does exactly that. It needs the scene to carry its terrain,
so it pairs with `MODDERLORDS_HEADLESS_MAP_KEEP_TERRAIN=1` — which the experiment above proved safe, and which
now looks less like a dead end than like the missing prerequisite: nothing had ever asked for the index map.

This is the general fix the handoff asked for. No format is parsed, nothing is prepared per map, and any mod that
replaces the campaign map is covered, because the data comes from whatever scene the server actually loaded.

### Retraction, and what the crash log says (2026-09-12, runs five to eight)

`MapPatchRestore` works. It reads a genuine 1024x1024 index map with 182 distinct scene indices, **and it does so
without `MODDERLORDS_HEADLESS_MAP_KEEP_TERRAIN`** — that pairing was an untested assumption and is wrong. The
descriptor is not needed and the switch can be dropped.

It also does not fix the crash.

| Run | Patch served at the battle position | Result |
|---|---|---|
| replay (48-per-axis recording) | `sceneIndex` **66** — a neighbouring cell, not the true value | **battle loaded** |
| restore (engine's own 1024x1024 map) | `sceneIndex` **55** — the true value | crashed |

Every one of these battles was fought at the same place. So the run that loaded did so because it served an
**incorrect** value, and the correct one crashes. "Serving a real sceneIndex loads the battle" was wrong. The
section above is left standing with a pointer here rather than rewritten, because the reasoning that produced a
confident wrong conclusion is worth not repeating.

What the indices name in TAOM's `sp_battle_scenes.xml`:

- 66 → `battle_terrain_d` (Desert, no `<TerrainTypes>`) — loaded
- 55 → `battle_terrain_020` (Plain, declares `<TerrainTypes>` Water and Mountain) — crashed
- 0 → `battle_terrain_a` — the original failing case

A tempting hypothesis was that scenes declaring `<TerrainTypes>` need terrain-type queries the server cannot
answer. **Measured false:** with a real patch being served, `TerrainStubExperiment` still reports
`filled 0 empty type list(s)`. The server is never asked for terrain types, patch or no patch. And all three
scenes ship the same shape of terrain (6x6 nodes at 200), so "the working scene had no terrain to build" is out
as well.

The client's own crash log is the most specific evidence yet:

```
[06:29:19.673] Scene_view::clear_all(Main_map)
[06:29:20.266] Loading xml file: $BASE/Modules/SandBoxCore/SceneObj/battle_terrain_020/scene.xscene.
[06:29:20.385] NAV_MESH: load finished, first load for scene: 1
[06:29:20.412] rglTerrain_shader_generator::clear
[06:29:20.432] Missing shader from sack: pbr_terrain            (many)
[06:29:20.432] compile_shader: $BASE/Shaders/Sources/pbr_terrain.rs, main_vs, vs_5_0, ...
[06:29:20.443] ERROR: rglGPU_device::create_texture_array failed at d3d_device_->CreateTexture2D!
```

The battle scene's XML, atmosphere and navmesh all load **successfully**. The failure is in terrain rendering
setup, immediately after the terrain shader generator finds `pbr_terrain` missing from the shader sack and
compiles it at runtime.

That reopens something the ruled-out table dismissed. "Missing shader cache" was eliminated by comparing
*counts* — the working village raid had 120 misses, the failing battle 21 — but nobody looked at *which* shaders.
These are `pbr_terrain`, `pbr_terrain_gbuffer`, `pbr_terrain_shadowmap`, `pbr_terrain_pointlight`: the terrain
shaders specifically, missing at the exact moment the terrain texture array is built. A count argument does not
eliminate that.

**Establish whether coop is implicated at all.** Fight this same battle, at this same map position, in
single-player. Everything so far rests on "it works in single-player", which was confirmed early but not
demonstrably at this position — and every coop run has been at one spot.

- If single-player crashes too, this is not a coop bug, and the map-scene work, while correct, was never the cause.
- If single-player loads, the difference is in what the client does under coop with a scene that otherwise loads
  fine, and the terrain shader path is where to look.

### 2026-09-12: the two battle scenes that crash are the only two without a shader cache

Of the **81** battle scenes TAOM's `sp_battle_scenes.xml` can select, exactly **two** ship without a compiled
terrain shader cache (`ShaderCache/D3D11/compressed_shader_cache.sack`):

```
NO SACK: battle_terrain_a     map_indices = 0, 2, 136, 118
NO SACK: battle_terrain_020   map_indices = 52, 54, 86, 35, 121, 122, 59, 55, 56, 57, 63, 110, 117, 144
```

Against every run of the session:

| served index | scene | shader sack | result |
|---|---|---|---|
| 0 — the server's broken default | `battle_terrain_a` | **none** | crashed |
| 55 — the true value at the test position | `battle_terrain_020` | **none** | crashed |
| 66 — the replay's wrong neighbouring cell | `battle_terrain_d` | 415,948 bytes | **loaded** |

Three for three, and hitting the only two sackless scenes out of 81 twice is roughly a 0.06% coincidence. It also
matches the crash log line for line: `read_compressed_shader_cache_package : 0.000012` finds nothing, every
`pbr_terrain*` shader is then missing, they are compiled at runtime, and the texture array creation fails.

This is also why the failure looked total. Before `MapPatchRestore`, the server answered `sceneIndex` 0 for the
**entire map**, so every field battle anywhere loaded `battle_terrain_a` — one of the two broken scenes. The one
position tested since happens to resolve to 55, the other one.

**`MapPatchRestore` is therefore necessary and is a real fix**, even though it did not stop the crash at that
position: without it every field battle crashes; with it, battles reach their true scene and 79 of 81 have a
shader cache.

**Prediction made, then confirmed.** With the restore on, a field battle well away from that position was called
to load. It resolved to index 46 — `battle_terrain_biome_046` (Swamp, SandBoxCore, 2,798,080-byte shader cache)
— and loaded.

| served index | scene | shader cache | result |
|---|---|---|---|
| 0 | `battle_terrain_a` | none | crashed |
| 55 | `battle_terrain_020` | none | crashed |
| 66 | `battle_terrain_d` | 415,948 bytes | loaded |
| 46 | `battle_terrain_biome_046` | 2,798,080 bytes | loaded |

So field battles work, on 79 of 81 scenes. The served index is logged on every battle, so any future crash can be
checked against the two sackless scenes in one step.

#### Shader caches: where they are, how they are keyed, and why clearing them does nothing here

Bannerlord clears compiled shaders when it detects a change, which makes "the cache is stale for this mod set" a
natural suspicion. Measured 2026-09-12, it is not what is happening — and the layout is worth writing down so
nobody spends an evening on it.

There are three separate things called a shader cache:

| | Where | What it is |
|---|---|---|
| Global | `<game>\Shaders\D3D11\compressed_shader_cache.sack` | 1.5 GB, shipped. Read once at startup (~145 ms in the client log). |
| **Per-scene, shipped** | `<game>\Modules\<mod>\SceneObj\<scene>\ShaderCache\D3D11\compressed_shader_cache.sack` | Game content. **This is the discriminator.** |
| Per-scene, user | `C:\ProgramData\Mount and Blade II Bannerlord\Shaders\TerrainShaders\<mod>\<scene>\D3D11\` | `shader_mapping.bin` + `NNNp.sacx`, written at runtime. |

**The user cache is keyed on the game build, not on the mod set.** `shader_mapping.bin` begins:

```
83 07 00 00 | 06 00 00 00 | 31 31 39 33 30 33
   1923      |  length 6   |  "119303"  = the build number
```

Nothing in it identifies the mods that were loaded. So it does **not** invalidate when the mod set changes — a
scene whose terrain layers a mod alters would keep serving shaders compiled for the old configuration. Worth
knowing; no evidence it has caused harm. TAOM's `Main_map` is the only scene here with real cached content
(12,758-byte mapping, 90 `.sacx` variants) and it renders correctly.

**And it is empty for every battle scene, working and crashing alike.** A 14-byte mapping is the header and
nothing else — zero entries:

| scene | user mapping | cached variants | shipped `.sack` | result |
|---|---|---|---|---|
| `battle_terrain_020` | 14 bytes (empty) | 0 | **absent** | crashed |
| `battle_terrain_a` | 14 bytes (empty) | 0 | **absent** | crashed |
| `battle_terrain_d` | 14 bytes (empty) | 0 | 415,948 bytes | loaded |
| `battle_terrain_biome_046` | 14 bytes (empty) | 0 | 2,798,080 bytes | loaded |

So nothing stale is being served to these scenes, because nothing is cached for them at all. **Clearing
`ProgramData\...\TerrainShaders` is a no-op for this bug.** The split falls entirely on the *shipped* per-scene
sack, which clearing does not touch and which only TaleWorlds can add.

#### What is left

Caveat worth keeping: vanilla ships those two scenes without a cache and presumably plays them fine in
single-player, so a missing cache may be necessary but not sufficient. What makes runtime terrain-shader
compilation fail *here* is not established — the correlation is 4 for 4, but it is still a correlation.

Ideas, cheapest first, for whoever picks this up:

- Check whether those two scenes crash a **single-player** client too. If they do, this is a vanilla content gap
  that coop merely exposed, and it belongs upstream rather than here.
- Compare what the client does differently for a sackless scene when joined versus solo; the terrain shader
  generator path is where to look.
- A per-scene shader cache is a build artefact, not content. If the engine can be made to write one, generating
  the two missing caches once would close the gap without touching either mod.

### The two routes considered before that, kept for the record

They are very different sizes of job:

**A. Read the patch layer out of `terrain.bin`.** The most correct fix and the most general — any map-replacing
mod would work headlessly with no per-map preparation. Cost is unknown until the format is cracked; it is
proprietary and undocumented.

**B. Prepare a patch table from the player's own client, and serve it.** This is exactly what
`TerrainReplayExperiment` already proves works at runtime — it is only reading a CSV that happens to have been
written by a probe. Promoted from spike to feature, the launcher would capture the table once per map (the client
loads the full map anyway) and ship it beside the projected scene, the same shape as the existing asset and map
preparation steps. Known cost, proven mechanism, no reverse engineering. Needs a resolution study first: the
48-per-axis recording used for the spike is far coarser than the real patch grid, and the sampling needs to match
whatever that grid turns out to be.

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

### The two spikes that got us here (removed)

`TerrainStubExperiment` fabricated the missing terrain-type list; `TerrainReplayExperiment` replayed a recorded
client probe. Both answered their question and were deleted — the stub because nothing ever asks the server for
terrain types, the replay because `MapPatchRestore` now reads the real map from the engine. They are in the
history if ever wanted: `git show 4b96b21`.

What they established is worth keeping:

- A count of served answers is not evidence. The stub reported `filled 0` through an entire battle because it had
  patched a method nothing calls, which reads exactly like a disproved hypothesis.
- The one run that loaded a battle did so on a *wrong* value, which is why every spike since prints the values it
  serves and not just how many.

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
