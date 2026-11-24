# Bellum Civile 1.5.1 in Coop: what runs where, and what the compat module does about it

Survey of Bellum Civile 1.5.1 (game v1.5.7) against the Bannerlord Coop nightly for 1.5.7, for the standalone compat
module `coop-compat/BellumCivileCoopCompat` and the `bellum` in-game test module. Written 2026-11-23 from a read of
the decompiles; nothing here has been run in a game yet unless a suite is named. Points still to check in game are in
"Open questions".

**Scope (Andy, 2026-11-23): the cheap fixes first, then player agency** (see "Player agency" below), so players can
use Bellum's systems in co-op rather than only live in a world Bellum runs. The "Decision" column below is the first
stage. Where it says X4 ("players are not AI"), the guard stays only for things done *to* a player without asking;
anything Bellum would ask the player becomes a prompt to that player in the agency stage.

## Where it stands

| Stage | What | Module | Suites |
|---|---|---|---|
| 0 | Peace parley off, voted peace, treaty vote, internal peace, no kingdoms on players' PCs, thread safety | 1.1.0 | `coop`, `treaty-vote`, `alliance-block`, `feud` |
| 1 | X1, X2, X3, X5, X6, X7; client kingdoms follow Coop's replays; Diplomacy's direct peace; title-name cache locked (`ServerDecides`, `PlayerActions`, `ServerScreens`); treaty draft gate (`TreatyDraftGate`) | 1.2.0 | `server-decides`, `treaty-lose` |
| 2 | X4 (Bellum's AI leaves players' clans alone on the server, `PlayersNotAi`); crown accession on a ruler's death | 1.3.0 | `players-not-ai`, `crown` |
| 3a | Player agency: the channel (`Agency`, `BellumCivile.CoopCompat.Net.dll`), acting as a player (`AsPlayer`, Coop's own scope), prompts (`Prompts`) with the first site, an ultimatum to a player's realm | 1.4.0 | `agency-channel`, `agency-ultimatum`, `agency-ultimatum-yes`, `agency-timeout` |
| 3b | Bellum's state on players' screens (`StateMirror`: factions, claim feuds, titles), and the Factions tab's buttons carried out by the server (`FactionActions`, A2) | 1.4.0 | `agency-state`, `agency-factions` |
| 3c | The rest of A1: a call to a civil-war side, treason solidarity, a succession opportunity (`Calls`); claim feud rulings, a player ruler's judgment, a feud's call for supporters, a demand to surrender a title (`Feuds`); list questions (pick one of several) in `Prompts` | 1.5.0 | `agency-calls`, `agency-calls-no`, `agency-calls-timeout`, `agency-treason`, `agency-feud-judge`, `agency-feud-judge-timeout`, `agency-feud-ruling` |
| 3d | A3 to A6 | later | `agency-*` |

Stage 3c notes (defaults, reversible): an unanswered civil-war call, treason appeal, feud support call or title
demand does nothing (the player stays where they are, pledges nothing, keeps the title for now); an unanswered feud
ruling is accepted; a feud a player ruler doesn't judge in time is judged by Bellum's AI and isn't put to them again; a
succession opportunity for an absent player is "not yet". A player in a faction whose ultimatum is refused is taken out
of it first and asked, as Bellum does for the player. Not staged in a suite: the succession opportunity (it needs a
ruler's death with a strong player-led faction waiting) and the feud support call and title demand; their patches are
checked at install (the module logs "not found" otherwise, which `bellum.compat` fails on).

Stage 1 notes: Bellum's dialogue lines and buttons are off on players' PCs, not routed, until stage 3. Bellum's own
inquiries are not suppressed generically (its diplomacy action menu wraps vanilla's war and peace proposals), but the
ticks that raise them are off on players' PCs. Bellum's seven patches that took a player's vanilla policy, expulsion,
fief and diplomacy proposals into its own deliberations, and the one that hid vanilla's marriage line, answer "vanilla"
on players' PCs, so Coop sends those proposals to the server as in any co-op game.

Found while testing stage 1: **a treaty for a player's realm rarely reached their vote.** Before Bellum puts an
automatic treaty forward (`ForeignTreatyBehavior.CanSubmitAutomaticDraft`) it asks whether both councils would accept,
except the player's own realm, whose player decides. On a dedicated server a player's realm was judged as a lord's: its
"council" was the player's own hero, read by Bellum's AI ("reject by sole ruler assessment" in Bellum's log), which
refuses to lose a war or pay a tribute. `TreatyDraftGate` treats a realm a connected player rules as the player's in
that check, so the draft goes forward and TreatyVote puts it to the player. `treaty-lose` fails without it (Bellum's
log: "Automatic treaty deferred before submission ... loser=reject by sole ruler assessment").

## How Coop treats a mod like Bellum

- **Every PC runs all of Bellum.** The server and each player's PC run every campaign behaviour, so each PC computes
  its own rebellions, treaties, councils and titles.
- **Bellum's own state is never synced live.** Coop syncs vanilla objects through its GameInterface services. A
  behaviour's `SyncData` reaches a player once, inside the server's save when they join, and then drifts. Bellum
  1.5.1 has 105 behaviours and about 400 `SyncData` keys.
- **A player's writes go one of three ways** (evidence: Coop nightly GameInterface, analysis only):
  1. **Refused on the PC, replayed from the server:** `DeclareWarAction`, `MakePeaceAction`, `ChangeRelationAction`,
     `GiveGoldAction`, `MarriageAction`, `KillCharacterAction`, `TakePrisonerAction`, `ChangeOwnerOfSettlementAction`,
     `DestroyClanAction`, `ChangeKingdomAction` (`Clan.SetKingdomInternal`), policies, `RemoveDecision`.
  2. **Applied on the PC, overwritten by the server's next write (AutoSync):** `Kingdom._rulingClan`, `Clan._leader`,
     `Clan._influence`, `Hero.Gold`, `Hero.Clan`, kingdom name and banner, mercenary flag, tributes, direct
     `SetHeroRelation`. Bellum code on a PC that writes these makes that player see a different king or leader for a
     while.
  3. **Forwarded to the server with little or no check:** `Kingdom.AddDecision` of a type Coop can send,
     `EndCaptivityAction` for any hero who isn't a player, `StartAlliance`, `Hero.SetName`, `Clan.ChangeClanName`.
     These are the dangerous ones: a PC's own Bellum can change the server's world.
- **Not blocked anywhere:** creating a kingdom, clan or hero on a PC. It exists only on that PC, with no Coop id.
- **Mod kingdom decisions:** Coop nightly has `KingdomDecisionTypeRegistry.Register<T>(write, read)`. A type that
  isn't registered is decided by the server's AI with no player vote. On a PC it is refused with "can't be proposed in
  Coop". Server and players must register the same types or the join is refused.
- **The player on the server.** On a dedicated server `Hero.MainHero` / `Clan.PlayerClan` is a placeholder nobody
  plays. Bellum asks `== Clan.PlayerClan` to tell "the player" from the AI, so **on the server every real player's
  clan and realm is run by Bellum's AI**, and every popup Bellum puts to "the player" goes nowhere. Most of those
  popups also need a `MapState`, which a dedicated server never has. The module's `Coop.IsPlayerHero` /
  `IsConnectedPlayer` (from Coop's `HeroExtensions.IsPlayerHero`) is the test to use instead.

So the general rule for the module: **Bellum decides on the server; players' PCs only show.** On a player's PC,
Bellum's state-changing ticks are off, and so are the player actions that would change only that PC. On the server,
Bellum keeps its AI away from realms and clans that real players lead.

## Already covered (module v1.1.0, suite `bellum/coop`)

| Area | What the module does |
|---|---|
| Council caches | Locked, so Coop's parallel party tick can read them. |
| Kingdom founding | Players' PCs found no kingdoms. `KingdomCreationSafetyHelper.CreateKingdom` is the only caller of `Kingdom.CreateKingdom` in 1.5.1 (9 callers), so the guard covers every path; a sweep removes any kingdom without an id. |
| Peace parley | Off on players' PCs; peace is the vanilla kingdom vote. |
| Voted peace | A vanilla peace vote in a player's realm passes Bellum's guard, except when an ally's call to arms forbids a separate peace (`AlliancePeaceRestriction`). |
| Treaty vote | A Bellum treaty for a realm a connected player rules becomes a vanilla peace vote there. |
| Internal peace | The server runs `InternalPeaceSettlementBehavior.OnTick` despite having no map screen; players' PCs skip it. |

## Per-system findings and proposed decisions

Columns: **Where it acts** (S = the server's copy matters, P = players' PCs act too); **Problem in Coop**; **Decision**
(proposed); **Cost** (L/M/H).

### Cross-cutting (fix these first; they cover most systems)

| # | Problem | Decision | Cost |
|---|---|---|---|
| X1 | Bellum's AI on **every PC** files vanilla-type decisions (policies, expulsions, fief claimants, alliances, trade agreements, king selection) through `IdeologyBehavior.AddDecisionAsModAction`, influence cost waived. Coop forwards each PC's copies to the server, which adds them again. So duplicate votes, and a fief can be voted away twice. Bellum's own decision types are refused on PCs with a "can't be proposed in Coop" message, possibly daily. | Players' PCs: refuse `AddDecisionAsModAction`, except a decision the PC's own clan proposes. | L |
| X2 | Bellum's daily/hourly/weekly ticks on PCs keep diverged copies of factions, feuds, titles, councils, pacts and hostages. Some of their writes leak to the server (X1, X3) or overwrite AutoSync fields locally. | Players' PCs: skip the state-changing ticks of the behaviours listed below (one prefix group, the same `Coop.OnPlayerPc` test as InternalPeace). The PC keeps the state it got at join for its UI. | L |
| X3 | Leaks from PCs to the server: `EndCaptivityAction` (treaty prisoner release, hostage release, household ransom) and `StartAlliance` (`ClientKingdomBehavior.EnsureProtectedAgreements`). A PC can free real prisoners for a treaty the server never signed. `HouseholdRansomBehavior` skips the gold on the PC but the release still reaches the server, so the ransom is free. | Players' PCs: skip `RetainedTreatyPrisonerBehavior.ReleaseByTreaty`, `HostagePactBehavior.Release`, `HouseholdRansomBehavior` selection, `ClientKingdomBehavior.EnsureProtectedAgreements`. | L |
| X4 | Server: Bellum's `== Clan.PlayerClan` checks see the placeholder, so real players are run as AI. Examples: an ultimatum accepted for a human ruler gives away their crown; a treason vote can execute a human clan's leader; humans' family married off, put under a regent, partitioned into cadet clans, given as hostages; their realm dragged into defensive pacts; their gold spent on espionage; their clan rallied into a rebellion or feud. | Server: where Bellum's AI acts for a clan or realm, skip clans that have a player hero (`Coop.IsPlayerClan`). Sites are listed per system below. | M |
| X5 | Player actions in Bellum's own screens, dialogs and inquiries run only on the PC (factions, ultimatums, feuds, law changes, titles, lordships, marriages, barters, council incidents). The player sees a result the server never got. | Players' PCs: hide or refuse them with one line: "In co-op, the server decides this." Where vanilla has a Coop-supported path, fall back to it (companion to lord, leave kingdom). | L |
| X6 | Popups and processors gated on `MapState` never run on a dedicated server. | Server: lift the gate where the processor itself is needed (ElectiveContest, landless realm recovery), as InternalPeace does. Leave popups dead. | L |
| X7 | Relations: `DynamicRelationPatch` overrides every relation read with Bellum's per-PC memory and writes it back. On PCs this fights Coop's synced relations. | Players' PCs: off. | L |

### Diplomacy

| System | Where it acts | Problem in Coop | Decision | Cost |
|---|---|---|---|---|
| Defensive pacts (`DefensivePact*`, `DefensivePactJoinDecision`) | S, P | Tick on every PC; the join decision is a Bellum type (AI-decided on the server, refused on PCs). A player's realm joins and is dragged into war with no vote. | X2 tick off on PCs; X4: AI pacts skip player-ruled realms. Later (M): register the join decision so players vote. | L / M |
| Hostage pacts, HostagePeace terms (`HostagePactBehavior`) | S, P | Hostages taken from a player's family; a held hostage can be executed when a pact breaks; judgment and disposition popups need MapState. | X2, X3, X4. | L |
| Retained treaty prisoners, household ransom | S, P | Release leaks from PCs (X3); free ransom. | X3; household ransom off on PCs. | L |
| Foreign treaties (`ForeignTreatyBehavior`) | S, P | On a PC every war without that player counts as AI-only, so the PC drafts and applies its own treaties (releases leak). | X2, X3. Server treaties for player realms: TreatyVote (done). | L |
| Voluntary pacts (`VoluntaryPact*`, `VoluntaryPactDecision`) | S, P | Same as defensive pacts; target choice needs MapState. | X2, X4. Later (M): register the decision. | L / M |
| Client (vassal) kingdoms (`ClientKingdomBehavior`) | S, P | Bellum vetoes `DeclareWarAction` / `MakePeaceAction` for client kingdoms, **including Coop's replays on PCs**, so a client kingdom at war with the server shows at peace on every PC. Also the `StartAlliance` leak. | Players' PCs: `ClientKingdomBehavior.Can*` answers yes (Coop's replay always wins). X3. | L |
| War score (`WarScoreBehavior`, `WarPeaceRevampBehavior`) | S | The PC's widget shows the PC's own score. Terminal war score ends feuds directly on the server (good). | X2 resolution off on PCs. Mirroring the score to PCs: later (M). | L |
| Espionage (`ProxyWarBehavior`) | S, P | Spymaster missions on a PC change only that PC; the server spends a player's gold. | Spymaster off on PCs (X5); X4. | L |
| Royal Peace decrees, Appeasement, ComradesInArms, MarriageAlliance, ConflictCallResponse | mixed | Appeasement barters only change the PC. ConflictCallResponse is letters about the player's own rebellion (not an allied call to arms), shown from the PC's own rolls. | Appeasement off on PCs; ConflictCallResponse off on PCs; decrees tick off on PCs; others as is. | L |

### Court and internal politics

| System | Where it acts | Problem in Coop | Decision | Cost |
|---|---|---|---|---|
| Court agenda, `MandateReformDecision`, realm laws | S, P | X1 duplicates; mandate ballots refused on PCs daily; AI picks court business (wars, protection) for player-ruled realms. | X1, X2; X4: no AI court business for player-ruled realms. Later (M): register the decision. | L / M |
| Policy, expulsion, fief deliberations | S, P | X1 duplicates (fiefs reassigned twice). Bellum intercepts the vanilla policy and expel buttons and queues them on the PC; they reach the server later, free. | X1, X2; let vanilla's buttons through on PCs so Coop sends them as normal. | L |
| Privy council (`PrivyCouncilAppointmentDecision`) | S, P | Players never vote on appointments; a PC's council differs from the server's. | X2. Later (M): register the decision; send the office holders to players. | L / M |
| Council incidents | P | Coop's `Incident.InvokeOption` prefix eats the click (Bellum's incident isn't a registered object), so the choice applies nowhere and the incident stays pending. | Off in Coop. | L |
| Treason and purges (`IdeologyBehavior`) | S | A treason vote against a human clan runs the AI rebel-or-execute path: `KillCharacterAction`, fief confiscation, forced civil war, no prompt. | X4: for a player clan, the outcome is a plain expulsion. | M |
| Lobbying, bribes, controversies, crown authority, ideology shocks | S, P | Barters never pledge on the server. | Lobby, bribe and indict dialogue lines off on PCs (X5); ticks X2. | L |
| Bellum's game models | S, P | Read local state, but the server computes the real values and Coop syncs them; on PCs only previews differ. | Leave as is. | – |

### Succession, titles, households, marriage

| System | Where it acts | Problem in Coop | Decision | Cost |
|---|---|---|---|---|
| Crown accession on a ruler's death (`CrownAccessionBehavior`, `HereditaryDeathAccessionPatch`) | S | Bellum patches `KillCharacterAction.ApplyInternal`, which Coop replaces, so Bellum's hook never runs: Coop picks a ruler and opens a king vote, then Bellum installs its heir an hour later. The crown changes twice. | Server: when Bellum owns the death (`OwnsDeath`), Coop's leader-death handler steps aside and Bellum's `AfterDeath` runs. **Confirm in game first.** | M |
| Succession law, challenges, elective succession and contests, partition, regency | S, P | Ticks on PCs; `ElectiveContestBehavior`'s driver needs MapState (dead on the server, live on PCs, where it can set a ruling clan locally). On the server an AI regent can take a dead player's clan, a player's clan can be partitioned, an AI answers a challenge for a player's realm. | X2; X6 for ElectiveContest; X4 for regency, partition, challenges. Player law change and abdication: X5 off. | L / M |
| Feudal titles, `Hero.Name` prefix | P | The prefix is a getter postfix (no write, no sync), so each PC shows titles from its own records. Its name cache is changed inside the getter, which races if Coop reads names off the main thread. | X2 title ticks off on PCs; title actions off (X5); name cache under the module's lock. Titles a player sees can lag the server; syncing them is later (H). | L |
| Title acquisition, usurpation, claim fabrication, de jure drift, feudal service | S, P | Same as titles. | X2; player actions X5. | L |
| Lordships (`LordshipBehavior`) | S, P | Dialogs and indentures only change the PC. | X2; dialogs off (X5). | L |
| Companion subinfeudation | P | Bellum's dialog leaves a ghost clan on the PC and does nothing on the server. | Off on PCs; vanilla "make companion a lord", which Coop supports. | L |
| Cadet households, mercenary bands (`DynamicMercenaryBandService.TryCreateBand`) | S, P | `Clan.CreateClan` on every PC: local ghost clans with the server's id. On the server, players' family members can be split off. | Refuse clan creation by Bellum on PCs; X4. | L |
| Marriage (strategic, specific proposals, player agreements) | S, P | `PursueDynasticMarriage` marries off players' family on the server; Bellum's player marriage agreements live only on the PC. | X4 (no direct marriage of a player clan's members); Bellum's player marriage dialog off on PCs (vanilla marriage barter stays). | L |

### Civil wars, rebellions, claim feuds

| System | Where it acts | Problem in Coop | Decision | Cost |
|---|---|---|---|---|
| Factions, ultimatums, rebellions (`FactionManagerBehavior`, `FactionObject`) | S, P | Server: a human ruler's ultimatum is accepted by AI (crown handed over), human clans are rallied into rebellions. PC: the Factions tab actions (create, join, ultimatum, disband, surrender) run locally, some calling `ResolveLiegeVictory` / `ResolveRebelVictory` directly. Refused kingdoms retry, so the ultimatum can show again. | X2, X4, X5. "Leave kingdom, keep fiefs" already reaches the server (Bellum adopts it as an independence war); keep it. | L / M |
| Called to a civil-war side (`CivilWarPlayerCallContext`) | P | The choice applies only on the PC. | X5 off; the server's X4 keeps a player clan out. | L |
| Civil war resolution, landless realms | S | `ProcessLandlessRealmRecovery` needs MapState, so landless realms never retire on the server. | X6. | L |
| Claim feuds (`ClaimFeudBehavior`, `ClaimFeudWarBehavior`) | S, P | Feuds end on the server by war score, the daily tick, surrender or royal peace (not screen-gated), plus council votes through InternalPeace (covered). Server presses claims and judges petitions for players. PC petitions and surrenders stay local. | X2, X4, X5. | L / M |
| `ConflictOutcomeBehavior` | S | MapState-gated; if the placeholder clan ever sits in a kingdom, its queue never empties and blocks post-war tribunals. | Check in game; X6 if needed. | L |
| First-year civil-war grace | S | Set at new game and reaches players in the save; date-based, so every PC agrees. | As is. | – |
| Diplomacy mod's direct peace | P | `DiplomacyDirectPeaceCompatibilityPatch` is missing from the module's peace targets. | Add it to `BellumPeaceTargets`. | L |

## Stage 2: Bellum's AI leaves players alone on the server

Until stage 3 asks players, Bellum's AI must not act *for* or *against* a real player's clan, hero or realm on the
server. Bellum tells the player apart with `== Clan.PlayerClan`, which on a dedicated server is the placeholder, so
every real player gets the AI treatment.

**Design.** One data-driven patcher (`PlayersNotAi`): a list of Bellum methods, each with the argument that names a
clan, hero or realm, and the answer to give when that is a player's (skip, false, null). On the server, a prefix
reads the argument and, for a player's, answers without running Bellum's AI. The list is chosen so each early answer
leaves no half-done state (no pending flag that would wait forever). Each entry is logged at install; one missing is a
"not found" line the `bellum.compat` step fails on. Single player and players' PCs are untouched.

**What it covers** (high harm first):

| Harm | Bellum path | Answer for a player's clan or realm |
|---|---|---|
| Crown given away | a court faction's ultimatum to a player-ruled realm, accepted by the AI | the faction issues no ultimatum there |
| Leader executed, fiefs confiscated | a treason or expulsion outcome against a player's clan | a plain expulsion only |
| Family married off | strategic and dynastic marriages | no marriage of a player clan's members without their offer |
| Clan split or ruled by a regent | partition, cadet households, regency, mercenary bands | none for a player's clan |
| Crown contested | succession challenges and disputed elections answered by the AI | not for a player's realm |
| Dragged into war | defensive and voluntary pacts, court business, protection | not for a player's realm |
| Hostages | taken from a player's family, or executed while a player holds them | none |
| Gold spent | espionage paid from a player's purse | none |
| Clan rallied | into a court faction, rebellion, civil-war side or feud | not a player's clan |

Found while testing: a new player's clan is a minor clan on the server, and Bellum refuses to charge a minor clan
with treason, so the treason entries rarely fire today; they stay for clans that grow out of it. Bellum's other AI
tests (factions, marriages, regency, partition, feuds, refuge) do take players' clans, and those entries fire.

Choices made here (Andy can reverse them): a player's clan is kept out of Bellum's hereditary crown line (a player can
still be elected or made ruler in vanilla ways), and a disputed election is skipped in any realm with a player in it.

**Crown accession.** Coop replaces `KillCharacterAction.ApplyInternal`, where Bellum hooks a ruler's death, so Coop
picks a new ruler and opens a king vote, and an hour later Bellum installs its own heir: the crown changes twice.
On the server, when Bellum owns that death, Coop's leader-death handling steps aside and Bellum's accession runs once.

**Tests** (module `bellum`):
- `players-not-ai`: on the server, Bellum's own cheats aim each path at the player's clan or realm (an ultimatum, a
  treason charge, a marriage, an heir challenge, a pact); the player's hero is alive, rules, keeps its fiefs and family,
  and is at peace, on both sides. Each check fails without the module's entry.
- `crown`: an AI realm's ruler dies on the server; the crown changes once, to Bellum's heir, and the same on the
  player's PC, with no king vote left open.

## Player agency (stage 3)

Without it, co-op players live in a world Bellum runs but can't play its politics. They can't found or join a faction,
issue an ultimatum, lead a rebellion, press a claim, sit on a council, change succession law, take titles or grant
lordships. Nothing is put to them, and a realm a player rules plays like a vanilla kingdom. Players would keep only
vanilla politics: votes (including Bellum treaties, through TreatyVote), proposals, and leaving a kingdom with fiefs.
That is most of what Bellum adds for a player, so the module lets players in.

Bellum's player paths are wired to `Clan.PlayerClan` / `Hero.MainHero`, but they reduce to four pieces, built once:

1. **Prompts to a player.** Where the server's Bellum would ask "the player", it asks the player concerned. The
   question (title, text, options) goes to that player's PC, the answer comes back, and the server applies it. If
   nobody answers in time, Bellum's AI answers as it would for a lord. (Pattern proven by ModderLords' 1.3.1 adapter:
   `BellumPrompts` / `BellumPromptSites`.)
2. **Actions from a player.** Bellum's own buttons and dialog lines on a player's PC (Factions tab, feud screens,
   Succession tab, title and lordship dialogs) don't apply locally. They send a request ("found a faction with these
   clans", "press this claim") and the server applies it.
3. **"As this player" on the server.** While the server applies an answer or a request, `Clan.PlayerClan`,
   `Hero.MainHero` and `MobileParty.MainParty` answer for that player (a main-thread, call-scoped override of the
   getters), so Bellum's own appliers run unchanged. Elsewhere, Bellum's `== Clan.PlayerClan` tests that keep the
   player out of AI loops read "is any player" (`PlayerComparisonRewriter`, which ModderLords already has, rebuilt
   inside the module). Bellum's UI and inquiry calls inside an applier are skipped on the server.
4. **Bellum's state on a player's PC.** When a player opens a Bellum screen (Factions, Hierarchy, Succession, feuds,
   council), the server sends the part of its Bellum state that screen reads, so the screen and title names match the
   server.

**Channel.** The module has no network messages today. The pieces use Coop's own message system (`ICommand` /
`IEvent` with handlers in Coop's `Coop.Core.Server.Services.*` / `Coop.Core.Client.Services.*` namespaces, which
ModderLords.CompatSync.Coop already uses). To keep the module loading in single player and against a changed Coop,
the network part is a second assembly in the module (`BellumCivileCoopCompat.Net.dll`) compiled against Coop's DLLs
and loaded only once Coop is found. The core stays reflection-only. Server and players must run the same module
version (they already must for Coop).

**Risks.** Bellum code run "as a player" may show UI or touch other systems that read `MainHero` (other mods, Coop
itself): the override must be short, on the main thread, with Bellum's UI paths skipped on the server. Coop may
already patch some of these getters; check before building piece 3. Each feature gets its own Coop suite.

**Built (1.4.0).**
- *Channel.* `BellumCivile.CoopCompat.Net.dll` is built against the Coop nightly and loaded by the module once Coop's
  `GameInterface` is loaded. Two generic messages (`BellumAgencyRequest`, `BellumAgencyMessage`) carry a kind and
  strings. Coop's handler discovery (namespaces `Coop.Core.Server.*` / `Coop.Core.Client.*`) builds the handlers when a
  session starts. The server takes the sender from Coop's player list, never from the message. A PC on another
  protocol is refused and told once.
- *Acting as a player.* `AsPlayer` opens Coop's own `MainHeroSubstitutionScope` by reflection for one call on the
  main thread. While it is open, stage 2's guards let Bellum treat that player as the player.
- *Prompts.* A site runs as its connected player; the question Bellum would show is caught, sent to that player and
  shown there; the answer runs Bellum's own yes or no as that player. Unanswered after `Prompts.AnswerSeconds` (600 s),
  the site's fallback answers. First site: a court faction's ultimatum to a realm a connected player rules (fallback:
  refuse; the player keeps the crown). A disconnected ruler is not asked, and stage 2 keeps the ultimatum from
  happening.

- *State.* The server runs a Bellum behaviour's own `SyncData` into a recording data store (as a save does) and sends
  it to players; their PC runs the same `SyncData` from it (as a load does), then drops Bellum's lookup caches. Game
  objects travel as ids (a player's hero and clan by name, since their ids differ between PCs), Bellum's records as
  the game's saveable fields, types without assembly versions. Mirrored: `FactionManagerBehavior`,
  `ClaimFeudBehavior`, `FeudalTitleBehavior`; sent each game day, on request, and after a player's action.
- *Factions tab.* Join (or leave), Found, Ultimatum and Create send the selection; the server builds Bellum's own
  `FactionsWindowVM` as that player, selects the same faction and action from its state, and presses the same button
  (Bellum's checks and costs apply there). A confirmation comes to the player as a question, Bellum's lines to "the
  player" as notices, and the player then gets the server's state.

**Order** (what players feel most first; each is a small adapter on the four pieces plus a suite):

| Step | Features |
|---|---|
| A1 | Being asked: an ultimatum to your realm, a call to a civil-war side, treason solidarity, a succession opportunity, feud rulings for your realm. |
| A2 | Factions: found, join, issue an ultimatum, lead and surrender a rebellion. |
| A3 | Claim feuds: press, petition, surrender, withdraw. |
| A4 | Titles, lordships, succession law, abdication. |
| A5 | Council seats, appointments and incidents; Bellum marriages; hostage judgment; regency of your clan. |
| A6 | Players vote on Bellum's own decisions (`DefensivePactJoinDecision`, `VoluntaryPactDecision`, `MandateReformDecision`, `PrivyCouncilAppointmentDecision`) through Coop's `KingdomDecisionTypeRegistry`; the payload carries the Bellum state their text and `IsAllowed` read. |

As each step lands, its X5 "off on players' PCs" line becomes "routed to the server", and its X4 guard becomes a
prompt to the player.

## ModderLords' older Bellum adapter (1.3.1)

Two layers, both written for Bellum 1.3.1:

- **Layer A, the operation adapters** (`src/ModderLords.CompatSync.Coop/Operations/BellumCivile*Adapter.cs`,
  `BellumPrompts`, `BellumPromptSites`, `BellumPlayerClans`, `BellumClientProjection`, `BellumScreenRefresh`; docs
  `BELLUM-CIVILE-ADAPTER.md`, `BELLUM-VALIDATION-INSTRUCTIONS.md`). They install only with the validation token and a
  SHA-256 pin on the 1.3.1 DLL, so **on 1.5.1 they never install**. If forced, they'd do harm: several targets are
  gone (`DynasticHeirBehavior.OnHourlyTick`, `StrategicMarriageBehavior.OnDailyTickHero`, `OnDailyTick_Treason`, the
  fief redistribution builders, `_realmLaws`), and `OnRulingClanChanged`'s second parameter changed meaning.
- **Layer B, ModderLords' built-in copies of the module's patches** (`src/ModderLords.CompatSync.Coop/Bellum*.cs`).
  `Bridge.Tick` turns them off whenever `BellumCivileCoopCompat.Hooks` is loaded, and the launcher always loads the
  module next to Bellum, so they are dormant. As a fallback they are stale (no internal peace; 1.5.1 dropped the
  minimum-war check they rely on).
- `supported-mods.json` claims Bellum's prompts "open one at a time and never during a battle". That was Layer A's
  validation mode and isn't true today.
- ModderLords' own mod-decision sync (`ModKingdomDecisions.cs`) hooks `ServerKingdomHandler`, which Coop nightly no
  longer has, so it only logs "changed shape".

**Proposed: retire both layers** in their own PR. Keep `StandaloneBellum.cs` (the module's Hooks wiring),
`ClientKingdomDecisions`, `KingdomVoteTimeoutAi`, `SerializedState` and `PlayerComparisonRewriter`. Correct
`supported-mods.json`. Moving `ModKingdomDecisions` to `KingdomDecisionTypeRegistry` is a separate follow-up.

## Test plan (module `bellum`, Coop suites)

Each suite compares the server with `client:1` and has at least one check that fails without the module's fix (run
once with the fix turned off to prove it).

| Suite | Checks |
|---|---|
| `coop` (exists) | Module patches found; civil war ends on the server; a player's peace vote ends a war. |
| `treaty-vote` | A Bellum treaty drafted for the player's realm arrives as a peace vote with the tribute; voting yes signs Bellum's treaty on both sides; voting no keeps the war and isn't asked again. |
| `alliance-block` | With an ally's call to arms on the war, the player's peace vote is blocked on the server and the war stays on both sides. |
| `feud` | A claim feud started on the server ends on the server (war score or settlement) and the player sees it end. |
| `no-kingdoms` | Bellum on the player's PC tries to found a kingdom: no kingdom without an id appears, and the kingdom lists match. |
| `firewall` (X1, X3) | A run of game days with an idle player: the server gets no decisions, captivity ends or alliances from the player's PC; the player sees no "can't be proposed" message; vanilla decisions on the server aren't duplicated. |
| `players-not-ai` (X4) | Server: an ultimatum, treason vote, dynastic marriage, regency and pact against the player's clan or realm leave it unchanged. |
| `client-kingdom` | A client kingdom's war is at war on the player's PC too. |
| `crown` | The player's realm's ruler dies: the crown changes once, to the same heir on both sides. |
| `relations` | After server-side relation changes, the player's and the server's relations with a lord match. |
| `player-actions` (X5) | Bellum's player actions are refused on the PC with the co-op line; vanilla leave-kingdom still reaches the server. |
| `agency-*` (A1 to A6) | One suite per step: the server's prompt reaches the player and their answer applies on the server and both sides agree; with no answer the AI decides after the timeout; a player's request (found a faction, press a claim, take a title) applies on the server and the player's Bellum screen shows the server's state. |

## Open questions (check in game before writing the patch)

1. Harmony prefix order on `Kingdom.AddDecision` (Bellum has about 10 prefixes; Coop's returns false).
2. Whether Coop's join transfer carries Bellum's `SyncData` (expected: yes, once).
3. Whether an unanswered inquiry on the dedicated server leaves `InformationManager.IsAnyInquiryActive()` true, which
   would block every Bellum feature gated on it.
4. What `Clan.PlayerClan.Kingdom` is on the server (affects `ConflictOutcomeBehavior`).
5. Whether clans the server creates reach players renamed `Created_<n>`, which breaks Bellum's id checks.
6. Crown death: Coop's `HandleKingdomLeaderDeath` and Bellum's accession, observed on a ruler's death.

Raw survey notes (file:line citations into the decompiles) were kept outside the repo with the session notes.
