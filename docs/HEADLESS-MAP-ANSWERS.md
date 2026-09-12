# What the headless server answers wrongly, and why

A dedicated server runs `DedicatedServer.Core`'s own map scene in place of `SandBox.MapScene`. That class is a
deliberately reduced implementation: a set of members return defaults, empties and zeroes, **with no warning and
no log line**. A mod that depends on one of them does not fail — it gets a plausible-looking wrong answer.

This is the general shape of every compatibility problem found on this path so far, including the field-battle
client crash (`docs/FIELD-BATTLE-TERRAIN.md`), which was one null `byte[]`. This document is the inventory, so the
next one can be recognised in minutes rather than a night.

Derived by decompiling the shipped `DedicatedServer.Core.dll` (it is obfuscated, but `SandBox.dll` is not, so the
two can be read side by side). Reproduce with:

```
dotnet tool install -g ilspycmd
ilspycmd -r <server>/bin/Win64_Shipping_Server -r <game>/Modules/SandBox/bin/Win64_Shipping_Client \
         <server>/bin/Win64_Shipping_Server/DedicatedServer.Core.dll > core.cs
```

## The inventory

Everything in `DedicatedServer.Core` whose body is a single trivial return. Twenty-six members; the split matters
more than the count.

### Harmless — no screen, nothing to draw

`DisplayDebugMessage`, `WatchVariable`, `WriteDebugLineOnScreen`, `RenderDebugLine`, `RenderDebugSphere`,
`RenderDebugText3D`, `RenderDebugFrame`, `RenderDebugText`, `RenderDebugRectWithColor`, `SetDebugVector`,
`SetCrashReportCustomString`, `SetCrashReportCustomStack`, `SetTestModeEnabled`, `ReportMemoryBookmark` — debug and
render sinks. Also the party-visual stubs `Initialize`, `OnMapEventEnd`, `SetVisibility`.

None of these carry data a mod can read back. Ignore them.

### Load-bearing — a mod reads these and gets a lie

| Member | Returns | What it costs |
|---|---|---|
| `GetMapPatchAtPosition` | `default(MapPatchData)` | **Fixed.** `sceneIndex` 0 for the whole map, so every field battle loaded one arbitrary battle terrain. See `MapPatchRestore`. |
| `GetEnvironmentTerrainTypes` | empty `List<TerrainType>` | Terrain composition queries return nothing. Measured: nothing on the battle path asks, but a mod may. |
| `GetEnvironmentTerrainTypesCount` | empty list; **out-param is correct** | Same, and the asymmetry is a trap — the terrain type is right, the list beside it is empty. |
| `GetHeightAtPoint` | `0`, **returning `true`** | The worst of the set: it reports success. A caller that checks the return value is told the answer is good. |
| `GetFaceVertexZ` | `0f` | Height from a navmesh face is always ground level. |
| `GetAtmosphereStates` | empty list | **Called once at field-battle start.** The client's `MapScene.AfterLoad` fills this via `MBMapScene.LoadAtmosphereData`; the headless `Load` omits that call. Untested suspect for the two battle scenes that still crash. |
| `SetAtmosphereColorgrade` | no-op | Rendering only; harmless in isolation, but it means the server never records a colorgrade choice a mod may expect to read. |
| `GetSnowAmountAtPosition` | `0f` | Measured to differ from a client on 30% of sampled positions. |
| `GetRainAmountAtPosition` | `0f` | Differs on 22%. |
| `GetWinterTimeFactor` | `0f` | Seasonal logic runs as though it is never winter. |
| `AddNewEntityToMapScene` | no-op | A mod adding an entity to the campaign map silently adds nothing. |

### Hardcoded, not stubbed — the vanilla map's dimensions

The headless scene's static constructor:

```csharp
G.m_A = new Vec2(62f, 30f);     // border_min       vanilla
G.m_a = new Vec2(790f, 640f);   // border_max       vanilla
const float m_A = 620f;         // maximum height   vanilla
B = new Vec2(848f, 848f);       // terrain size     vanilla
```

For a mod that replaces the campaign map these are simply wrong — TAOM's map is `(62,0)..(1700,1550)`, height
`646.01`, terrain `1600 x 1600`. `HeadlessMapExperiment` overrides them today from a generated sidecar.

## The root cause, in one line

`DedicatedServer.Core`'s `Load()` is a hand-written subset of `SandBox.MapScene.AfterLoad()`. **Everything
`AfterLoad` does that `Load` omits becomes a silent wrong answer.** The omissions:

| `AfterLoad` does | headless `Load` |
|---|---|
| reads `border_min` / `border_max` entities for the bounds | hardcodes vanilla's |
| `GetTerrainData` → terrain size | hardcodes `848 x 848` |
| `MBMapScene.GetBattleSceneIndexMap` | **omitted** — the field-battle crash |
| `LoadAtmosphereData` | **omitted** — empty atmosphere states |
| `MBMapScene.ValidateTerrainSoundIds` | omitted |
| `_scene.OptimizeScene` | omitted |
| `MapWeatherModel.InitializeCaches` | done |
| `_scene.Tick(0.1f)` | done |

That table is the method for finding the next one: diff the two, and anything `AfterLoad` does that `Load` skips
is a candidate.

## The general fix, and why it is general

Ask the engine, the way the client asks it. `MapPatchRestore` calls
`MBMapScene.GetBattleSceneIndexMap` — a public managed API over the native scene, present in the engine the server
already runs — and answers with the arithmetic lifted from decompiled `SandBox.MapScene`. No format is parsed and
nothing is prepared per map, so **any** mod that replaces the campaign map is covered, because the data comes from
whatever scene the server actually loaded.

The same shape applies to most of the table above. Where `AfterLoad` calls a public API, the server can call it
too.

## In progress: retiring the map sidecar

The bounds are the clearest case. Today a mod map needs `HeadlessMapProjection` to compute a sidecar, hash the
navmesh into it, and `HeadlessMapExperiment` to load, validate and inject it — with `Environment.Exit(12)` if
anything disagrees. All of that carries four numbers.

A client reads those four numbers out of the scene, from two `game_entity` markers that are **153 and 159 bytes,
pure transforms, no mesh, no components, no children**:

```xml
<game_entity name="border_min"><transform position="62.000, 0.000, 26.000"/></game_entity>
<game_entity name="border_max"><transform position="1700.000, 1550.000, 646.010"/></game_entity>
```

The projection deleted them along with everything else renderable, which is the only reason the sidecar had to
exist. It now keeps them (`HeadlessMapProjection.BoundsEntities`), and `HeadlessMapBounds`
(`MODDERLORDS_HEADLESS_MAP_BOUNDS_CHECK=1`) reports whether the scene's own bounds match the injected ones.

**It is opt-in because its first version crashed a server.** Every call it makes crosses into native code, where a
fault cannot be caught by the try/catch around it, and it took the engine down with an access violation moments
after the map scene loaded. It now announces each call before making it — so a repeat names the exact one — and
refuses to ask a terrain-less scene for terrain data, which is the likeliest of the two candidates. The retained
markers themselves are not implicated: the scene loaded and logged `Load complete` before anything went wrong.

**Measured 2026-09-12 on TAOM's map — they agree exactly.**

```
map bounds derived from the scene : (62, 0)..(1700, 1550) height=646.01
map bounds currently in effect    : (62, 0)..(1700, 1550) height=646.01
map bounds: terrain derived=(1600, 1600) in effect=(1600, 1600) (agree)
```

Both runs reached SERVING and stopped cleanly. So the scene can answer for its own bounds, and the sidecar is
carrying numbers that are already in the scene the server loads.

That run also settled what the earlier access violation was, by elimination: the borders-only run does
everything the crashing run did **except** `GetTerrainData`, and it was clean. `GetTerrainData` on a scene whose
terrain descriptor was stripped is the fault. It is safe with `MODDERLORDS_HEADLESS_MAP_KEEP_TERRAIN=1`, which
is the only configuration that calls it.

**What this unlocks, and the caveat.** The sidecar cannot simply be deleted: `HeadlessMapExperiment` patches the
bounds getters *before* the scene loads, so correct values are in effect from the first read, whereas
scene-derived values only exist afterwards. The sidecar is also free — the projection computes those numbers
from the scene XML anyway.

**Done 2026-09-12.** The machinery around the sidecar is gone: the navmesh SHA-256 validation, and the
`Environment.Exit(12)` that killed the process when anything about the sidecar failed. Their job was to prove the
sidecar matched the scene, and `HeadlessMapBounds` now does that directly against the **loaded** scene, which is
a stronger check than hashing a file next to the sidecar. It is on by default, and a disagreement is a loud
warning naming the consequence rather than a silent exit code.

The sidecar itself stays, deliberately. `HeadlessMapExperiment` patches the bounds getters *before* the scene
loads, so its values are in effect from the first read; scene-derived ones only exist afterwards. It is also
free — the projection computes those numbers from the scene XML regardless. The `navmesh-sha256` attribute is
still written for provenance; nothing reads it.

A failure to apply the prepared bounds no longer stops the server. It warns, runs, and the bounds check then
reports the disagreement — so a server that would previously have refused to start gets to run, and the problem
is still impossible to miss.

**Original next step, now done:** read that comparison on a real map. If it agrees, the sidecar, the hash check, the env var and the
exit path can all go, and map-replacing mods need no preparation for bounds at all. The terrain size is the
remaining question: `GetTerrainData` needs the scene's `<terrain>` descriptor, which
`MODDERLORDS_HEADLESS_MAP_KEEP_TERRAIN` retains and which was measured safe — the server reaches SERVING with it.

## The stubs now announce themselves

`SilentDefaultWatch` (on by default; `MODDERLORDS_STUB_WARNINGS=0` silences it) patches the load-bearing stubs
and warns **once per member per run** the first time a mod reads one:

```
[ModderLords.Compat] Warning: a mod just read IMapScene.GetHeightAtPoint, which this server does not implement:
reports SUCCESS with a height of 0, so a caller that checks the return value is told the answer is good.
```

It changes no answers. After the first report the postfix is a bool read, and members another component has
restored are skipped so a fixed query does not keep warning. It deliberately leaves `GetFaceVertexZ` alone —
it may sit on the pathfinding path, and the census established what patching a hot member costs.

This is the point of the inventory. The field-battle crash was one of these read silently; with this in place it
would have been a line in the first log anyone looked at.

## Diagnostics for this class of bug

- **`TerrainProbe`** (`MODDERLORDS_TERRAIN_PROBE=1`, both sides) — samples the map scene over a grid and writes a
  CSV; `scripts/Compare-TerrainProbe.ps1` diffs a server's against a single-player client's. This is what turned
  "the server's answers are probably wrong" into a table of which ones.
- **`MapSceneCallCensus`** (`MODDERLORDS_MAPSCENE_CENSUS=1`) — counts which terrain members the server actually
  calls, and names the ones it never does. Measuring one member at a time and inferring from its silence cost this
  investigation two runs; this exists so nobody repeats that.

Both are off by default and neither changes an answer.
