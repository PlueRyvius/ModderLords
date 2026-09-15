# "The player" on a Coop server: spike

Question: single-player mods ask `Hero.MainHero`, `Clan.PlayerClan` and `MobileParty/Campaign/PartyBase.MainParty` who
"the player" is. On a Coop server those answer for the host, so code like Improved Garrisons' "is this castle mine?"
treats every player's castle as an NPC's. Can one general mechanism fix that, and which one?

Measured on 2026-09-14 from the installed mods' IL (read only) and Coop 0.1.5's `GameInterface.dll` (read for facts).

## 1. How mods use the player getters

Each call to one of the getters was classified by what the next few IL instructions do with the value, and by who runs
the method (reachability from the mod's entry points, as the authority classifier finds them):

- **compare-direct:** compared straight away (`owner == Hero.MainHero`, `clan == Clan.PlayerClan`).
- **compare-chained:** a property first, then compared (`x.OwnerClan.Equals(Hero.MainHero.Clan)`).
- **null check:** `MainHero != null` and similar.
- **stored:** put in a local or field and used later, usually for position/distance.
- **acts on / reads data:** passed to a call, or its data read (gold, culture, party, position).

Where it runs: **server** = reached from simulation or load-time handlers (server-only under gating); **model** = game
models, which every peer runs, the server included; **player** = menu/dialog/UI code on the clicking player's client
(correct as is); unreached counts are left out.

| Runs on | compare-direct | compare-chained | null check | stored | acts on | reads data | other |
|---|---|---|---|---|---|---|---|
| server | 49 | 20 | 53 | 54 | 19 | 16 | 5 |
| model | 38 | 15 | 18 | 11 | 2 | 7 | 0 |
| player | 4 | 6 | 49 | 45 | 31 | 30 | 5 |

Mods: TAOM, ImprovedGarrisons, Europe1100, RTSCamera (+CommandSystem), MyLittleWarband, ModularSmithing2, Heal on Kill
(0 uses), CoopModPatch (0). KingdomPlus is not analysable. Coop itself and TAOM.CoopCompat are excluded.

Per mod, server + model only:

| Mod | compare-direct | compare-chained | stored | acts on / reads | null check |
|---|---|---|---|---|---|
| ImprovedGarrisons | 18 | 20 | 4 | 12 | 4 |
| TAOM | 35 | 4 | 58 | 15 | 56 |
| Europe1100 | 33 | 10 | 3 | 9 | 6 |
| RTSCamera | 1 | 1 | 0 | 8 | 4 |

Spot checks against the decompiled code:
- IG compare-direct `GarrisonRecruitmentLogic.RecruitPrisonerToGarrison`: `settlement.Owner == Hero.MainHero` —
  an ownership check. IG's key gate `GetTownSettings` is chained: `OwnerClan.Equals(Hero.MainHero.Clan)`.
- TAOM stored `SupplyCaravanService.TickPositions` / `DistanceToPlayer`: `MainParty` into a local, then its position
  for distance to a caravan — "near the player", not "owned by the player".

**Reading the numbers:**
- Leaving null checks aside (the host hero exists, so they pass on the server), server code has 163 meaningful uses:
  **69 comparisons (42%)**, 54 stored (33%), 35 act-on/reads (21%). Game models are **58% comparisons** (53 of 91).
- **ImprovedGarrisons is comparison-shaped:** 38 of its 58 server + model uses are comparisons, 20 of them chained.
- **TAOM is not:** most of its server uses are "where is the player / do it for the player" (stored, acts on). A
  comparison rewrite helps it only a little.

Caveats: the classifier looks four instructions ahead, so unusual shapes land in "other" or "stored"; patch roots
(which inherit their target's trigger) are not in the reachability; the counts are uses, not distinct behaviours.

## 2. What Coop already has

Facts from `GameInterface.dll` (read, not copied):
- `IsPlayerHero(Hero)`, `IsPlayerClan(Clan)`, `IsPlayerParty(MobileParty)` extension methods answer "is this any
  connected player's?" from Coop's player registry (`PlayerManager.TryGetControlledObjectInfo`).
- `MainHeroComparisonTranspiler.ReplaceMainHeroComparisonsWithIsPlayerHero`: a Harmony transpiler that replaces
  `Hero.MainHero` immediately followed by `ceq`/`beq`/`bne` with a call to `IsPlayerHero`, and logs a warning for any
  other shape. It only handles **Hero, direct** comparisons. No call site was found inside `GameInterface.dll` itself.
- `MainHeroSubstitutionScope`: swaps `Game.PlayerTroop`, `Campaign.MainParty`, `PlayerDefaultFaction` and the
  thread-static resolved main hero for one player, and restores them on dispose — the "act as this player" scope.

So Coop's authors chose the same two tools this spike considered: rewrite comparisons to "any player", and swap in one
player for an action. They apply them to vanilla code only.

## 3. Verdict per approach (from the discussion in chat)

| Approach | Covers here | Decision |
|---|---|---|
| **B. Rewrite comparisons** to "belongs to any player", in mod methods the analysis says the server runs | 69 server + 53 model uses; most of IG | **Build.** Needs our own transpiler (no Coop code) covering Hero, Clan and Party, direct **and** chained through `.Clan` / `.Owner` / `.LeaderHero` / `.OwnerClan`. Coop's shape alone would miss IG's key check. |
| **A. Context from the event argument** (swap in the owner of the settlement/party a handler receives) | Per-settlement and per-party ticks that act on "the player" | **Build after B**, only for handlers whose event carries a settlement, party or hero. |
| Relay (swap in the clicking player) | "Do it to the player" from a player's click | Already planned (relay A3). |
| **C. Run global ticks once per player** | — | **No.** NPC work would run once per player. |
| **D. Getters follow a "current subject"** | — | **No.** Nothing reliably knows the subject. |

**Not solvable generally:** TAOM-style "where is the player" logic in global ticks (stored/acts on with no subject).
The report should flag those as needing per-mod handling instead of promising a fix.

**Chained comparisons with `MapFaction` / `Kingdom`** ("is this the player's kingdom?") mean something different when
several players can be in different kingdoms. Rewriting them to "any player's kingdom" is wrong for enemies. They stay
report-only.

## 4. Proposed next steps

1. **Classifier:** split `HostIsOnlyPlayer` into `PlayerComparison` (direct/chained, rewritable), `PlayerPosition`
   (stored → position/distance) and `ActsOnPlayer`, with counts in the report. Reuses this spike's IL reading.
2. **Recipe schema v3 + module:** `PlayerComparisons[]` per mod (method ids the server or models run). The client
   module stays untouched; the server module installs our transpiler on them. Answer "is this a player's?" from Coop's
   player registry through its public extension methods if they stay public, else our own lookup via `IPlayerManager`.
3. **Live test on IG:** a player-owned castle's garrison uses that castle's settings on the server (e.g. "Maximum
   number of troops to recruit" set on the server side is respected), and NPC castles are unchanged.
4. Only then: the settings upload and button relays, which are useful for IG once its castle checks work.
