# TAOM co-op layer — plan

## Context

Earlier compat work tried general fixes for all mods, and TAOM (an overhaul) never fitted that shape. The maintainer has chosen a
**TAOM-specific interface layer** inside ModderLords. Goal: every TAOM feature works in co-op. First must-have: no
crashes/blocks getting into the game. The maintainer does not play TAOM, so the work is driven **from decompiled code**; they run
short checkpoint tests.

Current state (2026-09-22, v1.0.8, TAOM 2.0.27, Coop 0.1.5): with TAOM.CoopCompat unticked the server starts, but on
join **character creation's culture list is blank**, so no one can get in.

Resources:
- Decompiles (not committed; regenerate with `ilspycmd -p -o <dir> <dll>` from the installed game Modules) of TAOM (2,147 files with `TAOM.pdb`, TAOM.Dependencies, TAOM.CoopCompat,
  CoopCompat.Bootstrap). TAOM is organised as ~100 `TAOM.Features.*` namespaces plus its own `CoopInterop` feature.
- **TAOM.CoopCompat 0.3.17**: the TAOM team's own co-op patch — ~100 components, 50 relay messages. Used as a map
  and design reference. It installs by overwriting files (incl. a rebuilt `DedicatedServer.Core.dll`), which the maintainer won't
  do, so we build our own.

## Decisions (made / recommended)

1. **Build our own (option B)**, using CoopCompat as reference. Where their fix is the right one we follow it closely
   and credit it in a comment. Caveat: "no licence" legally means default copyright, not free reuse — before
   copying a sizeable chunk near-verbatim, flag it; a courtesy ask to the TAOM team clears it. Coop and HexTool code
   stay off-limits (AGENTS.md).
2. **A new game module `ModderLords.TAOM`**, separate from `ModderLords.Compat`, so TAOM code can never run in a
   non-TAOM session. Loaded on the server AND every client (Coop's validator requires id+version match), deployed by
   the launcher the same way `ModderLords.Compat` is (`LaunchSession.SyncModuleId` path), only when TAOM is in the
   profile. Never writes into TAOM's or Coop's folders.
3. **Component per fix, each self-disabling.** Every component pins the TAOM/Coop methods it patches using the
   existing method-surface pinning (`TargetSurfaces`: signature + canonical IL hash, `MethodSurface` runtime reader,
   `src/ModderLords.Analysis/provider-contracts.json`). Pin mismatch → that component turns off and logs; the rest run.
4. **Code first, tests at checkpoints.** No open-ended play testing; each batch ends with a short checklist for the maintainer.
5. Experimental compatibility (the general gating) stays OFF for TAOM sessions.

## Phases

### Phase 0 — Plan into the repo
This document. Baseline recorded above: blank culture list at join.

### Phase 1 — Entry blockers (first slice of the map)
- **Blank culture list at join.** Trace from both sides: TAOM `TAOM.Features.CharacterCreation` (+ `.Hooks`,
  `.Models`, `CharacterSelection.Patches`, `HeroRace`) against Coop's join-time character creation (Coop decompiles:
  regenerate with ilspycmd into the scratchpad). Check what CoopCompat does around join (`CompressedHeroJoinTransfer`,
  `JoinBaselineRepair`, `InactiveMainPartyBaseline`, `FaceGen`/`TaomHeadlessFaceGen`) and TAOM's `CoopPresence`
  (`coop-modules.txt` / `CoopNightly` detection — verify our deployment of that file still happens).
- Also from code: first-map-load and start-of-session crash paths (CoopCompat's null guards list: ObjectManager,
  caravan init, recruitment volunteers, wanderer intro, ServerBootFault, starting gold).
- Re-check the stale-looking `LOTRLOME_Armory` "Broken" warning vs `TaomLaunchPolicy` (which requires it).

### Phase 2 — Module skeleton
`src/ModderLords.TAOM` (+ `.Coop` split if Coop references are needed, mirroring `ModderLords.CompatSync` /
`CompatSync.Coop`): SubModule, component registry, surface-pin check at load, per-component on/off log lines,
launcher deployment + version bump rule (bump module version on every client-visible change). Tests in the existing
test projects. First components = Phase 1 fixes.

### Phase 3 — TAOM map (`docs/TAOM-MAP.md`)
For each TAOM feature: what it does, where it runs (server/client/both), what state it keeps (saved? synced?), player
and UI assumptions, predicted co-op failure (crash / desync / not-working-in-coop / fine), and the matching CoopCompat
component if any. Reuse `AuthorityScan` output (`authority --mod TAOM`) as the starting data. Output: a ranked
backlog.

### Phase 4 — Work the backlog in severity order
Crashes → desyncs → not-working features. State that clients never receive is handled by instance-state sync
(Coop's AutoSync seam: `AutoSyncRegistry.AddField`, built once at Coop start-up — registration must happen before
that) or by TAOM-specific relay messages, per feature. One PR per coherent batch.

## Verification
- Unit tests per component (pin check, policy logic); full test suite green before each PR.
- Each batch: the maintainer runs a short checklist (host TAOM world → join → pick culture → enter town → one field battle,
  plus batch-specific items); agent reads launch log, `Coop_server.log`, `Coop_client.log` (shared-read), rgl logs,
  `ModLogs` and dumps. Allow crash dumps.
- Always also smoke a vanilla-map launch after server-side changes (lesson from 1.0.0).
