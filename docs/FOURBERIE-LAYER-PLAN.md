# Fourberie co-op layer: investigation and plan

Fourberie (module id `Fourberie`, Workshop 2875710877, Nexus 2969) under Bannerlord Coop. Reviewed at **v1.4.8.2**
(`Fourberie.dll` SHA-256 `6c73723c6129933a170e63e6d7aaabe4e37bb4ca847206b1878041197e7cce89`, built 2026-09-29),
against ModderLords `ba821db` (1.2.1), Coop source `97420dc` (2026-10-01) and the installed Coop **0.1.5** Workshop
build. Status: **phases 0–4 done; the server side passes a live self-test on the real dedicated server (no client yet); phases 5–7 to do.**

The per-line analysis (entry-point matrices with line numbers, `_crimeValue` key legend, mutation catalogue) is kept
outside the repository, because it is derived from a decompile: `D:\Work\Claude\Tech Support\_fourberie-analysis\`
(A1 core behaviour, A2 sub-features, A3 bandits/safe house/pit fights, A4 models/missions/UI, A5 Coop internals).

## What Fourberie is

A single-player "criminal empire" mod. The player builds a crime base (a town or a hideout safe house) with a "lads"
party, then runs scams, protection, extortion, robbery, larceny, insurance scams, sabotage, incited riots, caravan
ambushes, notable extortion and greedy-militia jobs. There is also:

- a Scheme Room for bribing, blackmailing, poisoning, kidnapping or murdering two tracked victim heroes;
- gang rivalry, domination and partnership, and alley and workshop territory;
- bandit diplomacy (support bandit cultures, escort parties, "War Dog" contracts);
- pit fights;
- prison breaks and stealth missions in lords' halls and prisons;
- AI kingdom leaders offering contracts;
- clans scheming back against the player.

Shape of the code:

| | |
|---|---|
| Behaviours | 9 campaign behaviours (`AddOnHS` only with Homesteads Reloaded). Only `FourberieBehavior` saves anything. |
| Saved state | 36 **`public static`** fields on `FourberieBehavior`, written by its one `SyncData`: dictionaries keyed by settlement/clan/role strings, `Dictionary<int,int> _crimeValue` used as ~60 flags/counters, live `Hero`/`MobileParty`/`Settlement` references, one `List<TroopRosterElement>`. The other behaviours' statics are transient scratch. |
| Models | 14 game-model wrappers (clan finance, crime, loyalty, security, food, access, diplomacy, military power, speed, prices, death, damage, donation, party transition). |
| Harmony | **None.** |
| UI | Three Gauntlet screens (`CriminalVM`, ~90 bound commands), opened by a hotkey polled in `Main.OnApplicationTick` only while `MapScreen` is on top. Map notifications (`MapNotifGrudgeData`, saveable). |
| Missions | Mission logics added in `OnMissionBehaviorInitialize`, all gated on `Mission.Current`/`Agent.Main`. Safe house and pit fight inject a `Location` into the settlement's private `_locations` by reflection, then call `SandBoxMissions.OpenVillageMission`. |
| Settings | `FourberieConfig.xml` via `XmlSerializer` into `Settings.Instance`: 14 key bindings + 4 gameplay switches (`MinorFactionRecruitment`, `HealButtonInTown`, `HealButtonInHideout`, `GoodFortuneButton`). |
| Manifest | One submodule tagged `DedicatedServerType=none`; depends on `StoryMode`. |

**The defining fact: Fourberie is all about "the player".** Almost every tick, event handler and command ends up acting
on `Hero.MainHero` / `Clan.PlayerClan` / `MobileParty.MainParty`, or on the single `_crimeBase`. The core file has no
genuinely player-independent simulation. Even the "clans scheme against you" loop iterates every clan only so that it
can hit the one player. The only world-wide logic is in `FourbBanditBehavior.FOnDailyTickParty` /
`FOnDailyTickSettlement`, which walk every bandit party and hideout, and `FourbContractBehavior`'s offers from AI rulers.

## How it behaves under Coop today

ModderLords' **Run** role already strips the `DedicatedServerType=none` tag, so the server loads `Fourberie.dll`
(seen in the 2026-09-25 launch logs). The server ships the Gauntlet/SandBox.View assemblies in Coop's server bin, so
type loading is fine. Every UI and mission path is gated behind a map screen or `Mission.Current`, so the DLL is
headless-safe as long as nothing calls into those paths. Release 1.0.7 already added the missing
`Dictionary<int, CampaignTime>` save container that Fourberie needs (`SaveableTypeCompatDefiner`).

What actually happens, using Coop facts verified in source (A5, file and line references there):

1. **Clients never tick.** Coop skips `CampaignPeriodicEventManager.TickPeriodicEvents` on clients, so no
   Hourly/Daily/Weekly/DailyTickSettlement/DailyTickHero handler of *any* mod runs there. `MapEventEnded` is also
   server-only. Every Fourberie income, cooldown, scheme timer, base upkeep, healing, revenge and bandit tick therefore
   **runs only on the server**.
2. **On the server, "the player" is an idle placeholder.** Coop substitutes the main hero only while it processes a
   specific player's request. So on the server Fourberie runs one criminal empire for nobody: the placeholder has no
   base, and its revenge loop targets the placeholder's companions.
3. **Each client holds its own copy of the statics.** Each client gets them from the server's save at join (the
   placeholder's empty book). It then mutates them locally from menus, dialogs, the Gauntlet screen and missions, and
   loses them at the next join.
4. **World changes from client code are mostly lost:**
   - **Blocked on the client:** `GiveGoldAction`, `ChangeRelationAction`, `KillCharacterAction`,
     `TakePrisonerAction`, `DestroyPartyAction` and `Alley.SetOwner` do nothing there.
   - **Silent drift:** direct writes to `Town.Security/Loyalty/Prosperity/FoodStocks`, militia and `Hero.Gold` change
     the local copy only, are never sent, and drift apart between machines.
   - **No sync at all:** `ChangeCrimeRatingAction` has no Coop patch (upstream #3234, open).
5. **Client-created heroes, clans and parties are phantoms.** Coop intercepts every registered type's constructor,
   so `MobileParty.CreateParty` or `BanditPartyComponent.CreateLooterParty` on a client builds a party that only that
   client ever sees. Coop also syncs only its eight vanilla party-component types.
6. **Missions are mixed.** Vanilla settlement scenes opened through `SandBoxMissions.Open*Mission` go through Coop's
   location sync. Fourberie's injected safe-house and pit locations also go through `OpenVillageMission`; how Coop
   handles an unregistered location there is **unverified**. Conversations with unregistered NPCs start unmediated.
7. **Time stops.** Fourberie sets `TimeControlMode = Stop`; Coop swallows that and turns it into a time-speed
   request.

Net result: menus and screens open, but nothing a player does persists, nothing pays out, and the server runs a
phantom empire. That matches the community's "it loads but doesn't really work".

## Design

**The server runs each player's Fourberie, in that player's own copy of Fourberie's statics. Clients are views and
interaction front-ends.** This is the TAOM/Living Economy layer pattern (`src/ModderLords.CompatSync.Coop/<Mod>/`,
components that check the members they need and switch themselves off with a log line). It is not the Operations
framework, which is too strict and has never run live for this kind of mod (see `TAOM-LAYER-PLAN.md`).

The new idea, and the reusable part, is the **player book**. All of Fourberie's state is static, so the server can keep
one set of field values per player and swap them in around any Fourberie call. A swap only reassigns about 50
references, so it is cheap. Values are read back on exit, so both in-place edits and reassignments land in the right
book. Together with `ServerRelay.PlayerScope` (which already swaps `MainParty`, `PlayerClan`, `PlayerTroop` and Coop's
resolved main hero), this lets Fourberie's own unmodified code run correctly "as" each player. It also removes the
scratch-field races, because the server game thread is serial and each client is its own process.

It is built generically (`PlayerBooks`: a list of static fields plus a codec) so the next single-player mod that keeps
"the player's" state in statics can reuse it.

### Components

| Id | Side | What it does |
|---|---|---|
| `fourb-book` | server | `PlayerBooks` store keyed by player hero id. It covers every static field on the Fourberie types except UI (`GauntletLayer`, movies, VMs) and constant `Location` definitions. `FourbScope(hero)` = `PlayerScope(hero)` + swap-in/read-back; outside a scope the placeholder's (empty) book is active. Books are **persisted in the server save** by a new ModderLords campaign behaviour (`SyncData` of `Dictionary<string,string>`, hero id to book JSON). The **explicit codec** stores `Hero`/`MobileParty`/`Settlement`/`Village` by `StringId`, `CampaignTime` as ticks, and `TroopRosterElement` as (character id, count, wounded, xp). It is bounded, and malformed books are rejected whole. |
| `fourb-ticks` | server | Prefix on every per-player handler: `FourberieBehavior` Hourly/Daily/Weekly/DailyTickSet/DailyTickHero; bandit Hourly/Daily/Weekly; safe house Hourly/Daily; pit Weekly/DailyTickHero; contract HourlyTick/DailyTickClan. Instead of one placeholder run, the original runs **once per player book** inside `FourbScope`. World-wide handlers (`FOnDailyTickParty`, `FOnDailyTickSettlement`) are reviewed one by one: run once, or once per player where they read the player's bandit standing. World events (`HeroKilled`, `ClanDestroyed`, `KingdomDestroyed`, `HideoutDeactivated`, `CompanionRemoved`, `HeroPrisonerTaken`, `MobilePartyDestroyed`, `MapEventEnded`) are fanned out to every book. Where a single player is responsible (the destroyer's or prisoner's owner), only that player's book receives it. This is where insurance-scam and bounty resolution start working, because `MapEventEnded` exists only on the server. |
| `fourb-mirror` | both | Each player's book is kept in step **in both directions**, row by row (`FbBookSync`, `LeMirrorDelta` deltas + gzip, sequence-numbered with a full resync on a gap). The server sends the rows its ticks changed, and the player's game reports the rows its own menus, screens and missions changed. Each side's rows overwrite only those rows, so a player's local progress is kept on the server before any per-command relay exists. On the player's game the statics *are* that player's book. Sent per player through `TaomActions.Push`; reports go back on the shared action channel. |
| `fourb-commands` | both | Each Fourberie command is put in one of three tiers, listed in a reviewed table (below). |
| `fourb-prompts` | server | Inside `FourbScope`, Fourberie's `InformationManager.ShowInquiry` / `MBInformationManager.ShowMultiSelectionInquiry` are sent to **that player** as a remote prompt, with the callbacks held server-side under a request id. The answer runs the chosen callback in scope; on timeout or when the player is offline, the negative/default option runs. Map notices (`MapNotifGrudgeData`) and `DisplayMessage` lines are forwarded the same way (`LeNotices`/`TaomNotices`). This **must** replace ModderLords' bundled headless guard, which answers every inquiry affirmatively: inside Fourberie that would auto-accept blackmail demands and war declarations on the player's behalf. Built generically so TAOM and Living Economy can use it too. |
| `fourb-models` | server | Owner-scoped model calls. `FModelClanFinance` (its expense helper also mutates `_crimeValue`!) runs in scope when the clan belongs to a player. `FModelPower`/`FModelMobileFood`/`FModelMapSpeed`/`FModelPartyTransition` run in scope when the party is a player's main party. `FModelDeath` runs in scope for a player hero, and `FModelDiplo` gets its faction-leader checks the same way. Clients compute models from their own mirrored book (correct for themselves). Other players' numbers on screen are an accepted display-only gap, as in TAOM. |
| `fourb-phantom-guard` | client | During a co-op session, any hero, clan or party construction under a Fourberie frame is refused with a log line naming the call site, so a missed relay shows up as a clear log line rather than an invisible bug. |
| `fourb-locations` | client | Resolves how Coop's `PlayerLocationEntryPatches` treats the injected safe-house and pit locations. If it misbehaves, those two locations are marked local-only (the scenes are single-player scenes anyway). |
| `fourb-settings` | both | Host's four gameplay switches win; **key bindings stay per player** (the generic static-settings sync would push the host's keybinds onto everyone). This is done with a compat record (`IgnoreSettingsTypes: Fourberie.Settings`) plus a small explicit sync of the four bools. Client: no reload of `FourberieConfig.xml` over the host's values during co-op. |

### Command tiers

Every player-reachable entry point gets a row in a reviewed table in code (`FourbCommands.cs`): about 90 `CriminalVM`
commands, the menu consequences, the dialog consequences and the mission aftermaths.

- **T1, server-run.** These are state or world changes with no UI inside: Gauntlet buttons (upgrades, role
  assignment, scheme start, troop moves), most menu consequences, and **every method that creates a party**
  (`CreateVirtualParty`, insurance-scam caravan + bandits, extortion militia, caravan-ambush party, bandit escorts).
  - **Client:** a prefix sends (op, args by id) on the shared action channel under feature `fourberie` and skips the
    local body.
  - **Server:** re-resolves the args, checks the sender is where the action needs them (party at that settlement),
    runs the same Fourberie method in `FourbScope`, and answers with the mod's lines.
  - **Result:** the client receives the new book; any new parties arrive through Coop's normal replication.
- **T2, client-played, server-applied.** These are flows whose outcome comes from something only the client has: a
  mission fight, stealth, pickpocketing, a pit fight, or a dialog choice with a dice roll.
  - **Client:** the flow runs locally, so the scene, the roll and the UI all agree. A recorder captures what the flow
    did: book keys it changed, plus the world calls Coop blocks or lets drift (gold, relation, crime, renown,
    influence, skill XP, kill/imprison, settlement stat writes). It sends them as one bounded transaction.
  - **Server:** applies the transaction in `FourbScope` with plausibility bounds (gold limits, same settlement, victim
    still alive).
  - **Trust:** a trusted-friends model, the same one TAOM's special resources and careers use. It is not cheat-proof
    and is documented as such.
- **T3, not in co-op.** These are features that cannot be made coherent; each shows Fourberie-styled "not available
  in co-op" text instead of running. Candidates are decided only after the spike in phase 2: party-screen enslaving
  (`OnDoneEnslaved` is the only feed of the slave/mine economy), the leave-kingdom consequence chains, and contract
  offers that pop a conversation from a tick.

## Phase 0 results: installed Coop 0.1.5 (2026-09-30)

Every fact above holds in 0.1.5; detail with file and line references in `_fourberie-analysis\P0-coop015-verification.md`.

- **Party ids:** server-created parties get the **same StringId** on clients, so books can refer to parties by id.
- **Fourberie's own locations** (safe house, pit fight): not registered with Coop. Coop logs a warning, skips the
  location sync and plays the mission on that player's game only. Nothing throws.
- **`PlayerEncounter.StartBattleInternal`** asks the server and silently refuses a battle against a party the server
  does not know. Fourberie's ambushes need **server-created parties** (phase 4).
- **Crime:** only `Kingdom.MainHeroCrimeRating` is synced, and it is one value per kingdom shared by every player.
  Per-player crime still needs carrying (phase 6).
- **Unguarded on clients:** `ChangeKingdomAction` and `GainRenownAction`/`Clan.Renown` are not blocked; they change the
  local game silently. Item and troop rosters made on a client are local only.
- **`TimeControlMode`:** sets outside Coop's own controls are refused everywhere, so Fourberie's time stops do nothing.

## Phases (one PR each, merged as they go green)

0. **Ground truth against the installed Coop 0.1.5.** Source `HEAD` is 1,047 commits past 0.1.5. Decompile the
   installed `Coop.Core.dll`/`GameInterface.dll`/`Missions.dll` and re-check the A5 facts the design leans on: client
   tick suppression, the Action blocks, constructor phantoms, location entry with a custom `Location`, conversation
   locks, `TimeControlMode`, and item roster deltas. Record the results in this doc. *No code.*
1. **Foundation**:
   - `FourberieLayer` with inert-unless-active, the component log lines and a version check.
   - `FourberieSurfaceTests`: Cecil checks every bound member against the installed DLL (`MODDERLORDS_FOURBERIE_DLL`).
   - Compat record: Run, Settings sync required, the settings ignore rule.
   - `FourberieLaunchPolicy`: refuse without Settings sync; warn if the experimental server-only logic is ticked for
     it; warn on a save made without Fourberie.
   - `fourb-settings`.
2. **Player books + ticks + two-way book sync** (`fourb-book`, `fourb-ticks`, `fourb-mirror`, the saved store, the codec,
   the launch policy). Built:
   - `StaticBook` swaps a player's book in and out around a call.
   - `FbBookCodec` writes a book as JSON by object id.
   - `FbBookSync` merges rows in both directions.
   - `FbBooks` stores books in FourberieBehavior's own save data under `modderlords_fourberie_books`.
   - `FbTicks` fans out 45 handlers: periodic ticks to connected players, world events to every book. This includes an
     event fired inside another player's run, while skipping books suspended further up the stack.
   - `FbMirrorClient` is the client side.

   Offline: under real Harmony against a stand-in mod, Cecil checks against the installed DLL (the saved-field set and
   every registered handler are accounted for), and negative runs proving the tests catch the bugs they guard against.
   **Not run live.**
3. **Prompts and notices** (`fourb-prompts`), because ticks in phase 2 start raising inquiries. Built (`FbPrompts`,
   `FbPromptWire`). A Cecil call-graph over the 45 server-run handlers found what they can raise:
   - **Yes/no inquiries:** 9 call sites.
   - **List inquiries:** 2, both inside blackmail/murder consequences.
   - **Map notices:** 2, informant reports.
   - **Map conversation:** 1, the contract offer, which needs the text variables its dialog lines read.
   - **Messages:** plain messages and quick information.

   Inside a player's run on the server, each of these goes to that player's game, after the book rows that run
   changed. Callbacks wait on the server and run as the player when the answer comes back. Offline, disconnected or
   10 minutes without an answer means the default: "no" where there is one, else the only option; for a must-pick
   list, the fewest allowed. Prompts never reach the headless guard's auto-yes (patched at a higher priority; Harmony
   2.4.2 checked to skip later prefixes). Messages use the TAOM layer's notice forwarder, now shared:
   `NoticeComponent.EnsureInstalled`, and Living Economy's copy stands aside whenever it is on. Prompts still waiting
   when the server stops are lost; they are not saved.
4. **Parties** (`FbLedgers`, `FbRelay`, `FbGaps`). With two-way book sync, a player's own menu and screen actions
   already keep Fourberie's state. What a player's game cannot do is make a party others can see. Fourberie's 11
   party-creation sites (a Cecil test fails if a new one appears unhandled) now go four ways:
   - **Server ticks** (bandit spawns, the revenge loop): already real, since they run on the server.
   - **Ledgers** (`CreateVirtualParty`: "Your lads", saboteurs): the server makes each player's once, as that player.
     `CreateVirtualParty` hands back the existing one, emptied, on either side. Its members, prisoners and warehouse
     items travel in the book (`ml_ledgers`, row per ledger, set semantics), so Fourberie's party-screen and stash
     callbacks reach the server.
   - **Relayed (T1):** the insurance scam's `SpawnCaravan` and `SpawnBandits`. The player's game skips them and sends
     the arguments by id; the server runs them as the player.
   - **Not in co-op yet (T3, with an on-screen line):** fights against a party made on the spot (the safe-house raids,
     extortion's village militia, the grand caravan heist), sabotage's "send raiders", and spinning a bandit party off
     the gang. These need battle work (Coop refuses a battle with a party it does not know) or a phantom-to-real
     migration.

   A watch logs any party Fourberie code still makes on a player's game, with where it came from.
5. **Models** (`fourb-models`).
6. **T2 transactions**: mission and dialog flows, crime-rating mirroring, `fourb-locations`.
7. **T3 notices + docs**: `docs/FOURBERIE-LAYER.md` as the user-facing layer doc, the README mod list and a live test
   script.

## Verification with as little game time as possible

- **Offline, every PR:**
  - codec round-trips (Unicode, malformed, oversized, stale sequence);
  - book swap and read-back against a **stand-in mini-mod** with Fourberie's exact static shapes, run under real
    Harmony. This is the approach Living Economy used, and it found a real bug.
  - delta/resync;
  - Cecil surface parity with the installed `Fourberie.dll`;
  - full repo test suite;
  - for each new component, a test that fails with the component off.
- **Server-only live self-test, built and passing (phase 4).** `FbSelfTest`, off unless `MODDERLORDS_FOURBERIE_SELFTEST`
  is set, on a world generated with Fourberie in an isolated data folder:

  ```
  ModderLords.Cli launch --mods Fourberie:Run --settings-sync --create-world fbtest --data-dir <dir>
  MODDERLORDS_FOURBERIE_SELFTEST=run MODDERLORDS_FOURBERIE_SELFTEST_SAVE=fbcheck MODDERLORDS_FOURBERIE_SELFTEST_EXIT=1     ModderLords.Cli launch --mods Fourberie:Run --settings-sync --compat --save fbtest --data-dir <dir>
  MODDERLORDS_FOURBERIE_SELFTEST=check MODDERLORDS_FOURBERIE_SELFTEST_EXIT=1     ModderLords.Cli launch --mods Fourberie:Run --settings-sync --compat --save fbcheck --data-dir <dir>
  ```

  - **"run"** treats two AI lords from different clans as players. It makes their ledgers, gives one a crime base and
    troops, and runs every per-player tick handler through the real prefix. It then relays the insurance scam and
    raises a yes/no and a must-pick prompt for the (offline) player. Last, it saves.
  - **"check"** loads that save and verifies books, base, territory, ledger troops and the caravan, then ticks again.
  - **Result:** run 27/27 and check 7/7 on 2026-09-30.
  - **Bugs it found:**
    - The engine's save loader hands back `MBList<T>` for `List<T>` fields, which the codec skipped, so territory and
      partnership lists were silently left out of the book. The codec now accepts collection subclasses, with a unit
      test.
    - Coop shows every active party on the server, so ledgers are hidden on players' games instead.
- **Server-only, run by Claude, no game client (still to add).** The scriptable dedicated-server host (stdin/stdout, as used for
  Bellum) plus new console commands:
  - `modderlords.fourb.adopt <hero>`: treat a hero as a player for testing;
  - `modderlords.fourb.run <op> <args>`: drive a T1 command through the same server handler the network uses;
  - `modderlords.fourb.dump <hero>`;
  - Coop's `coop.debug.*` and time controls to pass days.

  What this proves without a client: per-player ticks pay out into the right book, revenge schemes target the right
  player, party creation is real and registered, prompts route (logged as "sent to <hero>"), and save → restart →
  reload keeps every book byte-identical. Before every claim, the same script is run with the layer off to show the
  failure it fixes.
- **Client side, offline.** The client prefixes, mirror apply and T2 recorder are run against the stand-in mod and a
  fake action channel.
- **Your time: two batched sessions, each with a step-by-step script and the exact log lines to read back.**
  1. After phase 4: join, open the Fourberie screen, set up a base, pass days and watch income arrive, reconnect and
     see it kept, then have a second player confirm they cannot see or affect the first player's book.
  2. After phase 6: a scam, a robbery, a pit fight, a safe-house visit and a scheme, checking the payouts and that the
     crime rating shows.

  Anything that fails gets fixed from the logs and re-checked server-side before the next session.

## Decisions (maintainer, 2026-09-30)

1. **T2 outcomes are trusted.** Client-played, server-applied, as with TAOM special resources.
2. **Offline players freeze.** `fourb-ticks` runs periodic ticks and movement events only for connected players.
   World events (deaths, wars, clans and kingdoms ending) still reach every book, so a dead victim leaves an offline
   player's schemes. Where Fourberie pays out on such an event (a contract target dying), the offline player is paid
   as single player would.
3. **T3 is acceptable** for flows that phase 2 shows cannot be made coherent.
4. **Fourberie's author is not contacted.** Nothing of Fourberie's is copied or shipped; everything binds by reflection.

## Risks

- **Fourberie updates often** (the DLL is from 2026-09-29). Components switch off on a moved member; the surface
  tests name what moved. Books record the Fourberie version and refuse a book from a different major layout.
- **Coop 0.1.5 vs source.** Everything is re-checked in phase 0 against the installed DLLs, and the code binds to Coop
  only through seams ModderLords already uses live (`PlayerScope`, the action channel, `PlayerHeroes`).
- **Object ids across machines.** The mirror resolves heroes, parties and settlements by `StringId` on the client. That
  is proven for vanilla objects; parties the server creates mid-session need confirming in the first session (the
  `TaomPartyComponents` precedent shows Coop drops non-vanilla party components).
- **Settlement direct writes** made on the server in scope reach clients only if Coop auto-syncs those properties
  server→client. Phase 0 checks `Security/Loyalty/Prosperity/FoodStocks/Militia`.
- **Size.** About 7,900 lines of core behaviour plus 27,000 more. TAOM's layer took about 25 PRs. Expect 8–10 here,
  most of it tables plus the generic book, prompt and transaction machinery.
