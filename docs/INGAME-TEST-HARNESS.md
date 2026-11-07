# In-game test harness (design, for review)

Status: **proposal, 2026-11-06.** Nothing below is built yet. Read it, mark it up, and say go (or change it) before
the MVP is written.

## Why

Coop 0.1.7 started finishing the inventory screen's Done itself. Our `InventoryExchangeEvent` did it too, and town
stashes doubled. A player found that, not us. The existing smoke test (`ModderLords.Core.Smoke`) proves a client
joins and stays on the map; it proves nothing about gameplay. We want: after a Coop or mod update, one command runs
scripted checks in a real session (dedicated server + one client on this machine) and says pass or fail per check.

Checks it must be able to express:

1. Open a town stash, move items in, Done; close and reopen 3 times; the stash's item count is unchanged.
2. Complete a delivery-quest step; quest progress counts once.
3. Edit a hero's level from the host; the client sees the new level.
4. A Living Economy menu action on the client is carried out by the server.

(1) and (2) are single-side, (3) and (4) are cross-side: a step on one side, an assertion on the other.

## Shape

```
  launcher (ModderLords.Core.Testing)          shared run folder                  game processes
  ───────────────────────────────────          ─────────────────                  ──────────────
  SmokeRunner gets the session to OnMap  ──►   run.json      (plan, read-only)  ──► agent on server
  TestRunner writes run.json, then for         control.jsonl (launcher appends)     agent on client
  each step appends "go step N" and waits ◄──  results-server.jsonl  (agent appends)
  for a result line from that side        ◄──  results-client.jsonl  (agent appends)
  writes report.json + report.md
```

- **Launcher side** (`ModderLords.Core/Testing`, net10, pure and unit-tested): plan model, control/result line
  codec, the step sequencer, the report. `TestRunner` runs after `SmokeRunner` reaches "steady on map", reusing its
  launch, its log slices and its report folder. CLI: `ModderLords.Cli test --suite stash [--attach]`; the App gets a
  button later, not in the MVP.
- **In-game agent** (`ModderLords.CompatSync`, net472, *no Coop reference*): enabled only by the launch argument
  `/modderlords-test <run folder>` (server: passed the same way through its launch plan). Ticks from the submodule's
  `OnApplicationTick` (every frame, not the 3 s `Bridge.Tick`, so waits are precise), reads `control.jsonl`, runs the
  steps for its side, appends results.
- **Coop-touching steps** (e.g. "the session is a Coop client", "wait for the server's roster sync") live in
  `ModderLords.CompatSync.Coop` and register themselves through the same API, so the agent itself never needs Coop.

## Transport: files in a run folder (chosen)

`%ProgramData%\ModderLords\tests\<runId>\` — one writer per file, append-only JSON Lines.

Why files, not a pipe or local HTTP:

- Server and client are on the same machine for this harness, and both already write `ModLogs` there; a folder needs
  no port, no firewall prompt, no listener thread inside the game, nothing in net472 we don't already ship.
- A crash or a hung game leaves the evidence on disk: the results so far are the report's raw material, kept with the
  log slices `SmokeRunner` already saves.
- One writer per file + append-only means no locking protocol; readers tail by byte offset (`LogTail` already does
  this for logs).
- The server's console is still used as it is now (commands, `@DS@` events), not replaced.

Cost: polling (the agent checks the control file's length each tick, ~free; the launcher every 250 ms). Latency of a
step handoff is under a second, fine for steps that wait on game ticks anyway.

## The hook API (for our modules, `coop-compat/` modules and third-party mods)

Versioned contract: **`ModderLords.Testing` API v1.** A mod can use it two ways, and neither needs a reference to
Coop:

**A. With a reference** to `ModderLords.CompatSync.dll` (our modules, `coop-compat/` modules):

```csharp
using ModderLords.Testing;

InGameTests.Register(new InGameStep("stash.put", StepSide.Client, ctx => StashSteps.Put(ctx)));
```

**B. Without a reference** (third-party mods, which must load whether or not ModderLords is present): a public static
class whose name ends in `ModderLordsTests`, with public static methods of the shape

```csharp
// [side]_[name], returns IEnumerable<object> (a step that spans ticks) or void (one tick)
public static IEnumerable<object> Client_PutItems(IDictionary<string, object> ctx) { ... yield return null; ... }
```

found by a reflection scan of loaded assemblies when a run starts. `ctx` is a plain dictionary (args, shared state,
`"log"` → `Action<string>`), so the mod needs no type of ours. The step id is `<ModuleId>.<name>`.

Step semantics (same for A and B):

- A step is a coroutine run on the game's main thread. `yield return null` waits one frame; `yield return
  TimeSpan`/a `Func<bool>` (A only: `ctx.WaitUntil(...)`) waits; returning ends it passed; **throwing fails it** with
  the exception's message. `ctx.Assert(cond, message)` throws. A step times out (default 60 s, per-step override in the
  plan).
- `ctx.Set(key, value)` / `ctx.Get(key)` share values between steps **on the same side**. Across sides a step
  *publishes* a value (`ctx.Publish("level", 12)`), which goes into its result line, and the launcher passes it as an
  argument to later steps (`"args": {"expected": "$hero.edit.level"}`) — the launcher is the only thing both sides
  talk to.
- Versioning: `InGameTests.ApiVersion = 1`; the plan says which version it needs; an agent with a lower one refuses
  the run with a clear result line instead of misreading it. New optional fields only within v1.

## Plans (suites)

A suite is JSON, shipped with the launcher (`tests/suites/*.json` in the repo, copied to the output):

```json
{
  "api": 1,
  "name": "stash",
  "steps": [
    { "id": "game.waitOnMap",       "side": "client" },
    { "id": "coop.isClient",        "side": "client" },
    { "id": "stash.prepare",        "side": "server", "args": { "town": "town_EN1", "item": "grain", "count": 5 } },
    { "id": "stash.putAndDone",     "side": "client", "args": { "count": 5 } },
    { "id": "stash.reopenAndCount", "side": "client", "args": { "times": 3, "expected": "$stash.putAndDone.stashCount" } }
  ]
}
```

Steps run strictly in order; the first failure stops the suite (later steps are reported "skipped") unless the step
says `"continueOnFail": true`.

## The stash check (MVP), concretely

On the client, no UI clicking: the steps drive the same code the screen does.

- `stash.prepare`: teleport-free — give the main party N of an item (`PartyBase.ItemRoster.AddToCounts`) through the
  server (a cheat on the client would be overwritten by Coop's roster sync), so this step is **server-side**: find the
  player's party by the client's hero, add items. Then on the client wait until the party shows them.
- `stash.putAndDone`: open the stash the way the town menu does (`InventoryScreenHelper.OpenScreenAsStash(town's
  stash)`), move the items with `InventoryLogic.AddTransferCommand`, call `DoneLogic()`, close the screen. Publishes
  the stash's count and the party's count.
- `stash.reopenAndCount`: open/Done/close with no transfers, 3 times, waiting for Coop's roster sync between; assert
  the stash count equals `expected` each time and the party lost exactly N once.

On Coop 0.1.7 with `main` as it is today this should **fail** (stash doubles) and pass with PR #263's change: the
harness's first real job is to prove that PR.

The other three checks are the same pattern; they are follow-ups, not MVP: (2) server step completes a quest step,
client asserts the count; (3) server step sets a level and publishes it, client asserts; (4) client step runs the LE
menu action, server step asserts the state change.

## What gets unit-tested (net10, tests/ModderLords.Core.Tests)

Plan parsing and validation, `$step.key` argument substitution, the control/result line codec (shared source file,
linked into the net472 agent like `MiniJson.cs` is), the sequencer against a fake agent (pass, fail, timeout, crash /
no result, skipped steps, API version mismatch), the naming convention for reflection-found steps, and the report.

## Not in scope / open questions for the maintainer

1. **Server launch flag.** The server needs `/modderlords-test <folder>` (or an env var
   `MODDERLORDS_TEST_RUN`, which the launch log already lists). Env var is simpler to thread through Host mode; OK?
2. **Run against the GUI's server or CLI only?** MVP: CLI `test`, with `--attach` to a server the App is hosting (as
   `smoke --attach` does). App button later.
3. **Third-party discovery by naming convention (B)** vs. requiring a reference (A only). B costs a reflection scan at
   run start (only when a run is active). Keep both?
4. **Test mode never ships enabled**: the agent is inert unless the flag is present; test steps that cheat (give items)
   run only then. Steps live in the shipping DLLs (small), not a separate test module — OK, or a separate
   `ModderLords.TestAgent` module that is only installed for runs?
