# Bellum Civile adapter status

Scope: Bellum Civile `1.3.1`, Bannerlord `1.4.8`, War Sails disabled. Bellum is intentionally version-pinned;
Coop is not. This adapter does not modify or redistribute Bellum files.

## Completed foundation

- The pinned Bellum world creates, saves, reloads, reaches Coop `SERVING`, and shuts down cleanly.
- Coop managed admission is guarded by exact capability/signature probes instead of a historical `Coop.Core.dll`
  hash. Session attestation still requires the server and client to agree on the bytes selected for that session.
- `bellum-civile.state` and `bellum-civile.authority` are bundled compiled contracts with both validation flags
  false. They therefore report `ValidationRequired` and cannot activate automatically. The exact environment token
  `bellum-civile-1.3.1-isolated` can activate only those two contracts in a disposable validation process; it does
  not alter the catalog or production flags.
- Bellum state is copied into an explicit ID-only schema. Live TaleWorlds/Bellum objects and arbitrary reflected
  graphs are never serialized.
- The schema now has explicit ID/value codecs for all 36 reviewed nontrivial Bellum `SyncData` fields. In addition
  to the primary political records, it covers war score/will, foreign wars, services and drift, fabrication,
  civil-war/faction snapshots, relation history, dynastic succession, and every reviewed pending-resolution record.
- Snapshot transfer is bounded at 32 UTF-8 chunks of 224 KiB, supports out-of-order delivery, rejects conflicting
  chunks and stale revisions, and reassembles only inside the authenticated session epoch.
- The client mirror replaces its read model only after a complete schema-valid snapshot.
- Reviewed server mutation handlers mark the shared Bellum snapshot dirty. Changes are coalesced for at least two
  seconds, captured once, revision-deduplicated, and fanned out to all currently admitted actors through the same
  bounded transport. Actor-specific snapshot operations cannot opt into this shared path accidentally.
- The current client projection reconstructs every represented Bellum state family in the 36-field schema,
  including faction, title/claim, succession, regency, treaty/tribute, council, feud, mercenary, war-score/will,
  foreign-policy, service/drift/fabrication, relation/memory, dynastic/cadet and pending-resolution state. It resolves
  TaleWorlds references only by stable IDs against existing campaign objects and invokes Bellum's pinned
  lookup/index/cache rebuild hooks.
- The live probe now goes beyond signature checks: it builds the authentic projection graph, then deep-clones that
  snapshot and adds an in-memory representative of every empty record family and nested constructor. Both graphs
  pass codec and materialization validation; the coverage fixture is discarded and never touches the campaign or
  disk. Committing the authentic graph in a joined client remains pending.
- `bellum-civile.authority` now records 71 exact Bellum method surfaces. It gates ordinary campaign mutation
  callbacks and mutation-only callees while retaining mixed UI/presentation handlers on clients. The Bellum DLL is
  still strictly pinned; the contract's validation flags remain false, so these patches cannot activate in a normal
  launch yet.
- Five mixed callbacks are deliberately excluded from whole-method gating: council incident presentation, dynamic
  mercenary departure presentation, treaty daily/presentation processing, and the war-score map widget. Their
  authoritative state and UI handoff must be completed before activation.
- The isolated dedicated-server activation now passes end to end. The plan freezes both adapters before campaign
  initialization, the save reaches `SERVING`, and Bellum mutation handlers execute only on the server.
- Native client validation on 2026-09-29 completed initial admission, campaign-map entry, more than one in-game day,
  disconnect and reconnect. Both joins agreed the same operation plan. The reconnect transferred all 62 save
  chunks, restored the player party, and returned the player to the map. Shared Bellum snapshots advanced through
  revision 11. The server recorded 4,708 executions across 23 authority-gated handlers with zero client-side runs;
  the client recorded 1,338 suppressed mutation calls with zero server-side runs. The available Kingdom and Clan
  views remained stable. Bellum-specific action controls were unavailable because the disposable character did not
  meet their political eligibility requirements, not because navigation or the adapter failed.
- The first actor-aware command slice is implemented behind a third disabled contract, `bellum-civile.commands`.
  It covers title fabrication, usurpation, formation, dissolution, rename, service-level change, grant and revocation, plus
  gender and house succession-law changes, claim-feud petitions, crown-enforced feud peace, privy-council appointments and dismissals, and (since the offline follow-up) policy, expulsion and fief-revocation proposals. Bellum's native confirmation UI remains local; only the final mutation callback is
  intercepted. Requests contain stable IDs, never Bellum objects, and the server replaces any claimed actor with
  the authenticated Coop hero/clan before rerunning Bellum's own eligibility checks. A client that has never received Bellum state is refused;
  Bellum's own checks decide the rest (see the live validation below). The client reports completion only after the correlated server result and then asks
  for a fresh authoritative snapshot. All seventeen patched Bellum 1.3.1 methods have pinned IL surfaces.

## Live snapshot evidence

An opt-in one-shot probe (`MODDERLORDS_BELLUM_SNAPSHOT_PROBE=<absolute path>`) was run against the disposable
Bellum save on 2026-09-28. It does not activate the contract or patch Bellum.

The exact previously validated save loaded, reached `SERVING`, built the authentic and discarded full-family client
projection graphs without mutating server state, emitted a 368,682-byte schema-valid authentic snapshot, then saved
and stopped cleanly with exit code 0. Snapshot counts were 205 titles, 29 factions, 8 succession-law records, 48
council-office records, 4 war-score records, 73 war-will records, 73 pressure records, 4 active foreign wars, 606
relation-memory records, and 8 dynastic-succession records. The live save had no active claims, treaties, tributes,
regencies, feuds, mercenary bands, client kingdoms, services, drift, fabrications or pending resolutions at this
checkpoint; those empty families were exercised only in the discarded fixture. The payload contains no live
`TaleWorlds` objects or arbitrary type metadata. Its SHA-256 was
`633001F16437C39C5A81BF632524376F25883B309A7B324C6516F9640181F800`.

An earlier 5,579,137-byte capture containing 26,562 relation memories separately proved that the same codec and
chunk transport remain below their 7 MiB bound under a much larger real payload.

Evidence:

- Full-family projection launch: `C:\Users\A\AppData\Local\Temp\modderlords-launch-20260928-193919.log`
- Snapshot: `D:\Design\Bannerlord Mods\_bellum-civile-analysis-data\live\BellumCompatSmoke-full-family-projection-snapshot.json`
- Isolated two-adapter server activation: `C:\Users\A\AppData\Local\Temp\modderlords-launch-20260928-195407.log`
- Client startup/admission log: `C:\Users\A\Documents\Mount and Blade II Bannerlord\Configs\ModLogs\ModderLords.Compat-client.log`
- Successful joined-client and reconnect run: the 2026-09-29 01:53-02:12 entries in the same client log and the
  corresponding `ModderLords.Compat-server.log`; the server launch log records module validation acceptance,
  62-chunk save transfer, campaign entry, party restoration, and map entry.

## Live validation, 2026-09-30

First native run with a politically eligible player (solo ruler of a debug-created kingdom).

Fixed during the run:

- Coop's `Player.HeroId` is an object-manager id (`Hero_Player`), not the hero's `StringId` (`Player`). The command
  adapter looked it up in `MBObjectManager` and refused every action as "Actor clan ownership changed"; TAOM and
  Living Economy notice forwarding had the same mix-up. All four now resolve through `PlayerHeroes`.
- The snapshot revision counted `CapturedDay`, so it moved every capture while campaign time ran. The exact-revision
  requirement was then dropped altogether: Bellum's simulation changes political state every few seconds, and each
  action already runs through Bellum's own `Try*` service against live state.
- Bellum screens only re-read state when refreshed; the open screen is now refreshed once after the player's own
  completed action.
- Validation deploys must sync both `Win64_Shipping_Client` and `Win64_Shipping_Server`; the dedicated server loads
  the latter.

Verified: a succession-law change completed end to end, and 14 vanilla policy votes (solo ruler, two kingdoms, three
sessions) resolved on the server and reached the client.

Observed but not reproduced after the save was restored: in the original save, a resolved policy vote's decision was
never removed on the server, so the client kept a stale decision and later votes (addressed by list index) missed.
An A/B run without Bellum did not show it. `KingdomDecisionTrace` (validation only) records every decision/policy
add and remove on both sides, `ApplyResolved`, the election steps with a finalizer, and the runtime list of methods
patched by both Bellum and Coop, so a recurrence is diagnosable from logs.

Known gaps found, and where each stands after the offline follow-up below:

- `PrivyCouncilBehavior.EnsureRecordIndexes` threw "same key already added" from Coop's parallel moving-party tick.
  **Fixed for every Bellum user** (`BellumThreadSafety`), see below.
- The deliberation ticks were rated "Local" by the authority analyzer and ran on every client. **Gated server-side**
  (validation mode), and the analyzer blind spot is fixed in a separate change (see below).
- Bellum's player-input patches created client-local Bellum state. **Policy, expulsion and fief-revocation
  proposals now go to the server**; the remaining patches are listed in the patch audit below.
- Bellum refers to `Clan.PlayerClan`/`Hero.MainHero` about 900 times; on a dedicated server that is the placeholder
  hero. **Open**: needs design decisions (see below) and a two-player run.

## Offline follow-up, 2026-09-30

Done without a game session; installation verified on a headless validation server (Coop will not run campaign time
with no player connected, so nothing below has been exercised in play yet).

**Thread safety.** Bellum's party-size and army-food model postfixes (`CouncilAssignmentRuntimePatches`) read the
privy-council and council-incident indexes for every moving party, and Bannerlord ticks moving parties on several
threads (vanilla does too, so this is Bellum's own bug that Coop makes likelier). Both indexes rebuild lazily behind a
dirty flag; `CouncilIncidentBehavior.EnsureAspectIndex` also rebuilds whenever an aspect expires and invalidates the
council cache. `SerializedState` runs every method that touches those fields under one re-entrant lock (12 + 10 methods
on Bellum 1.3.1). It is installed whenever Bellum is loaded, not only in validation mode: it changes when a thread may
run, never a result. A race test reproduces the crash without the lock and never throws with it.

**Deliberation.** The policy, fief, expulsion and council-appointment deliberation ticks fire queued votes into
`Kingdom.AddDecision`. Clients load the server's vote queue with the save and ran those ticks too; Coop turns a
client's `AddDecision` into a request, so the same AI vote could be added once by the server and once per client,
and the clean-up (`RemoveDecision`) is refused on clients. That fits the stale-decision loop seen in the original
save. The six handlers are now server-only (77 authority gates). Because Bellum's `AddDecision` prefixes run before
Coop's and stop it, a player's own proposal had only ever reached the server through that client's tick, so the three
proposal entry points (`QueuePlayerProposedVote`, `TryStartTreasonVote`, `QueueRevocationSettlementVote`) are now
commands executed on the server as that player, with the actor's clan as proposer (17 commands). Known loss: Bellum's
own deliberation notifications are now raised on the server, so players see the resulting vote but not the
"the court will deliberate" message.

**Needs a session to confirm:** a non-solo player proposing a policy, an expulsion and a fief revocation; the vote
firing after the deliberation days; no duplicate decisions with two clients connected.

## Patch audit (Bellum 1.3.1)

The improved authority analyzer sees 95 attribute-declared patch classes (116 patch methods); the rest of the 124
are attached at runtime (`CouncilAssignmentRuntimePatches`, compatibility patches for Diplomacy/CustomSpawns chosen in
`TargetMethods`). By most severe verdict per class: 33 Local (display), 23 Both (deterministic, needs identical data),
5 already handled by Coop, 1 leaking postfix, 33 to review. The ones that matter:

- `Kingdom.AddDecision` prefixes (policy, fief, expulsion, foreign-policy, NPC budget, temporary feud): run before
  Coop's prefix and return false, so Coop never sees the call. Player paths for the first three are now routed; the
  foreign-policy and feud ones still queue client-locally if a player triggers them.
- `KingdomPeaceDecisionAddedParleyCleanupPatch` (postfix on `Kingdom.AddDecision`): runs on clients although Coop
  skips the original there.
- Decision outcomes (`PolicyVoteResolutionPatch`, `FiefVoteResolutionPatch`, `KingSelectionAIPatch`,
  `ExpelClanDecisionPatch`): change relations, settlements and decisions; they run where the decision resolves, which
  is the server, but nothing stops a client from running them if it ever resolves one locally.
- `NpcKingdomDecisionPaymentBudgetPatch`, `WarDeclarationInfluencePaymentPatch` (`KingdomElection.HandleInfluenceCosts`):
  change influence, which Coop does not intercept.
- `DynamicRelationPatch`: a postfix on `CharacterRelationManager.GetHeroRelation` that, on a read, can write the
  relation (`SetHeroRelation`) and record relation memories. Every peer does this whenever its UI or AI reads a
  relation, so relations can drift apart between peers. No crash has been seen; not locked, because it is the
  hottest path Bellum patches and there is no evidence of a race yet.
- `Guard*ScorePatch` (diplomacy model queries): set `Kingdom.RulingClan` inside a query.
- Ten `TargetMethods` patches: targets chosen at runtime, so only the runtime shared-patch dump shows them.

## The "player" on a dedicated server

Bellum asks "is this the player's clan?" in about 900 places. On a dedicated server `Clan.PlayerClan` is the
placeholder hero's clan, so with the server now authoritative, every real player is treated like an AI lord in the
simulation: votes are cast for them, and choices Bellum offers "the player" are made by AI or skipped. The command
adapter already runs a player's own action as that player (`PlayerScope`), and ModderLords has a rewriter that turns
"is this the player's?" into "is this any connected player's?" for server-run code (`PlayerComparisonRewriter`, used
for other mods).

Decided by the maintainer (2026-09-30):

1. A player gets the choice, on a timer of **10 minutes**; with no answer, the AI decides as it would for a lord.
2. An offline player simply times out (decided at once: there is nobody to wait for).
3. Every Bellum prompt that involves a player goes to that player.

The AI must keep acting on absent players (declaring war, revoking titles), even though their party cannot be
interacted with directly.

**Kingdom votes** (PR #146): Coop already gives player clans a 60 s voting round on vanilla decision types, which
Bellum's policy, fief, expulsion and king-selection votes use; a player who does not vote is now decided for by the AI
instead of abstaining. Bellum's own `PrivyCouncilAppointmentDecision` is a type Coop cannot send to clients
(`KingdomDecisionDataConverter`), so players never see that vote yet.

**Prompts** (`BellumPrompts`, `BellumPromptSites`, validation mode). A *site* is the Bellum method that raises a
player prompt, a function naming the player it is for, and Bellum's own AI decision for when that player does not
answer. The site runs as that player (`PlayerScope`), so Bellum takes its player branch with their names; the inquiry
it opens goes to their game (FbPromptWire under feature `bellum-ui`, the shared action channel), and their answer runs
Bellum's callback on the server as them. Where Bellum's "is this the player's?" means "players choose for
themselves" (enlisting kin in a feud, AI revocations, AI claim fabrication) the comparison is rewritten to any player.
Every site and rewritten method is pinned in the `bellum-civile.commands` contract.

First set, claim feuds: crown judgment (AI: Bellum judges the petition as for a lord's realm), a party's answer to the
ruling (AI: `GetRulingResponses` for that clan), the call to arms (AI: answer it, as a lord who qualifies does) and a
title revocation demand (AI: `TryExecuteRevocation`, where the holder's defiance is Bellum's own call).

Known limits: Bellum models one player per decision, so if the ruler and a party of the same feud are both players,
the second is decided like a lord. Prompts still waiting when the server stops are not saved; Bellum re-raises its
pending feud prompts each day, so those come back. Not yet exercised in play: the save used for headless runs has no
title claims, and petitions resolve on the daily tick, which Coop does not run with no player connected.

Still to do, in this order: the remaining choice prompts (civil war tribunal and call to arms, white peace, forced
surrender parley, succession ultimatum, court agenda and faction leadership, asylum, mercenary departure, policy
deviation), Bellum's notices to a specific player, and the council-appointment vote.

**Players' clans are noble houses** (`BellumPlayerClans`, validation mode, both sides; maintainer's call, 2026-10-01).
The game defines the player's clan as a minor faction (SandBox `spclans.xml`: `player_faction` has
`is_minor_faction="true"`), and nothing changes that at any tier or rank. Bellum's eligibility filters therefore say
"minor factions are excluded, except the player's clan", and its AI-only filters say "never the player's clan". On a
dedicated server `Clan.PlayerClan` is the placeholder, so every real player was excluded from claim feuds, votes,
council and service roles, while Bellum's AI could pick players' clans as if they were lords'. The player comparisons
within 10 IL instructions of an `IsMinorFaction` read are rewritten to "any player's clan" in 34 methods (37
comparisons), found by an IL scan of Bellum 1.3.1. These include compiler-generated lambda methods; the cheat commands
and the testing-only treason trigger are left out. Other "the player at this game" checks in the same methods (menu
conditions, tooltip text, a player ruler's refuge) are 16 or more instructions away and stay as written. All 34 are
pinned in `bellum-civile.commands`, now 60 surfaces. `CecilSurface.Find` now matches the exact type name first: the
`<`-cut normalisation made every compiler-made nested type look like its parent, so the capture tool had silently
found the first one.

## Deliberately not claimed

The adapter is not full playable compatibility yet. The first simulation-authority and full read-model tiers are
implemented and have passed one native client admission/reconnect run, but remain deliberately isolated pending
UI/action work. Seventeen player actions now have an actor-aware command path, but the remaining faction,
treaty and player-targeted decisions do not. The five mixed callbacks remain outside
the authority gate. No political command has passed native validation; no two-simultaneous-player run,
dynamic-object replication stress case, save/restart verification after political changes, or long soak is claimed.
The validation flags must remain false
until those paths pass the integration matrix.

Next validation/implementation order: validate the initial title/succession command slice with a politically
eligible disposable character; add faction and treaty commands; add player-targeted
decisions; then run two-player, reconnect, save/restart and soak validation.
