# In-game test harness (design)

Status: **design agreed in discussion with the maintainer, 2026-11-06; MVP not built yet.**

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

## The hook API (v1)

```csharp
using ModderLords.Testing;

InGameTests.Register("stash.putAndDone", StepSide.Client, StashSteps.PutAndDone);

static IEnumerable<StepWait> PutAndDone(StepContext ctx)
{
    var count = ctx.Arg<int>("count");
    // ... open the stash the way the town menu does, move items, Done ...
    yield return ctx.Until(() => StashCount() == count, TimeSpan.FromSeconds(30));
    yield return ctx.Settled(() => StashCount());     // matched, then unchanged for the quiet period
    ctx.Report("stashCount", StashCount());           // value for later steps and for compares
}
```

- A step is a coroutine on the game's main thread. Returning passes; throwing fails with the message;
  `ctx.Assert(cond, msg)` throws. Each step has a timeout (default 60 s, overridable in the plan).
- `ctx.Report(key, value)` puts a value in the result line; later steps get it as an argument (`"$stepId.key"`), and
  `compare` steps check two sides' values.
- `ctx.Dump(name, text)` writes evidence for the report.
- `InGameTests.ApiVersion = 1`; a plan that needs a higher version is refused with a clear result.

## Plans (suites)

```json
{
  "api": 1,
  "name": "stash",
  "world": "vanilla-sturgia-start",
  "steps": [
    { "id": "stash.prepare",        "side": "server",   "args": { "item": "grain", "count": 5 } },
    { "id": "stash.putAndDone",     "side": "client:1", "args": { "town": "town_S1", "count": 5 } },
    { "id": "stash.reopenAndCount", "side": "client:1", "args": { "times": 3, "expected": "$stash.putAndDone.stashCount" } },
    { "compare": "stash.count",     "sides": ["server", "client:1"] }
  ]
}
```

Steps run in order; the first failure stops the suite (rest "skipped") unless `"continueOnFail": true`. A step may
carry `"knownFailing": "<issue url>"`.

## MVP

1. Agent, API, file transport, test-mode gating; launcher sequencer, report with versions and evidence; CLI `test`.
2. World preparation: copy a named world fresh; creating and caching worlds for mod sets comes after the MVP.
3. **Stash suite** end to end, including the server/client compare. On Coop 0.1.7 it should fail against `main` and
   pass with PR #263's change: its first job is to verify that PR.
4. One tiny cross-side check (server sets a value, client sees it) to prove cross-side steps from the start.
5. Unit tests: plan parsing and validation, `$step.key` substitution, line codec, sequencer against a fake agent
   (pass, fail, timeout, no result / crash, skipped, known-fail, unexpected pass, API mismatch), settled-wait logic,
   report. Docs: how to write a step.

Follow-ups: delivery-quest, hero-level and Living Economy suites; world creation per mod set; second client; App
button; reference-free registration for third-party mods.
