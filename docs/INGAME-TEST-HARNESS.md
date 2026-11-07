# In-game test harness

Status: **MVP built 2026-11-06, not yet run in game.** Design agreed with the maintainer the same day; the
decisions are below, then how to run it and how to write a step.

## Why

Coop 0.1.7 started finishing the inventory screen's Done itself. Our `InventoryExchangeEvent` did it too, and town
stashes doubled. A player found that, not us. The existing smoke test (`ModderLords.Core.Smoke`) proves a client
joins and stays on the map; it proves nothing about gameplay. We want: after a Coop or mod update, one command runs
scripted checks in a real session (dedicated server + client on this PC) and says pass or fail per check, with enough
evidence to diagnose a failure without re-running it.

Checks it must be able to express:

1. Open a town stash, move items in, Done; close and reopen 3 times; the stash's item count is unchanged.
2. Complete a delivery-quest step; quest progress counts once.
3. Edit a hero's level from the host; the client sees the new level.
4. A Living Economy menu action on the client is carried out by the server.

## Decisions

| # | Decision | Why |
|---|---|---|
| 1 | **Every run starts from a known world**, copied fresh. A world is named per suite; for mods that change world generation the harness creates the world for that mod set (the CLI's world creation) and caches it by mod list + versions. | Tests change the world; reusing a live save makes results meaningless. |
| 2 | **Both sides are checked.** A built-in compare primitive reads the same thing on server and client and the launcher compares them. | Coop bugs are mostly desync or double-apply; the server's copy is the one that gets saved. |
| 3 | **Waits are conditions with a timeout**, and "settled" means the value matched and then stayed unchanged for a quiet period (default 2 s). Campaign time is paused during steps. | No fixed sleeps; works on a slower PC. |
| 4 | **Steps call game code, not the UI**, entering through the same entry point the menu uses (e.g. the town menu's stash action). A "menu option exists and is enabled" check covers missing-menu bugs. | Speed and repeatability; Coop's patches (where 0.1.7 changed things) still run. |
| 5 | **Hook API: reference-based only (`InGameTests.Register`), v1, internal-stable.** Only our modules and `coop-compat/` modules use it for now. A reference-free convention for third parties can be added later. | Nobody else uses it yet; don't freeze a contract we haven't lived with. |
| 6 | **Test mode cannot leak into a real session**: on only with a run folder whose one-off token matches; loud log line and "TEST MODE" in the server's player list; Host mode's normal launch never passes the switch. | Test steps cheat (give items, set levels). |
| 7 | **Failures carry their evidence**: step log, state dump from both sides, both sides' log slices, a client screenshot. Every report records game, Coop and ModderLords versions, mod list and world. | A red result you can't diagnose is barely better than none; runs must be comparable across Coop versions. |
| 8 | **Known failures**: a step can be marked known-failing with an issue link. Outcomes: pass, fail, known-fail, unexpectedly-passing (upstream fixed it). | Red must stay meaningful. |
| 9 | **One PC.** Files are the transport (below), behind a small interface so another transport can come later. | Server and client run on the maintainer's machine. |
| 10 | **Players are addressed by slot** (`server`, `client:1`, `client:2`...). The MVP starts one client; the format allows more. | Many Coop bugs need two players; changing the plan format later is costly. |

## Shape

```
  launcher (ModderLords.Core.Testing)          run folder                          game processes
  ───────────────────────────────────          ──────────                          ──────────────
  prepare world (copy / create + cache)
  SmokeRunner gets the session to OnMap  ──►   run.json      (plan + token)    ──► agent on server
  TestRunner appends "go step N" and      ──►  control.jsonl (launcher only)       agent on client:1
  waits for that side's result line       ◄──  results-<side>.jsonl (one writer each)
  collects evidence, writes report        ◄──  evidence/<step>/... (dumps, screenshot)
```

- **Launcher side** (`ModderLords.Core/Testing`, net10, unit-tested): plan model, line codec, sequencer, world
  preparation, report. CLI: `ModderLords.Cli test --suite stash`. An App button can come later.
- **In-game agent** (`ModderLords.CompatSync`, net472, no Coop reference): inert unless test mode is on (decision 6).
  Ticks every frame from the submodule's `OnApplicationTick`, reads `control.jsonl`, runs its side's steps, appends
  results.
- **Coop-touching steps** live in `ModderLords.CompatSync.Coop` and register through the same API.

### Transport: files in a run folder

`%ProgramData%\ModderLords\tests\<runId>\`, one writer per file, append-only JSON Lines, readers tail by byte offset
(as `LogTail` does). No port, no firewall prompt, no listener thread in the game; a crash leaves what happened on
disk. Polling costs nothing measurable; a handoff takes under a second.

## Running it

```powershell
ModderLords.Cli test --profile "My Host" --suite stash [--world NAME] [--keep-client] [--keep-server]
```

1. The suite is loaded (built-in name, or a path to a `.json` file) and checked; a mistake stops here.
2. With a world (`--world`, or the suite's `"world"`), `<data>\test-worlds\NAME.sav` (and its Coop session file) is
   copied over the server's `mltest-NAME` save, and the server loads that. Without one, the profile's save is used.
3. A run folder is created under `<data>\tests\`. The server is started with `MODDERLORDS_TEST_RUN` in its launch
   plan; the player's game with `/modderlords-test` (plus the smoke test's automatic join).
4. Once the game is on the campaign map, the steps run in order. Then the game and server are closed.
5. `report.txt` / `report.json`, `evidence\`, both sides' result files, the server console and the smoke test's log
   slices are all in the run folder. The exit code is 0 only when every step passed (or failed as known).

Built-in suites: `stash` (the 0.1.7 stash doubling) and `sync-basics` (gold given on the server reaches the player).
`stash` uses `town_EN1` and `grain`, both vanilla.

Test mode is visible: each game logs `TEST MODE: ...` at load (`ModderLords.CompatSync` log) and the agent logs each
step. It is not yet shown in the server's player list (decision 6); that is a follow-up.

## Writing a step (hook API v1)

Reference `ModderLords.CompatSync.dll` (compile-time only, `Private="false"`, like the other modules) and register
steps from your module's `OnSubModuleLoad`. Registering costs nothing when no test runs.

```csharp
using ModderLords.Testing;

InGameTests.Register("warehouse.deposit", StepSide.Client, Deposit);

static IEnumerable<StepWait?> Deposit(StepContext ctx)
{
    var count = ctx.ArgInt("count");
    var before = Warehouse.Count(ctx.Arg("town"));
    Warehouse.Deposit(ctx.Arg("town"), count);                            // call the code the menu calls
    yield return ctx.Settled(() => Warehouse.Count(ctx.Arg("town")), "the warehouse", expected: before + count);
    ctx.Report("value", Warehouse.Count(ctx.Arg("town")));                // for later steps and compares
}
```

Rules:

- **Ids** are `area.verb`, unique across all modules (a later `Register` with the same id replaces the earlier).
- **Side**: `Server` steps change the world (a change made on a player's game is overwritten by Coop's sync);
  `Client` steps act as the player; `Any` steps read state and are what compares use.
- A step is a coroutine on the game's main thread. `yield return null` waits one frame.
- **Wait on conditions, never fixed times.** `ctx.Until(cond, what, timeout?)` waits for a condition.
  `ctx.Settled(read, what, expected?, quiet?, timeout?)` waits until `read()` has stopped changing for `quiet`
  (default 2 s), and with `expected`, also equals it. `ctx.Delay` exists but passes on a fast PC only.
- **Fail** with `ctx.Assert(cond, message)`. Any exception fails the step too, with its type and message. Write
  messages that say what was seen and what was expected: "after reopening 2 time(s) the stash holds 10 grain, expected 5".
- **Report** values with `ctx.Report(key, value)`. Later steps read them as `"$stepName.key"`. A compare reads `value`.
- **Evidence**: `ctx.Dump(name, text)` writes `evidence\<seq>-<step>-<side>\name.txt`; `ctx.Log(line)` goes into the
  result and is printed under a failed step. A failed client step also gets a screenshot.
- `ctx.State` is shared by the steps of one run on one side; across sides, use Report and `$` references.
- Arguments arrive as text: `ctx.Arg`, `ctx.ArgInt`, with or without a fallback.

Built-in steps (`src/ModderLords.CompatSync/Testing/BuiltInSteps.cs`): `game.onMap`, `game.pause`, `game.whoAmI`
(reports `heroName`, `hero`, `party`; find a player's hero on another side by `heroName`: its StringId is local), `hero.addGold`, `hero.gold`, `party.addItem`, `party.itemCount`, `stash.count`,
`stash.deposit`, `stash.reopen`. Steps that need Coop's types go in `ModderLords.CompatSync.Coop` and register the same
way; nothing in the agent references Coop.

## Writing a suite

```jsonc
{
  "api": 1,
  "name": "stash",
  "world": "vanilla-1",                         // optional: test-worlds\vanilla-1.sav, copied fresh
  "steps": [
    { "id": "game.whoAmI", "side": "client:1", "as": "me" },
    { "id": "party.addItem", "side": "server", "as": "given", "args": { "heroName": "$me.heroName", "item": "grain", "count": 5 } },
    { "id": "stash.reopen", "side": "client:1", "timeoutSeconds": 180, "args": { "times": 3, "expected": "$deposit.value" } },
    { "compare": "stash.count", "sides": ["server", "client:1"], "args": { "town": "town_EN1", "item": "grain" } },
    { "id": "some.check", "side": "server", "knownFailing": "https://github.com/.../issues/1", "continueOnFail": true }
  ]
}
```

- `side` is `server` or `client:N`. This version starts one player's game; a suite naming `client:2` is refused.
- `as` names a step for `$name.key` references (default: its id). A reference must name an earlier step.
- `timeoutSeconds` (default 60) is enforced by the game; the launcher waits 30 s longer before reporting "no result".
- The first failure skips the rest unless `continueOnFail`. `knownFailing` turns a failure into `KnownFail` (the
  run still passes) and a pass into `UnexpectedPass` (the run fails, so the mark gets removed).
- Before any step runs, every game must announce itself and have every step the suite names; otherwise nothing runs
  and the report says what is missing.

## Files in the run folder

All JSON Lines, one writer per file, UTF-8.

| File | Writer | Lines |
|---|---|---|
| `run.json` | launcher | `{api, runId, suite, token}` |
| `control.jsonl` | launcher | `{"op":"step","seq":N,"side":"client:1","step":"id","args":{...},"timeoutMs":N}`, finally `{"op":"end"}` |
| `results-server.jsonl`, `results-client-1.jsonl` | that game | first `{"op":"hello","side","api","steps":[...],"versions":{game,coop,modderlords},"refused"?}`, then per step `{"op":"result","seq","step","side","status":"pass"/"fail","message","values":{...},"log":[...],"ms"}` |
| `evidence\...` | games | dumps and screenshots |
| `report.txt`, `report.json` | launcher | the outcome |

A game's switch value is `folder|side|token`; it refuses a run whose `run.json` token differs, and a server switch
on a player's game (or the reverse).

## Code map

| Where | What |
|---|---|
| `src/ModderLords.CompatSync/Testing/InGameTests.cs` | The hook API: `InGameTests`, `StepContext`, `StepWait`. TaleWorlds-free. |
| `.../Testing/StepRunner.cs`, `TestAgent.cs` | Runs a step's waits; reads control lines, writes results. TaleWorlds-free, linked into the tests. |
| `.../Testing/TestHost.cs`, `BuiltInSteps.cs` | The game's part: the switch, screenshot, versions; the built-in steps. |
| `src/ModderLords.Core/Testing/` | Suite parsing, the sequencer, the run folder, the report; built-in suites in `Suites/`. |
| `src/ModderLords.Core/Smoke/SmokeRunner.cs` | `WhileOnMap` and `ExtraLaunchArguments`: the test runs inside the smoke test's join. |
| `src/ModderLords.Cli/Program.cs` (`test`) | Ties it together. |
| `tests/ModderLords.Core.Tests/InGameTestHarnessTests.cs` | Suite parsing, waits, and the sequencer against the real agent through a real run folder. |

## Not done yet

- Not run in a real game. The first run is the stash suite on Coop 0.1.7: it should fail on `main` and pass with
  PR #263's change.
- Creating and caching a world per mod set (decision 1); for now a world is a `.sav` you put in `test-worlds`.
- "TEST MODE" in the server's player list (decision 6); the log lines are there.
- The delivery-quest, hero-level and Living Economy suites; a second player's game; an App button; reference-free
  registration for third-party mods.
