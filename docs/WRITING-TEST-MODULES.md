# Writing test modules

A guide for an agent (or a person) adding tests to ModderLords' in-game test harness. The design and the reasons
behind it are in `docs/INGAME-TEST-HARNESS.md`; this file is how to do the work.

**In one paragraph:** tests run *inside* the game, as part of ModderLords.Compat, so a test step can do anything the
game's own code can do, without a mouse. Steps are C# coroutines registered by id. Suites are JSON lists of steps.
A test module is a folder of suites for one area (`stash`, `combat`, `smithing`), and a run loads only the modules
it is asked for, plus `core`. Everything ships with ModderLords; no other mod carries test files.

## 1. Where things are

| What | Where |
|---|---|
| Test modules (suites + `module.json`) | `src/ModderLords.Core/Testing/Modules/<module>/` (embedded in the launcher) |
| General step code | `src/ModderLords.CompatSync/Testing/*.cs` (`BuiltInSteps`, `ParitySteps`, `WorldSteps`, `ErrorSteps`) |
| The step API | `src/ModderLords.CompatSync/Testing/InGameTests.cs` (`InGameTests.Register`, `StepContext`, `StepWait`) |
| A module's own step DLL (for steps that call a mod's code) | `src/ModderLords.Tests.<Module>/`, built into the launcher's `compat\` folder |
| Launcher side (suites, selection, sequencer, report) | `src/ModderLords.Core/Testing/` |
| The run (picking suites, launching, the reports) | `src/ModderLords.Coop/Testing/InGameTestRun.cs`, used by the CLI's `test` and the app's Tests tab |
| The Tests tab | `src/ModderLords.App/ViewModels/TestsViewModel.cs`, its `TabItem` in `MainWindow.xaml` |
| Unit tests | `tests/ModderLords.Core.Tests/InGameTestHarnessTests.cs`, `ModuleFingerprintTests.cs` |
| Run folders (results, evidence) | `%LOCALAPPDATA%\ModderLords\tests\<runId>\` |

## 2. The loop

```powershell
# What exists, as JSON (modules, suites, the steps each names, needed mods, and whether this profile can run them)
ModderLords.Cli test --list --profile "My Mods"

# Fast: one area, single player, leave the game open when something fails
ModderLords.Cli test --profile "My Mods" --modules stash --single-player --keep-open-on-fail

# Then Coop, the same modules
ModderLords.Cli test --profile "My Host" --modules stash --world harness-vanilla

# One suite by name (module/suite, or the suite's own name when unique), or a suite file by path
ModderLords.Cli test --profile "My Host" --suite stash/stash
ModderLords.Cli test --profile "My Host" --suite C:\scratch\try-this.json
```

- **In the app:** the Tests tab lists the modules (with what each checks and needs), runs the ticked ones in single
  player or Coop, and shows a pass or fail per suite and per step. It is the same run as the CLI's; an agent uses the
  CLI.
- **Build first:** `dotnet build src\ModderLords.Cli\ModderLords.Cli.csproj` builds the in-game modules into the
  CLI's `compat\` folder, and the run installs the client copy of ModderLords.Compat itself. Run the CLI from
  `src\ModderLords.Cli\bin\Debug\net10.0\`.
- **Exit code** is 0 only when every step passed (or failed as marked known-failing). Loop on it.
- **Read the run folder,** not only the console:
  - `report.json`: every step's outcome, message, values and log lines (`report.txt` is the same for people).
  - `results-<side>.jsonl`: what each game answered, line by line.
  - `evidence\<seq>-<step>-<side>\`: the step's dumps (`ctx.Dump`), `screenshot.bmp` for a failed step on a
    player's game, `messages-<side>.txt` (every on-screen message), `game-errors-<side>.txt` and
    `game-log-tail-<side>.txt` (the game's own logs), `mods-fingerprint-<side>.txt`.
  - `coop-<side>.log`, `server-console.log` and the smoke report's log slices, in Coop.
- **A one-off suite file** (`--suite <path>`) is the quickest way to try a step before giving it a home in a module.
- **Single player** takes about a minute (it creates a world by code each run). **Coop** takes a few minutes per launch
  (shared suites all run in one) and needs a Host profile, the dedicated server, and Coop's automatic join (no server password).
- **Never run while someone is using the machine:** a run launches the game, which takes the screen. Check first
  (the last keyboard/mouse input, or ask), and close any running Bannerlord.

## 3. What a step can reach

The agent runs on the game's main thread, every frame, inside the same process as the game and every mod. A step
is ordinary C# with the game's assemblies referenced, so the limit is the game's managed API, not the screen.
**Proven** below means a step in this repository already does it and has passed in a real game; **available** means
the API is there and the approach is sound, but no step does it yet: prove it with a one-off suite first.

### Game state and screens

- **Where the game is:** `GameStateManager.Current.ActiveState` is the screen's state: `InitialState` (main menu),
  `MapState` (campaign map), `CharacterCreationState`, `VideoPlaybackState`, `MissionState` (a battle or scene).
  `Campaign.Current` is null outside a campaign. *Proven* (`game.onMap`, `WorldSteps`).
- **Starting, loading, leaving a campaign:** `MBGameManager.StartNewGame(new SandBoxGameManager(...))`,
  `MBSaveLoad.LoadSaveGameData(name)`, `MBGameManager.EndGame()`, `Campaign.Current.SaveHandler.SaveAs(name)`.
  SandBox types are reached by reflection, since the agent references only the core assemblies. *Proven*
  (`game.createWorld`, `game.loadSave`, `game.toMainMenu`).
- **Any screen's buttons, without clicks:** a Gauntlet button is bound to a method on the screen's view-model (an
  `Execute...` method, or `OnNextStage`, `HandleDone`...). Find the view-model and call that method. It is the click,
  minus the mouse. *Proven*: character creation goes through every page by calling each page's own
  `CanAdvanceToNextStage()` / `OnNextStage()` (the view-model is reached from `CurrentStage.Listener`, the page's
  view; pages with no stage view-model, the face and banner editors, through the view's own `NextStage()`).
- **Game menus** (town, village, encounter menus): `Campaign.Current.CurrentMenuContext.GameMenu`,
  `GameMenu.SwitchToMenu(id)`, and a menu option's own consequence. *Proven* for entering a town and opening the stash
  as the town menu does (`settlement.enter`, `stash.deposit`).
- **Inventory and trade screens:** `InventoryScreenHelper.OpenScreenAsStash(...)`,
  `GetActiveInventoryState().InventoryLogic` to move items, `InventoryScreenHelper.CloseScreen(false)` for Done.
  *Proven* (`stash.deposit`, `stash.reopen`).
- **Pop-ups (inquiries):** `InformationManager.OnShowInquiry` reports each one; its `InquiryData` holds the
  affirmative and negative actions, so a step can answer it. Capturing is *proven* (`game.noErrors`); answering is
  *available*.
- **On-screen messages:** `InformationManager.DisplayMessageInternal` (every message), `DisplayMessage` (show one).
  *Proven* (`game.noErrors`, `game.showMessage`).

### Campaign data: read and change anything

- **Heroes:** `Hero.MainHero` (on a player's game, the player's own; it is *not* in `Campaign.Characters` there, so
  find a player's hero on another side by name), `Hero.AllAliveHeroes`, `Hero.DeadOrDisabledHeroes`, gold, level,
  `HeroDeveloper` (attributes, skills, focus, `AddPerk`), hit points and their maximum. *Proven* (`hero.*`).
- **Parties and rosters:** `MobileParty.MainParty`, `MobileParty.All`, `ItemRoster` (items), `MemberRoster` and
  `PrisonRoster` (troops), position, current settlement. *Proven* (`party.*`).
- **Items and objects:** `MBObjectManager.Instance.GetObject<ItemObject>("grain")`, likewise for `CharacterObject`,
  `Settlement`, `CultureObject`, any registered type. *Proven* (`party.addItem`).
- **Settlements, clans, kingdoms:** `Settlement.All`, `Town`, stashes, owners, `Clan.All`, `Kingdom.All`, relations,
  wars. Reading is *proven* (`stash.count`); changing them goes through the game's actions (next bullet).
- **The game's own actions,** for changes the game itself makes the same way: `GiveGoldAction`,
  `ChangeRelationAction`, `DeclareWarAction`, `MakePeaceAction`, `ChangeOwnerOfSettlementAction`,
  `AddHeroToPartyAction`, `KillCharacterAction` and the rest of `TaleWorlds.CampaignSystem.Actions`. Prefer an action
  to setting a field: actions fire the campaign events other mods and Coop listen to. *Available* (gold is *proven*).
- **Time:** `Campaign.Current.TimeControlMode` (stop it before reading numbers; `game.pause`), `CampaignTime.Now`.
  *Proven*.
- **Console commands, the game's and every mod's:** `CommandLineFunctionality.CallFunction(command, args, out found)`
  runs any `[CommandLineArgumentFunction]` command, as the developer console would: the game's `campaign.*` cheats,
  Coop's `coop.debug.*`, a mod's own. *Proven* (`party.toTownGate` runs `coop.debug.mobile_party.move_to_settlement`;
  `hero.adminLevel` runs ModderLords' `modderlords.edit`).

### Creating things and starting fights

- **Parties and armies:** `MobileParty.CreateParty`, `CustomPartyComponent.CreateCustomPartyWithTroopRoster`,
  `BanditPartyComponent.CreateBanditParty` create a party anywhere, with any troops (`MemberRoster.AddToCounts`).
  *Available* (the calls exist and ModderLords' Fourberie code hooks them; no step spawns one yet).
- **Heroes:** `HeroCreator` makes lords, companions and wanderers. *Available*.
- **Encounters and battles:** `EncounterManager.StartPartyEncounter(PartyBase.MainParty, other.Party)` meets a party
  as the map does; the encounter menu's Attack option starts the battle; `PlayerEncounter` and `MapEvent` hold its
  state and result. Auto-resolve settles it without a scene (casualties, loot, XP: fast and repeatable). A real
  battle opens `MissionState`. *Available*.
- **Inside a battle (mission):** `Mission.Current`, `Agent.Main`, `Mission.Agents`; any agent, the player's
  included, can be handed to the AI (`agent.Controller`); a `MissionBehavior` added to the mission sees every hit,
  kill and removal (`OnAgentHit`, `OnAgentRemoved`) to count them; time can be sped up. Check broad outcomes ("the
  archers landed hits", "the battle ended"), never exact numbers: battles vary. *Available*.
- **Custom battles** from the main menu (the CustomBattle module) test equipment and combat without a campaign.
  *Available*.

### Watching what happens

- **Campaign events,** without patching anything: `CampaignEvents.<Event>.AddNonSerializedListener(owner, handler)`
  for hero levelled up, item looted, map event ended, settlement entered, daily and hourly ticks... Remove the
  listener when the step ends. *Available*.
- **Harmony patches** to observe or count calls to any method: from a module's own DLL, which can reference
  Lib.Harmony at compile time (Bannerlord.Harmony, which nearly every modded profile loads, provides it at run
  time). The general agent itself reads Harmony by reflection only (`mods.started`). *Proven* for reading patches;
  patching from a test module is *available*.
- **Coop:** Coop's own join-finished signal (`game.joined`), its logger (`game.coopLog`), and its network messages
  (`game.trace` logs trade and item-roster messages on both sides; it found the double Done). All by reflection:
  nothing in the agent references Coop. *Proven*.
- **Files and logs:** the game's logs (`%ProgramData%\Mount and Blade II Bannerlord\logs\rgl_log_<pid>.txt`), a mod's
  own log files, the run folder (`ctx.RunFolder`, for files the launcher put there). *Proven*.

### What a step cannot reach

- **Anything outside the game process:** the TaleWorlds launcher, Windows dialogs, crash pop-ups, the Steam overlay.
  Those need screen control from outside (the computer-use tool), not a step.
- **Input itself:** a bug in keyboard focus, drag-and-drop, hotkeys or the mouse. Calling the method skips the input
  that is broken.
- **How things look:** layout, clipping, graphics. A step can take a screenshot (`TaleWorlds.Engine.Utilities
  .TakeScreenshot`; failed steps on a player's game get one automatically), but someone has to judge it.
- **The engine's native internals** beyond what the managed API exposes (rendering, physics internals).
- **The other side's memory:** a server step cannot read the player's game directly. Report values on each side and
  compare them (a compare step), or pass them forward with `$step.key`.

## 4. Rules that came from real failures

- **Do exactly what the player does,** by calling the method the button calls, and nothing more. The stash's Done is
  `InventoryScreenHelper.CloseScreen(false)` alone: it runs `DoneLogic()` itself, and a step that called both made Coop
  send a second, stale trade, which emptied the stash again. Read the view-model's bound method before guessing.
- **The server owns the world in Coop.** A change made on a player's game is overwritten by Coop's sync. Change the
  world on side `server` (or `game` in a both-ways suite), act as the player on `client:1` (`player`).
- **Wait for Coop's join, not the map.** A change sent while the server still sends the join data is lost; the run's
  setup waits on `game.joined` for you.
- **Coop's server refuses actions a real player could not do,** such as entering a town from too far away
  (`party.toTownGate` moves the party to the gate first).
- **Find a player's hero by name** across sides: its `StringId` is local (`main_hero` on every player's game).
- **Stop campaign time** before reading numbers (`game.pause`; the run's setup does it in Coop).
- **Wait on conditions, never fixed times:** `ctx.Until(...)`, `ctx.Settled(...)` (a value that stopped changing).
  `ctx.Delay` passes on a fast PC only.
- **Make failure messages say what was seen and what was expected:** "after reopening 2 time(s) the stash holds 10
  grain, expected 5".
- **Trace instead of guessing** when a number is wrong: log the messages or events involved on both sides, then
  read them.

## 5. Writing a step

```csharp
using ModderLords.Testing;

InGameTests.Register("stash.count", StepSide.Any, StashCount);   // id: area.verb, unique across ModderLords

private static IEnumerable<StepWait?> StashCount(StepContext ctx)
{
    yield return ctx.Until(() => Campaign.Current != null, "the campaign");          // wait on a condition
    var town = Settlement.Find(ctx.Arg("town"));                                     // arguments arrive as text
    ctx.Assert(town != null, $"there is no settlement {ctx.Arg("town")}");           // fail with what was seen
    yield return ctx.Settled(() => Count(town!, ctx.Arg("item")), "the stash count"); // stopped changing for 2 s
    ctx.Report("value", Count(town!, ctx.Arg("item")));                              // read by compares and $refs
    ctx.Dump("stash", Describe(town!));                                              // evidence file
}
```

- **Sides:** `StepSide.Server` changes the world, `Client` acts as the player, `Any` reads state (what compares use).
  In single player (side `game`) every step runs.
- `yield return null` waits one frame. Any exception fails the step with its type and message.
- `ctx.Report(key, value)`: later steps read it as `"$stepName.key"`; a compare reads `value` from every side.
- `ctx.Log(line)` goes into the result and is printed under a failed step; `ctx.Dump(name, text)` writes
  `evidence\...\name.txt`; `ctx.State` is shared by one side's steps in a run; `ctx.RunFolder` is the run folder.
- **Register** general steps in `BuiltInSteps.Register()` (or a sibling class it calls). Registering costs a
  dictionary entry; nothing runs outside a test run.
- **Coop types:** never reference Coop from these files. Reach Coop by reflection, as `CoopWatch` and `game.trace` do.

## 6. Writing a suite

```jsonc
{
  "api": 2,
  "name": "deposit-and-reopen",
  "description": "Items put in a town stash with Done stay there through three reopenings.",
  "mode": "coop",                                  // singleplayer | coop | both
  "requires": [ { "id": "CoopWarehouses", "version": ">=1.2" } ],
  "errorPatterns": [ "\\[Warehouses\\].*failed" ],   // extra on-screen errors game.noErrors fails on
  "steps": [
    { "id": "game.whoAmI", "side": "client:1", "as": "me" },
    { "id": "party.addItem", "side": "server", "args": { "heroName": "$me.heroName", "item": "grain", "count": 5 } },
    // The stash is opened from the town menu, so the party must be in the town; the server moves it to the gate first.
    { "id": "party.toTownGate", "side": "server", "args": { "heroName": "$me.heroName", "town": "town_EN1" } },
    { "id": "settlement.enter", "side": "client:1", "args": { "town": "town_EN1" } },
    { "id": "stash.deposit", "side": "client:1", "as": "deposit", "timeoutSeconds": 120, "args": { "town": "town_EN1", "item": "grain", "count": 5 } },
    { "compare": "stash.count", "sides": ["server", "client:1"], "args": { "town": "town_EN1", "item": "grain" } }
  ]
}
```

- A **both-ways** suite (`"mode": "both"`) names sides `game` (changes the world) and `player`; in single player both
  are the game, in Coop the server and `client:1`. A step can carry `"mode": "coop"` or `"singleplayer"`. Compares are
  left out in single player.
- The run's setup puts the world in place: in single player it makes or loads the world and reloads it fresh before
  each suite that needs one; in Coop it waits for the join and stops time. Do not repeat those steps.
- `"world": "shared"` runs the suite on core's world, after core and the other shared suites (no reset, and in Coop
  no launch of its own). Then it must not assume starting values: read "before" and check the change (`stash.deposit`
  adds to what the stash held; `hero.adminLevel` takes `by`), and put back settings later suites would see
  (`rules.set` reports `before`). Prefer it: a fresh world in Coop costs a whole launch, a few minutes. Keep a fresh
  world for a suite that saves and reloads, or makes a change other suites would trip on.
- `continueOnFail` keeps going after a failure; otherwise a failure skips the rest of *this* suite only.
- `knownFailing: "<issue link>"` marks a step expected to fail until an upstream fix: it shows `[known]`, and a pass
  becomes `UnexpectedPass`, which fails the run so the mark gets removed.
- The full format, sides and file layout: `docs/INGAME-TEST-HARNESS.md` ("Writing a suite", "Files in the run folder").

## 7. Making a module

1. **Folder** `src/ModderLords.Core/Testing/Modules/<id>/` with a `module.json`:

   ```json
   {
     "id": "combat",
     "title": "Combat",
     "description": "A battle started by code ends, and its casualties and loot read the same on the server and the player's game.",
     "requires": [ ]
   }
   ```

   `description` is what the Tests tab and `test --list` show: say what the module checks. `requires` lists the mods
   every suite in it needs (with optional version ranges); without them in the profile the module is unavailable, not
   failed.
2. **Suites** beside it, one file per suite (`<name>.json`). They are embedded in the launcher by the folder, with no
   project change.
3. **Steps:** general steps go in `src/ModderLords.CompatSync/Testing/` (a new `<Area>Steps.cs`, registered from
   `BuiltInSteps.Register()`).
4. **Only when steps must call a particular mod's code** (MS2's catalog, MA2's armour builder), give the module its
   own DLL instead, so nothing breaks when that mod is not installed:
   - a project `src/ModderLords.Tests.<Module>/` targeting what `ModderLords.CompatSync` targets (net472), referencing
     `ModderLords.CompatSync` and the mod's own DLL **at compile time only** (`Private="false"`), and built into the
     launcher's `compat\` folder;
   - a public static class `ModderLords.Tests.<Module>.Steps` with `public static void Register()` that calls
     `InGameTests.Register(...)`;
   - `"steps": "ModderLords.Tests.<Module>.dll"` in `module.json`.

   The agent loads it only when the run picked the module and the mod is loaded on that side; a side without the mod
   skips it with a note, and a DLL that will not load, or registers nothing, stops the run before any step. The first
   such module (`retinues`) sets up the build wiring; copy it: a `<Mod>RefDir` / `Has<Mod>Assemblies` pair in
   `Directory.Build.props` (the project builds only where the mod is installed, so CI and other PCs skip it), a
   conditioned `ProjectReference` and a `Copy` into `compat\` in both `ModderLords.Cli.csproj` and
   `ModderLords.App.csproj`. Reach anything only some versions of the mod have (another fork's API, its screen
   types that need UIExtenderEx to compile) by reflection, so the DLL builds against any installed copy.
5. **A health step** for a mod's module: register `<ModId>.health` (side `Any`), reporting what the mod loaded as its
   `value` (a catalog's size and hash, say). The core suite's `mods.health` runs every one on every side and compares
   them, in every run that loads the module.
6. **Unit tests:** `The_built_in_suites_are_grouped_into_test_modules_with_core_first` lists the module ids: add yours.
   Test any pure logic (parsing, formatting, a rule) in `tests/ModderLords.Core.Tests/` by linking TaleWorlds-free
   files, as `ErrorPatterns.cs` is.

## 8. Before opening a PR

- `dotnet test` green.
- The module run in the game in every mode it supports (single player and Coop), with the run ids in the PR
  description, and a failure seen at least once where that is cheap (a `knownFailing` self-test, as for
  `game.noErrors`): a check that has never failed may not be checking.
- `docs/INGAME-TEST-HARNESS.md` updated where the module adds steps or changes a rule; this guide where it adds a
  way of reaching the game.
- Follow `AGENTS.md`: a branch from `origin/main`, one PR per change, merge only on the maintainer's word.
