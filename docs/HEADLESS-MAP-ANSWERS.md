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
exist. It now keeps them (`HeadlessMapProjection.BoundsEntities`), and `HeadlessMapBounds` reports on each server
start whether the scene's own bounds match the injected ones.

**Next step:** read that comparison on a real map. If it agrees, the sidecar, the hash check, the env var and the
exit path can all go, and map-replacing mods need no preparation for bounds at all. The terrain size is the
remaining question: `GetTerrainData` needs the scene's `<terrain>` descriptor, which
`MODDERLORDS_HEADLESS_MAP_KEEP_TERRAIN` retains and which was measured safe — the server reaches SERVING with it.

## Diagnostics for this class of bug

- **`TerrainProbe`** (`MODDERLORDS_TERRAIN_PROBE=1`, both sides) — samples the map scene over a grid and writes a
  CSV; `scripts/Compare-TerrainProbe.ps1` diffs a server's against a single-player client's. This is what turned
  "the server's answers are probably wrong" into a table of which ones.
- **`MapSceneCallCensus`** (`MODDERLORDS_MAPSCENE_CENSUS=1`) — counts which terrain members the server actually
  calls, and names the ones it never does. Measuring one member at a time and inferring from its silence cost this
  investigation two runs; this exists so nobody repeats that.

Both are off by default and neither changes an answer.
