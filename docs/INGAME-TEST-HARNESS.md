# In-game test harness

Status: **v1 built 2026-11-06 and run in game; v2 designed 2026-11-07, not built.** The v1 decisions are below,
then the v2 design, then how v1 runs and how to write a step.

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

## v2: modular suites, single-player runs, tests shipped in mods

Decided with the maintainer 2026-11-07, decisions 11–27; **not built yet.** Decisions 23–27 come from a real
failure while testing Modular Smithing 2 v0.9.49 by hand: the install copied the new DLLs but not the changed
`ModuleData`, so the mod's catalog failed to load and the game never reached the main menu. Every unit test had
passed, `mods.started` would have passed too, and both installs said v0.9.49. One tool, two uses:

- **Fast per-mod development testing**, in single player or Coop: `test --mod ModularArmor2 --single-player`.
- **The full compatibility matrix**: every suite that applies to a profile, recorded per mod, mod version, Coop
  version and game version.

### v2 decisions

| # | Decision | Why |
|---|---|---|
| 11 | **Suites declare what they need**: `requires` (module ids, each with an optional version range), `mode` (`singleplayer`, `coop` or `both`) and `continuity` (bool). An interaction suite can require several mods, e.g. `ModularArmor2` + `CoopWarehouses`. | A suite runs only where it means something; interaction bugs need both mods present. |
| 12 | **With no suite named, a run picks suites itself**: the core suite plus every suite whose `requires` the profile's enabled mods meet and whose `mode` fits the run. All of them go into **one launch**, not one launch per suite. | A launch costs minutes; the matrix is many suites. |
| 13 | **A core suite always runs first**: `game.joined` (Coop), `mods.started` on every side, the `hero.parity` compare (Coop) and the `sync-basics` checks (Coop), plus the checks of decisions 24–26. In single player the Coop-only ones are left out. | The cheapest checks catch the broadest failures (a mod that never started, a desync). |
| 14 | **Tests ship inside the mod, with no runtime dependency for players.** A mod, including a single-player mod that has nothing to do with ModderLords, must never need ModderLords or its compat layer to run. | The maintainer's hard requirement: a test hook must not become a dependency. |
| 15 | **A single-player run mode**: just the game, no Coop server. Steps run on side `game`. Same run folder, agent, waits and evidence. | Most mod development is single player, and a Coop launch is slow. |
| 16 | **A clean world per suite by default.** A suite marked `continuity` can use the built-in `game.saveAndReload` step: save, reload (in Coop: restart the server on that save and rejoin), and later steps check what survived. | Isolation by default; save/load bugs need a deliberate reload. |
| 17 | **Worlds are created and cached per mod set** (the mod list with each mod's install fingerprint, decision 23); this builds v1's decision 1. | Overhaul mods change world generation, so a vanilla `.sav` doesn't load or doesn't mean the same thing. |
| 18 | **Results are recorded across runs**: pass or fail per suite, against each mod's id, version and install fingerprint, the Coop version and the game version, in a history file with a summary table. | That history is the compatibility matrix. Later it could feed the Coodsz compat records; v2 only stores it. |
| 19 | **A suite can opt in to sharing a world** (`"world": "shared"`): it runs on the world the previous suite left, with no reset. Fresh stays the default; the core suite and `continuity` suites always get a fresh world. A shared suite must not assume starting values: it reads "before" and checks the change. The report says which suites shared a world. | In Coop a reset is a server restart and a rejoin, about a minute per suite; most suites touch state no other suite reads. |
| 20 | **A test DLL names its entry type with an assembly attribute** from `ModderLords.CompatSync` (below). | Explicit and in one place; no scan of every type, which throws when one refers to something missing. |
| 21 | **`--mod X` stays narrow**: the core suite, X's own suites and the suites that require X. `--with-others` adds every other applicable suite. The report marks a suite that also failed in the last recorded run without X as "already failing", so a new failure points at X. | The development loop stays fast; the core suite still catches another mod going quiet or a desync, and the full run (no `--mod`) is the one before a release. |
| 22 | **Worlds are created by code**, stepping through character creation with the game's own creation state and taking the default choice on each page (Worlds, below). When that gets stuck, it fails naming the page, and the fallback is a world made by hand and recorded with `test world save NAME`. | A scripted screen path breaks on the overhaul mods the cache is for; the game's own page sequence doesn't care which mod added a page. |
| 23 | **Every enabled mod gets an install fingerprint** before launch: a hash of `SubModule.xml`, `bin\` and `ModuleData\`, with other folders by size and date. It goes into the report, the history and the world cache key. Results compare on fingerprints, not version strings. A mod may ship its own file list; a mismatch with the installed copy fails the run before launch, naming the files. | A test build and the release can share a version; the MS2 install that broke said the same version as the one that worked. Hashing art folders would make every run slow. |
| 24 | **The copy that loaded must be the copy ModderLords picked**, on every side, read from the game's own "Loading assembly" log lines. The core suite also compares fingerprints across sides (`mods.fingerprint`). Several copies of one mod are not a failure in themselves: `ModuleSelector` already picks one, and a profile can pin one. | A stale Workshop copy beside a local one was only visible in a log the mod writes itself. A server on another build of a mod than the player is a desync waiting to happen. |
| 25 | **Start-up errors are failures, even when the mod started.** Time limits to the main menu (single player) and to `game.joined` (Coop), with a screenshot and the last log lines on a miss. In test mode every on-screen message and pop-up is captured, and the core check `game.noErrors` fails on default error patterns plus a suite's or mod's own `errorPatterns`. New lines in the game's error log are kept as evidence only. | MS2's patches and behaviours were in place, so `mods.started` passed; its data load failed afterwards and said so on screen. A game stuck on load must be a fast, explained red, not a hung run. Vanilla's error log is too noisy to fail on. |
| 26 | **A mod that ships tests can register `<ModId>.health`**, reporting its own load status as values (MS2: catalog loaded, part count, catalog hash). The core suite runs every registered health step on every side and compares them. | Cheap, and it sees data a mod loads after start-up, which `mods.started` can't. |
| 27 | **Test what ships**: `--package <zip or folder>` installs a mod the way a player would (the whole folder, never DLLs alone) into a scratch copy and runs against it. Saves from each released version are kept as upgrade worlds, and upgrade suites load them with the new build (`continuity`). Once `--package` exists, `docs/RELEASING.md` gets a step that runs the full run against the exact zip and records its SHA-256; mods' own release steps belong in their repos. | The MS2 failure was a hand install that left out data files. Old saves must survive an update, and that is only unit-tested today. |

### The core suite (decisions 13 and 24–26)

| Check | Sides | Fails when |
|---|---|---|
| Time limit to the main menu / `game.joined` | every side | the game doesn't get there in time (stuck loading, a crash, a blocking pop-up) |
| `mods.started` | every side | a mod's code loaded but it shows no patch, behaviour or model |
| loaded copy | every side | the game loaded a different copy of a mod from the one ModderLords picked |
| `mods.fingerprint` compare | Coop | the server and the player run different builds of a mod |
| every `<ModId>.health` compare | every side | a mod reports a failed load, or the sides disagree |
| `game.noErrors` | every side | an on-screen message or pop-up matches an error pattern |
| `hero.parity` compare, `sync-basics` | Coop | the player's hero differs between the sides; a change on the server doesn't reach the player |

Decision 14 replaces v1's decision 5 for third-party mods: the reference-based API stays, but it now lives in a DLL
that the game never loads by itself.

### How a mod ships tests (decision 14)

```
<Module>\
  SubModule.xml                     lists only the mod's own DLLs, as today
  bin\Win64_Shipping_Client\ModularArmor2.dll
  ModuleData\ModderLordsTests\
    armor-basics.json               suites (the v1 format plus requires/mode/continuity)
    ModularArmor2.Tests.dll         optional; NOT listed in SubModule.xml
    files.json                      optional: the mod's own file list with hashes (decision 23)
```

- **The game never loads the test DLL.** It isn't in `SubModule.xml` and isn't under `bin\`, so neither the game nor
  a loader that scans `bin\` picks it up. A player who installs the mod gets nothing extra running.
- **ModderLords' agent loads it by hand, in test mode only**, as `SyncSubModule` loads the Coop adapter
  (`Assembly.LoadFrom` next to a known file). Test mode always adds `ModderLords.Compat` to the launch, so the
  test DLL's reference to `ModderLords.CompatSync` resolves.
- **The test DLL names its entry point** with one line, `[assembly: InGameTestsEntry(typeof(Steps))]`
  (`InGameTestsEntry` is in `ModderLords.CompatSync`, which the DLL references anyway for `InGameTests.Register`).
  The agent loads only that type and calls its public static `Register()`, which registers steps as v1 steps do. The
  DLL may reference the mod's own DLL to call its code. Decided 2026-11-07 (decision 20).
- **Its failures are loud**: a DLL with no entry, or whose entry registers no steps, fails the run with that message.
  A DLL that fails to load on one side (a client-only mod on the server) has its steps skipped there, with the reason,
  instead of failing the run. A DLL built for a different harness API version is refused, saying so.
- **One DLL for both sides.** The dedicated server loads the same file, as it loads `ModderLords.CompatSync`, so the
  DLL targets what `ModderLords.CompatSync` targets.
- **Discovery** scans the enabled modules' folders for `ModuleData\ModderLordsTests\`. The suites' JSON is read by the
  launcher; the DLL is loaded by each game's agent when the run starts.
- **A health step** (decision 26): the test DLL may register `<ModId>.health` with side `Any`, reporting what the
  mod loaded as values. The core suite finds and runs it; no suite has to name it.
- **Suite ids are namespaced by module** (`ModularArmor2/armor-basics`) and step ids stay `area.verb`; a mod's step
  id that clashes with another's fails the run up front instead of silently replacing it (a change from v1).

### Suite file additions

```jsonc
{
  "api": 2,
  "name": "warehouse-armor",
  "requires": [ { "id": "ModularArmor2", "version": ">=1.2" }, { "id": "CoopWarehouses" } ],
  "mode": "coop",                  // singleplayer | coop | both (default both)
  "continuity": false,             // true allows game.saveAndReload
  "world": "auto",                 // auto (default): a fresh copy of the cached world for this mod set;
                                   // shared: the previous suite's world, no reset; upgrade: each kept
                                   // upgrade world in turn (decision 27); or a named test world
  "errorPatterns": [ "\\[MArmor\\].*failed" ],   // added to game.noErrors' defaults (decision 25)
  "steps": [ ... ]                 // side: game (single player), server, client:N (Coop)
}
```

- A `both` suite names `game` for the side that changes the world and `player` for the side that acts as the player.
  In single player both mean `game`; in Coop they mean `server` and `client:1`. Compares across sides are skipped in
  single player and say so in the report.
- `api: 1` suites still load: they mean `mode: coop` (or `singleplayer` when every step is on `game`), no
  `requires`, no continuity. They run by name only: with no suite named, a run picks `api: 2` suites, since a v1
  suite says nothing about where it applies.
- A step in a `both` suite can carry `"mode": "coop"` or `"singleplayer"` to run only in that mode (a join, a gold
  transfer between sides).
- The suite format and the in-game step API are versioned apart: `run.json` asks the games for step API 1 whatever
  the suite's `api`.

### Running (decisions 12 and 15)

```powershell
ModderLords.Cli test --profile "My Host"                                   # core + every applicable suite
ModderLords.Cli test --profile "My Mods" --single-player                   # the same, single-player suites
ModderLords.Cli test --profile "My Mods" --mod ModularArmor2 --single-player [--keep-open-on-fail]
ModderLords.Cli test --profile "My Mods" --mod ModularArmor2 --package MA2-1.3.zip   # test the archive itself
ModderLords.Cli test --profile "My Host" --suite stash                    # v1: one named suite, still works
```

- `--package` installs the zip or folder as a player would, into a scratch copy of the module folders (and the
  server's, in Coop), so the installed mods are untouched; the report and history record the archive's SHA-256.

- `--mod X` runs the core suite plus the suites X ships and the suites that require X; `--with-others` adds every
  other applicable suite (decision 21).
- `--keep-open-on-fail` leaves the game (and in Coop, the server) running after the first failure, so the developer
  can look; the run's report is written first.
- **Single player** launches the profile's game with `ModderLords.Compat` added and `/modderlords-test`, and no Coop
  module. The agent loads the world itself from the main menu (game code, as the Load Game screen does), since there
  is no join to wait for. The steps run on side `game`.
- **One launch, many suites.** The launcher runs the suites in order. Before each suite that needs a fresh world it
  resets the world: in single player the agent reloads the fresh copy; in Coop the server restarts on the fresh copy
  and the player's game rejoins (the same machinery as `game.saveAndReload`). Shared suites (decision 19) are run
  together after the fresh ones, so one reset covers them all. The report lists each suite's outcome separately.

### Worlds (decisions 16 and 17)

- The cache key is the sorted list of enabled modules with their install fingerprints (decision 23), hashed, so a
  rebuilt mod with an unchanged version still gets its own world; the world lives in
  `<data>\test-worlds\auto-<hash>.sav`, with a small `.json` beside it recording the list it was made with.
- On a cache miss the harness creates the world (decision 22), by code and without touching the screens:
  1. Start a new Sandbox campaign as the main menu's Sandbox button does (no tutorial).
  2. Step through character creation with the game's own creation state: on each page take the first or default
     choice and move on. The agent doesn't need to know the page, so pages an overhaul mod adds are handled like
     vanilla ones, as long as they follow the game's page pattern. Required fields get fixed values (a clan name).
  3. Dismiss or detect pop-ups after creation (a mod's campaign options, a welcome message), as when loading a save.
  4. On the map, save under the mod set's key. The report lists each page it passed and what it chose. That run is
     reported as world creation, not as a test.
- **When creation gets stuck** (a mod's page with its own logic, or an input the agent can't guess), it fails naming
  that page. The fallback is manual: make the world by hand once in that mod set, and `test world save NAME`, run
  while the game is open, records it into the cache under that mod set's key.
- Worlds are not reproducible (world generation is random), so each is created once and kept; the history records
  which cached world a result came from, since results compare only on the same world.
- A named world (`"world": "vanilla-1"`, `--world`) still works as in v1, and skips the cache.
- **Upgrade worlds** (decision 27): `<data>\test-worlds\upgrade-<ModId>-<version>.sav`, one kept from each released
  version of a mod. An upgrade suite (`"world": "upgrade"`, `continuity`) runs once on each, with the new build, and
  checks that the mod's records survived the load.

### Results history (decision 18)

- Every run appends one line per suite to `<data>\tests\history.jsonl`: run id, time, suite, outcome, game version,
  Coop version (or none), ModderLords version, every enabled mod's id, version and install fingerprint, and with
  `--package` the archive's SHA-256.
- `ModderLords.Cli test --history` prints the summary table: one row per suite and mod set, the last outcome, and
  when it last passed. The run folders stay the evidence.

### Questions settled with the maintainer

1. ~~Clean world per suite in one launch~~: decided 2026-11-07, decision 19 (sharing is opt-in, fresh is the default).
2. ~~How the agent finds a test DLL's entry point~~: decided 2026-11-07, decision 20 (an assembly attribute).
3. ~~World creation~~: decided 2026-11-07, decision 22 (by code, with manual capture as the fallback). Loading a
   named save and creating a world by code are both unproven; the first build step is a spike of both.
4. ~~What `--mod X` runs~~: decided 2026-11-07, decision 21 (narrow, plus `--with-others`).

### Build order (proposed)

1. Spike: single-player launch, the agent loading a named save, and creating a world by code through character
   creation. **Built and passing in game 2026-11-07** (`sp-world`, the world steps, the `game` side); see "Single
   player" below. Not yet tried with mods that add creation pages.
2. `requires`/`mode`/`continuity`, suite selection and the core suite; multi-suite runs with a world reset.
   **Selection, the core suite and multi-suite runs built and passing in game 2026-11-07** (see "Several suites in
   one run" below); a fresh world in Coop still costs a launch. **Install fingerprints, the loaded-copy check and
   `mods.fingerprint` built and passing in game 2026-11-08** (decisions 23 and 24; a mod's own file list waits for
   step 3, and the history for step 4).
   With it, the cheap core checks: install fingerprints, the loaded copy, `mods.fingerprint`, the time limits and
   `game.noErrors` (decisions 23–25).
3. Tests shipped in mods: discovery, loading the test DLL, namespaced ids, `*.health` steps. Modular Smithing 2 is
   the pilot: single player first, with a Coop adapter, so it covers decisions 14, 15 and 11 at once. Its suites:
   `health` (both), `ammo`, `tier-preview` (single player), `heal-on-load` (single player, continuity) and
   `coop-craft` (Coop). Its repo is private, which doesn't matter: players never get its test DLL.
4. The world cache, then `game.saveAndReload`, then the history file and table.
5. `--package` and upgrade worlds, then the release step in `docs/RELEASING.md` (decision 27).

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

Built-in suites: `stash` (the 0.1.7 stash doubling), `sync-basics` (gold given on the server reaches the player),
`hero-level` (a level set with the character editor's `modderlords.edit` command reaches the player),
`both-perks` (with the both-perks rule on, a perk confirmed on the server gives its pair on both sides, and both count
the same max hit points) and `parity` (changes nothing: every mod that loaded also started, on both sides, and the
player's hero reads the same on both).
`stash` uses `town_EN1` and `grain`, both vanilla.

### Several suites in one run (v2 build step 2)

```powershell
ModderLords.Cli test --profile "My Host"                                   # Coop: core + every suite that applies
ModderLords.Cli test --profile "My Mods" --single-player [--world NAME]    # the same in single player
ModderLords.Cli test --profile "My Mods" --single-player --mod ModularArmor2 [--with-others]
```

- With no `--suite`, the run takes the core suite (`core.json`) and every `api: 2` suite whose `mode` fits and whose
  `requires` the profile's enabled mods meet (versions as they would load). It prints the suites it picked and why
  each other one was left out. `--mod X` keeps only suites that require X; `--with-others` keeps the rest too.
- The core suite runs both ways: `mods.started` and `mods.fingerprint` on every side; in Coop also the
  `mods.fingerprint` and `hero.parity` compares and gold given on the server reaching the player.
- **Single player:** one launch. Before the first suite the run creates its world (`mltest-run`), or loads
  `--world NAME` (copied fresh into the game's saves as `mltest-NAME`); before each later suite it goes back to the
  main menu and loads that world again. A suite with `"world": "shared"` skips the reload.
- **Coop:** the run's setup waits for the join and stops campaign time before the first suite of a launch. A suite
  that needs a fresh world starts a new launch (the server starts on a fresh copy); shared suites join the launch
  before them. Restarting the server inside one launch comes later.
- Step names are prefixed with their suite (`core/me`), so suites can name steps alike. A failure skips the rest of
  its own suite; the next suite still runs. The report groups steps by suite and ends with `suites: core PASS, ...`.
- **First runs, 2026-11-07 (Harness profile, vanilla):** single player picked `core` and passed in 54 s (world
  created, `mods.started`, `whoAmI`); Coop on `harness-vanilla` picked `core` and passed all 11 steps in 18 s after
  the join. No mod ships `api: 2` suites yet, so neither run had a second suite to reset for; build step 3 adds them.

### Single player (v2 build step 1)

A suite whose steps are all on side `game` runs in single player:

```powershell
ModderLords.Cli test --profile "My Mods" --suite sp-world [--keep-client] [--keep-open-on-fail]
```

- The profile's game is started with `ModderLords.Compat` added and Coop left out (in memory; the profile on disk is
  unchanged), and `/modderlords-test` on side `game`. No server, no join.
- The run starts at launch, so the game has up to 10 minutes to load and announce itself.
- The suite makes or loads its own world from the main menu with `game.createWorld` / `game.loadSave` (below).
- Built-in suite: `sp-world` creates a world by code, saves it, goes back to the main menu, loads it again and checks
  it is the same hero, then runs `mods.started`.
- **First runs, 2026-11-07 (game v1.4.8, vanilla):** `sp-world` passes in 70 s. Creation passed all 7 pages: culture,
  face, the six life-story menus, banner, clan name, review and options (`creation-pages.txt`). On the way: a page's
  view-model is found from its view, not by its page index (that isn't always set); the face and banner editors have
  no stage view-model and advance through their view; and `mods.started` ignores `ModderLords.Compat` in single
  player, where the run adds it and it has nothing else to start.

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
- **Check against the engine, not against your own numbers.** Where a mod predicts what the engine will do (a tier,
  a price, a stat), do the thing and compare the prediction with what the engine produced, e.g. a crafting preview's
  tier against `ItemObject.Tier` on the item made.
- **Prefer property checks** over simulating play: "every crafted ammo item has missile speed 10, like native ammo"
  catches a whole class of bugs without a battle.

Built-in steps (`src/ModderLords.CompatSync/Testing/BuiltInSteps.cs`): `game.onMap`, `game.pause`, `game.whoAmI`
(reports `heroName`, `hero`, `party`; find a player's hero on another side by `heroName`: its StringId is local),
`game.coopLog` (writes a line through Coop's own logger and checks it reaches Coop's log file; reports the file, the
logger's sinks, and in a test run the stack that first built Coop's logger), `game.joined` (waits for Coop's own
`ClientCampaignReady`, the join finished; every suite starts with it, and the launcher no longer adds a fixed wait),
`party.toTownGate` (server: Coop's own `coop.debug.mobile_party.move_to_settlement ... true` puts the party at the gate;
Coop's server refuses a town entry from further away), `settlement.enter` (the player's party enters a town, as
arriving there does),
`party.settlement`, `hero.addGold`, `hero.gold`, `hero.adminLevel` (server: the character editor's
`modderlords.edit … level=N` command), `hero.level`, `hero.addPerk` (server: `HeroDeveloper.AddPerk`, as Coop's
character screen does there), `hero.hasPerk`, `hero.maxHitPoints`, `rules.set` (server: turns a ModderLords gameplay
rule on or off, e.g. `BothPerks`), `party.addItem`, `party.itemCount`, `stash.count`, and
`stash.deposit` / `stash.reopen` (both open the stash as the town menu does, so the party must be in the town).
`game.whoAmI` also takes an optional `heroName` and fails if the hero is someone else.

World steps (`WorldSteps.cs`), from the main menu, for single-player runs:

- `game.createWorld` (`name`, optional `overwrite`): starts a new Sandbox campaign as the main menu's Sandbox button
  does, skips the intro video, and presses Next on each character-creation page as its own Next button does: through
  the page's view-model (found from `CurrentStage.Listener`, the page's view), or through the view itself on a page
  with no stage view-model (the face and banner editors).
  When a page can't advance, it takes the first entry of each list the page offers (through the page's own selection
  method) and fills an empty name field, then tries again; a page that still can't advance, or has no view-model,
  fails the step, naming it. Saves under `name` once on the map. Reports `save`, `heroName`, `culture` and `pages`;
  `creation-pages.txt` lists each page and whether a default was chosen.
- `game.loadSave` (`name`): loads a save as the Load Game screen does, without its pop-ups. The module differences
  that screen would warn about are logged and counted (`moduleWarnings`).
- `game.toMainMenu`: leaves the campaign without saving.

Parity steps (`ParitySteps.cs`), for what quietly differs between the sides:

- `mods.started`: every community mod whose code loaded shows it started: a Harmony patch, a campaign behaviour or a
  game model from its own DLLs. A mod can load, fail inside its own start-up, catch the error and go quiet; Both
  Perks does that on a dedicated server, so the player's game grants perks the server never has. `value` is the quiet
  mods or `none`; `ignore` lists mods allowed to show nothing. `mods-started.txt` has each mod's evidence.
- `mods.fingerprint`: hashes every community mod folder this game loaded (`src/Shared/ModuleFingerprint.cs`: data
  and this side's code by content, `SubModule.xml`, every other file by size) and checks each against the copy
  ModderLords picked, which the launcher hashed before launch into `expected-fingerprints-<side>.txt`. A different
  copy (a stale Workshop one, an install that left out changed data) fails, naming the mod and both folders. The server
  is checked on data only: its launch can leave a mod's DLLs out by role. `value` is one hash over the mods' data, so
  the core suite's compare fails when the server and the player run different data. `mods-fingerprint-<side>.txt`
  has every mod's fingerprint and folder; a file a launch view lists but cannot read counts as unreadable.
- `hero.parity`: one line a compare can check: level, health and its maximum, gold, and the attributes, skills, focus
  and perks (each counted and hashed, so a different set of the same size still differs). The full values are in
  `hero-parity-<side>.txt` for the diff.

Steps that need Coop's types go in `ModderLords.CompatSync.Coop` and register the same way; nothing in the agent
references Coop.

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

## What the first runs taught

The stash suite passes on Coop 0.1.7 with PR #263's change (11 real runs; the last two clean). Each failure on the way
was the harness, and each lesson is now in it:

- **Wait for Coop's join, not the map.** A change sent while the server still sends the join data is lost:
  `game.joined` waits for Coop's own `ClientCampaignReady`.
- **Do what the player does, exactly.** The stash is opened from inside the town (Coop's server refuses an entry from
  further than the encounter distance, so `party.toTownGate` comes first), and Done is `CloseScreen(false)` alone:
  that runs `DoneLogic` itself, and calling both sent Coop a second, stale trade that emptied the stash again.
- **A player's own hero is not in `Campaign.Characters` on their game**; steps find it by name through `Hero.MainHero`.
- **Coop's own log is empty in every run** (its logger is built before Coop sets up its log file). `game.coopLog`
  is known-failing on that, and each run keeps a copy, `coop-<side>.log`, in the run folder.
- **Trace instead of guessing.** `game.trace` logs Coop's trade and item-roster messages on both sides; it found the
  double Done in one run.

## Not done yet

- A run that shows the stash suite failing without PR #263. This branch cannot build the Coop adapter against the
  installed 0.1.7 (`HeroLevelChanged`, fixed by #263), and the stash put-back lives in that adapter.
- Creating and caching a world per mod set (decision 1); for now a world is a `.sav` you put in `test-worlds`.
- "TEST MODE" in the server's player list (decision 6); the log lines are there.
- The delivery-quest and Living Economy suites; a second player's game; an App button.
- Everything in v2 (above).
- A battle step: start a custom battle with given equipment, run until it ends or a time limit, read hits and kills.
  For behaviour that property checks can't reach (AI archers actually hitting); the expensive one, so it waits.
