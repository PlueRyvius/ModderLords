# What TAOM's own co-op compat package tells us

Source: `TAOM-Coop-v0.3.17-CompatOnly-CLIENT-PLAYTEST` (`TAOM.CoopCompat.dll`, ~120 components), decompiled
2026-09-12 **for reference only**. It is not licensed for reuse and it breaks other things in our launcher, so nothing
here is copied. Each entry records a *bug they found* and the *shape* of their fix, so we can implement our own.

**It applies to our install exactly.** Their package pins file hashes, and every pin matches what we run:
Coop v0.1.5 (`Common`, `Coop.Core`, `Coop.Steam`, `GameInterface`, `Missions`), `TAOM.dll` 2.0.27 and
`TAOM.Dependencies.dll`. Caveat from their own `PACKAGE.json`: almost every fix is marked `runtimeProven: false`.
Treat each as a well-evidenced lead, not a proven cure.

## 1. TAOM does not know co-op is running — fix first

TAOM decides "is co-op on?" through `TAOM.Dependencies.Foundation.CoopPresence`, which matches active module ids
against a compiled list: `BannerlordTogether`, `BattleLinkMPClient`, `Coop`. **The Coop workshop item's id is
`CoopNightly`**, so the answer is always *no*. Both our logs from 2026-09-12 say so:

```
[CoopInterop] no co-op module detected at UI registration — registering all TAOM UI
```

Consequences, from TAOM's own source:

- `CoopSessionProvider` returns *no session* whenever `CoopPresence.IsActive` is false. So on a **client** TAOM
  reports `IsAuthority = true`, `IsCoopClient = false`, `ShouldDeferToHost = false` — every peer believes it owns the
  campaign.
- ~35 TAOM features consult that provider: War of the Ring and its momentum, diplomacy (war, peace, alliance, kingdom
  decisions, fief granting), culture conversion, siege defence, castle recruitment, elite emissary, the whole
  enlistment system, field commission, messengers, player possession, race age, time acceleration.
- Co-op-only UI filtering never runs; `SaveShield` uses the wrong co-op branch.

Their fix forces `CoopPresence.IsActive` to true with a Harmony prefix. **We do not need a patch:** TAOM reads
`coop-modules.txt` from the `TAOM.Dependencies` module folder (the folder above `bin`), with an INI-style format —
lines under `[modules]` are extra module ids, `#` comments. A file containing

```
[modules]
CoopNightly
```

is TAOM's own supported seam. It must be present in **both** folders TAOM actually loads from: the game's
`Modules\TAOM.Dependencies` (client) and the server overlay's `TAOM.Dependencies` (where `diag.log` and
`patchshield-disabled.flag` already live). Verify with the log line changing to `co-op active — registering N UI
extension(s)` and `CoopPresence ... co-op module(s) ACTIVE: CoopNightly` in `diag.log`.

This is probably upstream of several symptoms below: a client that thinks it is authoritative will run simulation
that the server also runs.

## 2. Generic Coop bugs — any mod, often vanilla too

Candidates for ModderLords.Compat. Grouped by area; priority marked **H/M/L** for our current TAOM demo.

### Encounters and menus
- **H `deterministic-battle-scene`** — Coop's field-battle initializer filters candidate scenes, then makes a final
  `GetRandomElement` pick *locally* on each peer. Server and client can load **different battle scenes**. Their fix
  swaps that pick for one seeded by the MapEvent's shared `RandomTerrainSeed`. **Re-check our two "crashing" scenes
  (`battle_terrain_a`, `battle_terrain_020`) against this** — the client may not be loading the scene the server chose.
- **H late MapEvent family** (our Surrender bug is one instance): `encounter-map-event-recovery` (Coop's battle wait
  returns a null MapEvent; bandit surrender dereferences it), `null-map-event-encounter-end-guard` (`PlayerEncounter.DoEnd`
  on a client with a destroyed MapEvent), `encounter-attack-condition-guard` (stale Attack option throws in its
  condition).
- **M `settlement-leave-routing`** — several vanilla Leave consequences only end the *local* encounter; Coop never
  hears, so the server keeps the party inside the settlement.
- **M `raid-post-battle-continuation`** — after winning a village-defence battle during a raid, native `DoEnd` crosses
  Coop's asynchronous settlement reset and the raid does not resume.
- **M `stale-settlement-menu-repair`** — a synchronized settlement menu id survives to `MapState.OnLoadingFinished`
  with no live settlement context.
- **M `settlement-walk-around-context-repair`** — town/castle/village walk-around entered with no `LocationEncounter`.
- **L `settlement-entry-retry-throttle`**, **`settlement-entry-distance-repair`** — rejected entry requests retried
  every tick; small server/client position drift makes the server's distance validator reject a legitimate entry.

### Battles and missions (client crashes)
- **M `siege-destructible-state-guard`** — client crash on an out-of-range destructible state transition.
- **M `control-mode-view-removal-guard`**, **`tournament-agent-removal-guard`** — NullReferenceException in
  `OnAgentRemoved` after the mission screen or participant lookup is gone.
- **M `formation-simulation-cache-guard`** — a stale formation simulation cache carried into the next mission.
- **L `puppet-initial-wield`** — remote puppets spawn empty-handed while carrying weapons.
- **L `siege-deployment-selection-guard`**, **`post-battle-speed-repair`** (main-party speed inputs not rebuilt after
  a mission), **`battle-spawn-reliability`** (spawn batch replay/ack around deployment).

### Campaign state and replication
- **H `raid-loot-isolation`** — joined clients simulate raid loot locally as well as receiving the server's. Plausible
  cause of their testers' unexplained ~4,500 gold from ten looters.
- **M `exspouses-guard`** — Coop materialises heroes without their constructor; a null `Hero.ExSpouses` throws in the
  encyclopedia and wedges the screen system.
- **M `donate-prisoner-garrison-guard`** — the prisoner-donation screen creates a garrison party on a client.
- **M `join-baseline-repair` / `join-baseline-reconciliation`** — live parties missing from Coop's object manager
  when the join baseline is applied.
- **M `army-registration-repair`** — army id collisions in Coop's register/collect pass.
- **M kingdom decisions** — `kingdom-decision-hero-clan-repair` (voter eligibility reads `Player.ClanId`, often unset;
  fall back to hero → clan), `nested-kingdom-decision-deferral` (a decision raised inside an election),
  `call-to-war-outcome-authority`.
- **L null guards on Coop handlers**: `coop-object-manager-null-guard`, `caravan-initargs-null-guard`,
  `ghost-reference-sweep` (dangling party references), `invalid-path-target-guard` (pathfinder assert spam every tick),
  `general-navigation-recovery` (AI parties stalled on the map).

### Network and performance (only if we see the symptom)
`catch-up-drain-guard`, `campaign-world-coalescer-window`, `compressed-hero-join-transfer`, `stale-mission-relay-guard`,
`battle-receiver-spike-hysteresis`, `battle-uplink-guard`, `voice-bank-cache` (voice bank decoded every battle).
Log-spam coalescers: `equipment-diagnostic-coalescer`, `hero-home-settlement-diagnostic-coalescer`.

## 3. Headless dedicated-server bugs — Layer 0 territory

- **M `recruitment-volunteer-null-guard`** — vanilla volunteer refresh throws NullReferenceException on a DS.
- **M `facegen`** — no FaceGen provider on a DS; TAOM's 15-race table must be present and in order before a save loads.
- **M `monster-registration`** — non-human race monsters are not registered on a headless host.
- **L `basic-character-skill-load-guard`** — incomplete character skill rows during load.
- **L `wanderer-introduction-null-template-guard`** — custom wanderers with no vanilla template crash the intro dialog.
- Already covered by us: `server-warning-dump-suppression` (we suppress warning dumps, keep real crash dumps).

## 4. TAOM features that need a server-authority protocol

Not generic; each would need its own sync. Listed so we know the shape of the work:
career system, special resources, elite emissary purchases, War of the Ring momentum relay, full-war diplomacy
reconciliation, starting gold, server-owned TAOM gameplay settings (overlaps our MCM settings sync — they make TAOM's
settings *getters* return server values), battle size authority, transferred-save troop migration (retired TAOM troop
ids in old saves: roster, wage, XP, training), Edoras/Rivendell pathing repairs, tournament brackets.

- **H `troop-weight-policy`** — they force TAOM's TroopWeight **off on every peer** (raw roster counts, no size
  penalty, no upgrade shed). It mutates rosters from each peer's own simulation. **Our server is running it:**
  `[TroopWeight][diag] Shed 1 bodies from 'Thûrzog's Party' (weighted 203 > limit 202)`. Re-evaluate after §1, since
  some of its desync may come from clients believing they are authoritative.
- **L `field-camp-coop-suppression`** — TAOM's Make Camp is hidden in co-op because it is not synchronized.

## 5. Ignore

Their packaging and diagnostics: `server-boot-fault-blocker` (checks their own wrapper's handshake),
`server-log-sink`, `legacy-module-conflict`, `settings-fingerprint`, and the probes (`*-probe`, `*-telemetry`,
`siege-formation-snapshot`, `chat-ribbon-placement`, `item-grant-command`, `troop-grant-command`).

## Suggested order

1. `coop-modules.txt` (§1) — data only, then re-test Surrender, raids and battles, since authority changes everything.
2. Battle-scene determinism — compare the scene each side logs before chasing the two crashing scenes further.
3. TroopWeight and raid loot — both mutate rosters/gold per peer.
4. Late-MapEvent family and settlement leave — generalises the Surrender gate.
5. Client crash guards as they show up in testing.
