# TAOM co-op map

The backlog for `ModderLords.TAOM`: every TAOM feature, what co-op does to it, and what our layer has to do. See
[TAOM-LAYER-PLAN.md](TAOM-LAYER-PLAN.md) for the why and the phases.

**Sources.** TAOM v2.0.28 source (MIT, `github.com/haterade22/TAOM`, branch `bannerlord-1.4.5`), its own docs
(`docs/features/coop-interop.md` above all, and the per-feature docs), and TAOM.CoopCompat 0.3.17 (the TAOM team's
own co-op patch, used as a map; not licensed for reuse). Built from code, not play. Claims marked **[verify]** need a
live session.

## Checklist (updated 2026-09-23)

Legend: **[x] verified** in a live session · **[b] built**, not yet exercised live · **[ ] open** · **[~] partial** ·
**[u] upstream** (Bannerlord Coop, not ours to fix) · **[s] safe** by design (checked in code, nothing to do).

Source of the open items: a scan of TAOM 2.0.28 for code that changes server-owned state (troops, items, gold,
relations, influence, heroes, wars, settlements, parties, skills, new game objects) and whether a co-op gate or our
layer covers it (`scratchpad/audit_mutations.py` in the 2026-09-22 session; rerun it on a new TAOM version).

### Getting in and staying up
- [x] Install integrity refused at launch (hollowed TAOM, #109)
- [x] Co-op detection: TAOM sees `CoopNightly` on both sides
- [x] Join hand-off: culture gold, career, resource seed applied on the server (join-grant)
- [x] Settings parity: all four TAOM MCM pages reach clients; live edits persist per profile
- [x] Client crash in "join encounter, help attackers/defenders" (Coop reads `EncounteredBattle` too early; guarded, #119)
- [b] Map-mod version mismatch warns at launch, still loads (#120)

### Player actions TAOM only allows the host (relays)
- [x] Field Camp: establish / fortify / foraging / break, per-player hourly tick (#109, #110, #114, #117)
- [b] Elite emissary purchase (#112)
- [b] Siege defence reward (#112); the prompt itself runs on every peer [s]
- [~] Messengers: not relayable (arrival fakes an encounter Coop validates); player is told (#112)
- [b] **Refuge**: founding (warden pick), upgrade and dismantle relayed; server runs them for the sender, militia runs
      as the owner, refuge ticks are server-only; each player sees and counts only their own refuges. Garrison and
      stash use TAOM's own screens on the refuge party (Coop's party-screen sync; stash unverified).
- [b] **Supply Lines**: order placement relayed; server moves, delivers and cancels each player's caravans for that
      player (book narrowed per owner); clients show only their own routes. TAOM's messages shown while the server acts
      for a player (delivered, lost, refuge raised, desertion) are forwarded to that player (notices component).
- [b] TAOM's own party components (refuge, supply caravan) sent to clients, which Coop does not do for non-vanilla
      components; owner and banner answer from the party's clan instead of Hero.MainHero.
- [ ] Career quests: created client-side; only one quest exists in TAOM (captain_of_osgiliath_t2). Plan: client
      tracks, server receives completion and rewards.
- [~] Enlistment: not possible from this layer. Enlisting parks the player's party (IsActive/IsVisible false, pinned
      to the lord every frame) and the player fights inside the lord's formation; in Coop the player's party state and
      battle spawning are Coop's own. TAOM refused silently on clients (oath sworn, nothing happens); the offer is now
      greyed out with the reason on co-op clients (enlistment-guard). Would need Coop support for parked players.
- [b] Field Commission: client owns its merit bank; TAOM's kill counting (own party only, so once per kill), battle
      scoring and offer prompts run on the co-op client; the promotion (hero creation, soldier removed) is relayed
      and completed by the server for that player. Merit is backed up on the server ("heroId|troopId" rows in its idle
      bank) and restored on rejoin (client-state-backup). Live check: does the
      client get MapEventEnded for its battle (merit only banks on a won, eligible battle).
- [b] Player Switcher: only offered on the character-creation face screen (Patch77). Hidden on co-op clients, since
      taking over an existing lord clashes with Coop's player hero. No mid-campaign switch exists.
- [s] Equip Presets / Quick Actions: both apply through vanilla InventoryLogic transfer commands / vanilla sell-all,
      which Coop's inventory sync carries. Presets are backed up to the server's per-hero store (saved with the
      campaign), so they survive a reconnect (client-state-backup).
- [b] Start position: after the server applies a fresh player's join package, the client moves its party to TAOM's
      culture starting settlement (TAOM's own teleport only ran on the local character-creation campaign).
- [s] Other players' hero race: Coop auto-syncs BasicCharacterObject.Race; join-grant sets it on the server.

### Player state the server must own or know
- [b] Special-resource balances: client owns, server stores and saves (#115)
- [b] Upkeep desertion runs on the server (#121)
- [x] Careers: client reports, server stores and applies perks (#116)
- [b] "The player" on the server = connected players: War of the Ring credit, player kingdom, siege watch, execution
      relations (#118). Limit: one representative player for single-answer questions.

- [b] Player-sensitive TAOM models computed on the server (second audit, 2026-09-23): pregnancy (player/spouse rule),
      volunteer recruitment across alignments, prisoner-recruitment morale waiver, player caravan trade limits, naval
      capability. They compared with Hero.MainHero (the idle server hero), so players got AI rules; now computed with
      the player concerned in scope (game thread only; parallel party ticks keep TAOM's plain answer).
- [b] Alignment desertion: ran on every client (local-only roster loss, like #121) and treated players as AI on the
      server. Now server-only, run as the owning player for their parties and fiefs.
- [s] Second audit, confirmed fine: Fief Management (remote town screen; the settlement swap is a local field write,
      and Coop syncs build queues, projects, gold boosts and governors: check live), Castle Recruitment (vanilla
      recruit screen), Wanderer Allegiance (local dialog refusal), army targeting / lord templates / return to army /
      execution / uncapturable heroes / Nazgul family / race age (engine-path patches that run where the engine runs).
- [ ] Other players' career perks on your screen: clients hold only their own career record, so models a client
      computes for another player's hero (e.g. that player's party size in a tooltip) use no perks. Display only.

- [b] Fief elections (FiefGranting): TAOM swaps the vote for its own TaomSettlementClaimantDecision, which Coop's
      exact-type decision converter refused ("not supported"), so players never got fief votes. Now sent as the
      vanilla base type (fief-vote-sync). The player-clan penalty exemption is evaluated per player's clan.

- [b] 1.1.1 fixes, found by checking what Coop does NOT raise on clients: a client never sees a battle end (Coop
      finalizes on the server only), so (a) Field Commission battles are now scored by a client-side watch of the
      party's own battle, and (b) special-resource earnings for battles, raids, hideouts and tournaments now run on the
      server per player and are pushed to that player (TAOM's prisoner-taken earning is still not covered).
      Refuge: walking into another player's refuge no longer opens the refuge menu; "Store goods" is closed in co-op
      (Coop's inventory Done reads a trader a stash does not have), and refuge garrisons do not eat on a co-op server.

### TAOM state clients never hear about (state mirror)
- [x] War of the Ring phase and Momentum (#113; bar moves on the client)
- [b] Culture conversion (pending conversions): mirrored from the server
- [ ] Siege defence active events: host-owned timeline; clients prune nothing (TAOM notes it is harmless)
- [b] Refuge book and Supply order book (mirror now carries TAOM record dictionaries; refuge visuals of removed rows
      are cleared on the client)
- [ ] Enlistment / Field Commission state: after their relays exist
- [s] Hero race map, banner injection, Nazgul family, race ages: deterministic on every peer from shipped data

### Battles (reviewed 2026-09-23, nothing changed yet)
How Coop runs a battle (its `Missions` assembly): the battle runs on the CLIENTS, never on the server. One client is
the battle host (`BattleSession.IsLocalHost`, migrated if it leaves); every agent has one controlling client
(`agentRegistry.TryTransferAuthority`), the host owning the AI; the result is committed to the server afterwards
(`BattleResultCommitter`). Consequences for TAOM's ~25 mission behaviours:
- [ ] **Authority-gated logic never runs.** TAOM's `IsAuthority` is false on every client and the server has no
      mission, so Field Commission merit (`FieldCommissionMissionLogic`) is never earned in co-op. Fix belongs with
      the Field Commission item: count on the client, report to the server.
- [ ] **Ungated agent logic runs on every client.** Wargs, spiders, elephants, advanced combat, smart cavalry AI,
      siege dismount, mount despawn, banner bearers, dread aura and career perk effects all change agents (blows,
      deaths, morale, movement, dismounts) on every client that runs the battle, including agents another client
      controls. Expected symptoms: doubled or fighting effects (double trample/morale hits, AI tugged between
      clients), agents dying on one screen only. Proposed fix: one "agent authority" gate that lets TAOM act only on
      agents this client controls (and battle-wide effects only on the battle host).
      [b] Built as measure-then-gate (battle-gate component): during a Coop battle every TAOM battle-behaviour action
      (blow, death, morale, teleport, speed/scripted movement) on an agent another machine controls is logged with
      the TAOM class; classes listed in Configs\ModLogs\taom-battle-gate-client.txt (or *) are skipped for such
      agents. No file = measure only. Next: one two-player battle, read the log, list the offenders.
- [s] Presentation only (no agent changes found): mixed formations UI, battle action bar view, diagnostics, shader
      precompile, war ram and mumakil behaviours (they delegate; re-check if a live battle shows trouble).
- [u] Picking up fired arrows: Coop rejects items it has no shared identity for (`uncorrelated-runtime-identity`)

### Upstream and out of scope
- [u] Quests and issues: Coop disables them all (upstream #2642 / #2800); TAOM's own issues (LotrIssues) are
      blocked by the same switch
- [u] Arrow pickup (above); siege ammo (#2879, #2603)
- [s] Time controls: TAOM hides them under co-op
- [s] Troop weight: forced off by the compat database (per-peer model)
- [s] Diplomacy vetoes: classified in TAOM's `CoopVetoClassificationTests`
- [s] Uncapturable heroes, race-age offspring, marriage model: same answer on every peer from shipped data

## What TAOM already does for itself

TAOM is co-op-aware. `TAOM.Features.CoopInterop` answers three separate questions, and every gate in TAOM uses one:

| Signal | Meaning | Our concern |
|---|---|---|
| `ICoopPresenceProvider.IsCoopActive` | a co-op module is enabled (process-constant) | Its id list knows `Coop`, **not `CoopNightly`**. ModderLords' compat-db `EnsureLines` adds `CoopNightly` to `TAOM.Dependencies\coop-modules.txt`; a TAOM reinstall wipes that line again. |
| `ICoopSessionProvider` (`IsAuthority`, `ShouldDeferToHost`, `MayWriteSaveBackedState`) | a session is live and this peer owns the world | Reflection into Coop; fails open to singleplayer. |
| `IDedicatedServerProvider.IsDedicatedServer` | this process has no real player | True only when TAOM's assembly loaded from a path containing `Win64_Shipping_Server`. |

About a dozen behaviours are already host-only (culture conversion, race age, War of the Ring, momentum, messengers,
siege defence, castle recruitment, field commission, enlistment, fief granting), every skip-original prefix carries
a co-op disposition (`CoopVetoClassificationTests`), and PatchShield is off under co-op.

**The one thing TAOM cannot do:** send anything across the connection. It refuses to take a compile-time dependency
on any co-op mod, so where a client needs the server to act, TAOM *declines*; where the server changes TAOM state,
clients *never hear about it*. ModderLords already has that channel (`ModderLords.CompatSync.Coop`: relays, settings
sync, recipe delivery). **That is the core of our layer.**

## Backlog, in severity order

### P0: getting in

| # | Item | What happens | Our fix |
|---|---|---|---|
| 0.1 | **TAOM install integrity** | 2026-09-22: a hollowed TAOM install (ModuleData, GUI, Prefabs, server bin all empty) gave a blank culture list at join; TAOM logs each missing file and carries on. Cause of the hollowing unknown. | Launcher preflight: TAOM, TAOM.Dependencies, TAOM_Map, Armory must each carry their known core files (e.g. `ModuleData\cultures`/character-creation data, `bin\Win64_Shipping_Server`). Refuse with a clear message. |
| 0.2 | **Co-op detection** | Without `CoopNightly` in `coop-modules.txt`, TAOM thinks co-op is off: PatchShield runs (frame-rate collapse), SaveShield swallows save faults, every `IsAuthority` fails open → every client runs the host-only behaviours. | Keep `EnsureLines`, and have the TAOM module check at load that `CoopPresence.IsActive` is true and log loudly if not. |
| 0.3 | **Server loads TAOM from `Win64_Shipping_Server`** | TAOM's `IsDedicatedServer` reads its own DLL path. If our overlay serves it from a client bin, the headless server believes it has a player: special resources, WotR credit etc. go to the idle world-gen hero. | Verify the path in the server log; assert in the module. [verify] |
| 0.4 | **LOTRLOME_Armory on the server** | TAOM's doc: build 117131 (the DS engine) throws in `MergeElements` on the Armory's `action_sets.xml`. Our compat-db marks Armory "Broken on the server" while `TaomLaunchPolicy` requires it. | Reconcile: decide how the server gets Armory's items without its animation sets; fix the stale warning. |
| 0.5 | **Join hand-off grants** | Coop discards the joiner's character-creation hero and hands over a server-authored one. TAOM's `PlayerPossession` then re-applies race, culture startup gold, career and special-resource seed — **on the client**. Gold and race are Coop-synced objects: a client write is refused or diverges; the server never gets the grant. | Run the re-grant on the server for the joining player's hero: client sends its captured `PlayerCharacterCreationChoices`, server applies them. CoopCompat: `CompressedHeroJoinTransfer`, `CareerAuthority` bootstrap claim. |
| 0.6 | **Client-side object creation** | Coop blocks `StringId` writes on clients, so any `MBObjectBase` created client-side has a null id; `new CareerQuest` on accept is one such path. | Forward career-quest start to the server (see P2 relays). [verify] |

### P1: crashes and hard faults at session start

From CoopCompat's guard list plus TAOM's own open items. Each is a small defensive component; take them in the order
a live session shows them, but they are all code-visible now:

- Coop `ObjectManager` null lookups for TAOM-created objects (CoopCompat `CoopObjectManagerNullGuard`, `GhostReferenceSweep`).
- Caravan initialisation null (`CaravanInitializationNullGuard`), recruitment volunteer null (`RecruitmentVolunteerNullGuard`), wanderer introduction null (`WandererIntroductionNullGuard`), ex-spouses (`ExSpousesGuard`).
- Kingdom decisions added while one is resolving (`NestedKingdomDecisionDeferral`), eligibility (`KingdomDecisionEligibilityRepair`).
- Battle spawn / mission start failures (`BattleSpawnReliability`, `MissionAfterStartFailureProbe`, `ActiveMissionMapEventGuard`, `NullMapEventEncounterEndGuard`).
- Sackless battle scenes: already handled by ModderLords' `BattleScenePick` exclusion.
- Headless FaceGen on the server (`TaomHeadlessFaceGen`) — TAOM races read FaceGen data a server may not have.

### P2: things a client can start but never finish (need a relay)

TAOM declines these on clients. Our layer forwards them to the server, which runs TAOM's own code for that player:

| Feature | Client today | Relay |
|---|---|---|
| Elite emissary | "The emissary only deals with the host" (after browsing) | purchase request → server charges + grants (CoopCompat `EmissaryPurchaseProtocol`) |
| Messengers | send refused | send request → server enqueues and delivers |
| Siege defence | can accept; reward tick host-only | owner-checked accept on the server |
| Special resources | earning suppressed on the server; client balance is local `SyncData` | server-authoritative per-hero ledger (CoopCompat `SpecialResourceAuthority`) |
| Career system | mutations local to each peer | request/snapshot protocol (CoopCompat `CareerAuthority`) |
| Career quests | created client-side | server creates the quest for the player's hero |
| Starting gold | granted client-side at join | server grant (CoopCompat `StartingGoldAuthority`) — merges with 0.5 |

### P3: TAOM state clients never receive

TAOM keeps campaign state in its behaviours' `SyncData`. Clients get it once (from the join save) and never again.
Shown by feature, with the save-backed state:

| Feature | State | Effect on clients |
|---|---|---|
| War of the Ring + Momentum | phase, momentum, events | stale map meter, war/peace UI wrong; client-side vetoes read an old phase |
| Culture conversion | pending conversions | none visible; notables replaced on server only |
| Special resources | per-hero balances | balance shown ≠ server |
| Career system | careers, passives, unspent points | wrong perks applied in client-side models |
| Enlistment / Field commission | enlistment, merit | [verify] per-player state lives where? |
| Refuge, Supply lines, Field camp, Equip presets, Banner injection, Hero race, Quick actions, Messengers | own `SyncData` keys | UI shows join-time state |

**Design idea (to try first):** a generic TAOM `SyncData` mirror. On the server, run a chosen behaviour's `SyncData`
into a data store, serialise, send; on clients, load it back through the same `SyncData`. One mechanism for every
TAOM behaviour, no per-field work, no dependency on Coop's AutoSync type list. Instance-field AutoSync stays the
fallback for anything that must be live rather than periodic.

### P4: per-player context on a dedicated server

On a headless server `Hero.MainHero` is the idle world-gen hero. TAOM features that ask "the player" get that hero:
WotR participation credit (only the MainHero's kingdom counts), siege defence influence, special-resource earning
(TAOM turns it off entirely), career passives in models, Nazgul/possession paths. ModderLords already rewrites
`MainHero` comparisons in server-run mod methods (PR #72, `ServerPlayerChecks`) — extend it to TAOM's player-context
adapters (`IPlayerContextAdapter`) so the server answers per player.

### P5: settings parity

TAOM has 243 MCM settings; 180 affect the simulation (`CoopSettingsRelevance`). Peers do not exchange them; TAOM only
logs a fingerprint per group (`SettingsFingerprint`). ModderLords' MCM settings sync pushes the host's values —
confirm it covers `TaomSettings` (all four classes are `AttributeGlobalSettings`), then compare TAOM's fingerprint
lines on both sides as the check.

### P6: missions

TAOM mission logic (AdvancedCombat, BannerBearers, DreadAura, MountDespawn, SmartCavalryAI, Elephant/Mumakil/Warg/
Spider behaviour trees, WarRam, SiegeDismount, MixedFormations, CompanionTactics) runs on every peer that runs the
mission. How Coop splits a battle between server and clients decides whether these duplicate or diverge — to be read
from Coop's `Missions` assembly before any work. [verify]

### Presentation only — expected fine

FactionMap, MenuLinkColors, SettlementNameplate*, PartyIconScale, MainMenuCustomizer, Encyclopedia, LocalizationOverride,
ShaderPrecompilation, CrashReport/diagnostics features, TimeAcceleration (TAOM already hides it under co-op).

## Feature inventory

Counts from a source scan (behaviours, Harmony patch sites, `SyncData` keys, co-op gates, `MainHero`/`MainParty`
reads). High `MainHero` with no gate is where P4 bites; `SyncData` is where P3 bites.

| Feature | Beh | Patches | SyncData | Gates | MainHero | Bucket |
|---|---|---|---|---|---|---|
| CareerSystem | 4 | 2 | 5 | 0 | 17 | P2, P3, P4 |
| SpecialResources | 1 | 15 | 1 | 1 (DS) | 34 | P2, P3, P4 |
| Enlistment | 10 | 18 | 0 | 29 | 8 | gated; verify per-player state |
| WarOfTheRingMomentum | 1 | 0 | 0 | 7 | 0 | P3, P4 |
| Diplomacy | 3 | 28 | 2 | 10 | 5 | gated; P3 (WotR phase) |
| Refuge | 1 | 6 | 4 | 0 | 34 | P3, P4 |
| SupplyLines | 1 | 3 | 4 | 0 | 34 | P3, P4 |
| FieldCamp | 1 | 3 | 2 | 0 | 29 | P3, P4 |
| Messengers | 1 | 0 | 2 | 2 | 20 | P2 |
| EliteEmissary | 1 | 0 | 0 | 1 | 2 | P2 |
| PlayerPossession | 1 | 0 | 1 | presence | 7 | P0 (0.5) |
| CharacterCreation | 1 | 32 | 0 | 0 | 28 | P0 |
| PlayerSwitcher | 3 | 8 | 0 | 1 | 18 | P4 |
| LotrIssues | 1 | 0 | 0 | 0 | 22 | P4; creates objects |
| CultureConversion | 1 | 0 | 0 | 3 | 0 | gated; P3 |
| Siege | 1 | 3 | 1 | 4 | 5 | P2 |
| FiefGranting | 1 | 3 | 0 | 4 | 1 | gated |
| FieldCommission | 3 | 3 | 0 | 9 | 5 | gated |
| CastleRecruitment | 1 | 6 | 0 | 2 | 1 | gated |
| HeroRace | 1 | 44 | 2 | 0 | 0 | P3; capture guard exists |
| TroopWeight | 1 | 18 | 0 | 0 | 7 | settings (compat-db forces off today) |
| UncapturableHeroes | 0 | 6 | 0 | 0 | 4 | ReviewedSafe |
| QuickActions, EquipPresets, BannerInjection | 1 each | — | 2 each | 0 | — | P3, player-local |

Mission features and presentation features: see P6 and the list above.

## Notes for upstream (not acted on)

Recorded only; we do not contact the TAOM team.

- `CoopPresence.CompiledModuleDefaults` lacks `CoopNightly`, the id Coop's current Workshop build uses, so every
  install needs the text-file edit.
- TAOM's `SubModule.xml` still tags every submodule `DedicatedServerType=none` although the build now mirrors server
  binaries.
