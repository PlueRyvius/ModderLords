# Living Economy co-op layer

Bannerlord Living Economy (module id `BetterEconomy`, by ISKL, Nexus 10796, reviewed at v1.4.6) under Coop. Written
with the mod author's permission. The code is in `src/ModderLords.CompatSync.Coop/LivingEconomy/` and is built the
same way as the TAOM layer: one component per job, each checking the mod members it needs and switching itself off
with a log line if a mod update moved one. The layer is inert unless `BetterEconomy` is an active module, and every
client-side patch also checks that a co-op session is live, so single-player is untouched.

## What the mod is

18 campaign behaviours on daily, weekly and settlement events (population by class, village supply links, town and
castle economies, AI lord investment, village investment and development, cultural markets, estates, trade treaties,
caravans, economic events, lord wealth realism), 8 game-model overrides (prices, prosperity, settlement economy,
village production, party speed, size, wages, food), 5 Harmony patches, a "Living Economy" option in town, castle
and village menus with about 14 player actions, and a Gauntlet ledger on M. All of its state is in SyncData: plain
records (int/float/string fields) in `Dictionary<string, record>` books keyed by settlement id.

## What goes wrong under Coop without the layer

1. Every player's game runs its own economy, and the server runs another.
2. On the server `Clan.PlayerClan` / `Hero.MainHero` are the idle world-generation hero, so the mod treats every human
   player as an AI lord: it spends their gold on investments, castle treasuries and armories, picks their towns'
   tax policies and projects, trims their gold (lord wealth realism) and makes trade treaties for kingdoms they lead.
3. A player's menu actions change only that player's game.
4. Players' ledgers, menus and prices read records frozen at join time.

## The components

| Component | Side | What it does |
|---|---|---|
| `server-only` (`LeServerOnly.cs`) | client | Skips 28 simulation entry points (the behaviours' event handlers plus `RecruitmentPatch.ApplyRecruitmentDrain` and `VillageSupplyCampaignBehavior.ApplyRaidImpact`) on a player's game in a co-op session. Menus, the save store's session hooks, settlement intel, stats and every model stay. |
| `owner-scope` (`LeOwnerScope.cs`) | server | For the per-settlement daily ticks (and `OnClanDailyTick`, caravan `OnSettlementEntered`), when the settlement's owner clan belongs to a player, the handler runs inside `ServerRelay.PlayerScope` for that player. Every `OwnerClan == Clan.PlayerClan` check in those handlers is then right without rewriting any, and castle troop training delivers to the owner's party. Players are found from Coop's player list, and offline ones through Coop's `IsPlayerHero`. |
| `player-checks` (`LePlayerChecks.cs`) | server | `WealthAuditCampaignBehavior.IsEligibleLordForWealthControl`: `PlayerComparisonRewriter` turns its two player checks into "any player". `RunAiTreatyPass`: while it runs, `EvaluateAgreementValue` answers 0 for a kingdom a player leads, so the AI never signs for a player. |
| `notices` (`LeNotices.cs`) | server | Collects the lines the mod shows while a relayed action runs (sent back with the answer), and forwards the mod's notifications during an owner-scoped tick to that player. Stands aside for the second job when the TAOM layer is loaded (its forwarder does it). |
| `actions` (`LeActions.cs`) | both | Client: a prefix on each action method sends (op, args) over the shared `NetworkTaomAction` channel under feature `living-economy` and returns "sent to the server". Server: decodes the args by the method's parameter types, substitutes the sender's hero, checks the sender's party is at (or just left) that settlement and the gold amount is plausible, runs the mod's own method inside `PlayerScope`, and answers with the mod's own success or blocked line. Trade treaties agreed in a barter (`TradeAgreementBarterable.Apply`) are signed by the server if the sender rules one of the two kingdoms. |
| `state-mirror` (`LeStateMirror.cs`, `LeMirrorDelta.cs`) | both | The TAOM mirror's approach (the mod's own SyncData in saving mode on the server, loading mode on clients, through `MirrorStore`), with row-level deltas, gzip, sequence numbers (a client that misses one asks for everything again), and sends only when the day turns, after an action, or every 30 s. Also carries the caravan behaviour's unsaved `_caravans` table and a fingerprint of the regional and consumption profile XMLs. Uses `NetworkTaomState`; names starting `BetterEconomy.` route to this mirror. |
| `settings-guard` (`LeSettingsGuard.cs`) | both | Client: Ctrl+Shift+M cannot reload the XML over the host's settings during co-op. Server: skips the hotkey poll; holds the mod's own settlement culture conversion off (in memory) until its `Settlement.Culture` writes are confirmed to replicate. |

Relayed actions (method, arguments after the settlement):

| Method | Notes |
|---|---|
| `SettlementActionService.TryExecuteTownInvestment` / `TryExecuteVillageInvestment` (amount) | whole flow runs on the server, its lines come back |
| `TownEconomyCampaignBehavior.TrySetPolicy` (policy) | server checks the sender's clan owns the town (the mod only checks this in the menu) |
| `TownEconomyCampaignBehavior.TrySetTaxPolicy` (policy, playerAction) | server runs `CanPlayerSetTaxPolicy` first; playerAction forced true |
| `TownEconomyCampaignBehavior.TryStartProject`, `TryPlayerContributeTreasury`, `TryPlayerBuildOrUpgradeArmory` | |
| `FeudalEconomyCampaignBehavior.TryApplyEstateAction` | |
| `CastleEconomyCampaignBehavior.TryStartProject`, `TryPlayerContributeTreasury`, `TryPlayerBuildOrUpgradeTrainingCamp`, `TryPlayerStartTraining`, `TryPlayerCancelTraining` | |
| `VillageDevelopmentCampaignBehavior.TryPlayerNegotiateMarketAccess` | |
| `TradeAgreementBarterable.Apply` | op `treaty`, kingdom ids |

Settings: `BetterEconomySettings` (583 public static fields) is found by the static settings scan and follows the host
through Settings sync. `RuntimeSettings` is excluded by the compat record (`IgnoreSettingsTypes`): it holds each
player's own ledger preferences. Its two gameplay switches only matter where the simulation runs, the server.

Launcher: `LivingEconomyLaunchPolicy` refuses a Living Economy launch with Settings sync off, and warns if the
experimental Server-only logic is ticked for it (the generic player-check rewrite would let any player manage any
other player's towns). Compat record `BetterEconomy` (verdict Unknown until tested live). Module version 0.1.31.

## Checked here, not yet live

- `LeMirrorDelta` and `LeActionCodec` are compiled into the Core test project (`LivingEconomyTests.cs`).
- The whole layer was type-checked and its Harmony patches were run (Harmony 2.4.2, .NET 8) against a miniature
  stand-in of the mod with the same type names and method shapes: relay sends instead of running, server runs as the
  player with the mod's lines, the mod's own ownership check refuses someone else's town, a player elsewhere and a
  negative amount are refused, owner scope opens only for a player's settlement and restores after, wealth realism
  skips players but not AI lords, the mirror sends full then delta and a missed delta asks for a resend. That run found
  and fixed one bug: Harmony copies `__args` back over `out` parameters after a prefix that takes it.
- `MobileParty.LastVisitedSettlement` and `Kingdom.RulingClan` could not be confirmed against the 1.4.8 reference
  assemblies from here, so they are read by name at runtime with a fallback.

## Open questions for the live test

- Recruitment: when a player hires, does `RecruitmentCampaignBehavior.OnTroopRecruited` run on the server? If not,
  hires do not drain the settlement's manpower (the mod's client-side fallback, `RecruitmentAuditCampaignBehavior`, is
  skipped on clients because it watches the local main party); a server-side watcher over player parties is the fix.
- Relations from investments (`ChangeRelationAction.ApplyPlayerRelation` inside `PlayerScope`) reaching players.
- Whether Coop runs a player's barter results itself; if so the treaty relay answers `already_active`, which is fine.
- Offline players: whether Coop's `IsPlayerHero` still answers true for a registered player who is not connected.
- Ledger pages built from per-day in-memory figures the mod never saves may be sparse on clients.

## Live test script

Profile: Living Economy ticked, Settings sync ON, Server-only logic OFF for it. Host a world, join with two players
if possible. After each step read `Documents\Mount and Blade II Bannerlord\Configs\ModLogs\ModderLords.Compat-server.log`
and `-client.log`.

1. **Start-up.** Both logs: `Living Economy layer: Living Economy 1.4.6 loaded (server|client); 7 component(s)` and
   each component `on`. Client: `server-only on: 28 simulation handler(s)...`, `actions on: 14 ...`. Server:
   `owner-scope on`, `player-checks on: lord wealth realism leaves every player's gold alone; ...`.
2. **Join.** Server: `Living Economy layer: state mirror sent 10 book(s) to a joining client`. Open the ledger (M) on
   the client: numbers present.
3. **Let two days pass.** The 30 s verification line: client `simulation handlers skipped` climbing, server
   `owner-scoped ticks` above 0 once a player owns a fief, `state mirror: 10 book(s) sent`.
4. **Invest in a town you own** (e.g. Danustica, `town_ES1`): Living Economy > Invest 10,000. The mod's green
   "Invested 10000g in Danustica: +... prosperity" line appears (this action has no "sent" note). Gold goes down once, on every screen. Client log:
   `sent TryExecuteTownInvestment (town_ES1, 10000) to the server`.
5. **Change tax policy** in the same town: yellow "Sent to the server; its answer follows." then green "tax policy
   changed". Reopen the menu: the new policy shows as current (mirror applied).
6. **Castle treasury and training** (a castle you own): contribute 5,000; start training; wait for it to finish:
   the troops in YOUR party gain the XP.
7. **Second player:** cannot change your town's policy (the mod's own "only in towns owned by your clan" line), and
   their gold is never touched by your town's AI.
8. **Wealth realism:** with it on (host's `better_economy_user.cfg`), a rich player keeps their gold over a week.
9. **Barter a trade treaty** as a kingdom ruler: server log `trade treaty ... ` or the mod's "Trade agreement signed".
10. **Hire recruits** in a town and note the answer to the recruitment question above.
