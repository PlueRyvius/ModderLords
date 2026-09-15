# Player-action relay: feasibility spike (A0)

Question: when a player triggers an action the classifier marks `NeedsRelay`, can we run it on the server as that player
so Coop replicates the real effect? Answered from the authority reports of TAOM, ImprovedGarrisons (IG) and
MyLittleWarband (MLW), the decompiled mods, and the decompiled Coop v0.1.5 `GameInterface.dll` (read for facts only).

## 1. What the 52 NeedsRelay roots actually reach

| Mod | Roots | Console command | Menu option | Dialog line | Patch | ViewModel |
|---|---|---|---|---|---|---|
| TAOM | 33 | 19 | 9 | 1 | 2 | 2 |
| IG | 17 | 0 | 4 | 11 | 0 | 2 |
| MLW | 2 | 0 | 1 | 0 | 1 | 0 |

The blocked call that made each root NeedsRelay, with the gate kind `CoopSinks` assigns:

| Reached call | Gate kind | What Coop's prefix really does on a client |
|---|---|---|
| `CampaignCheats.CheckCheatUsage` | Conditional | `IsClient` → return false. Cheats are **deliberately disabled** for players. |
| `GameStateManager.PushState` | Conditional | Allow-list: Kingdom/Character/Party/Inventory/Clan screens **open** on clients. Not a block. |
| `GameMenu.ActivateGameMenu` | Conditional | Only the join-siege menu is intercepted; other menus navigate locally. |
| `LeaveSettlementAction.ApplyForParty` | Conditional | Publishes `PartyLeaveSettlementAttempted`, returns `IsServer`. **Coop already relays.** |
| `DestroyPartyAction.Apply` | Conditional | If destroyer is `MainParty`, publishes `DestroyPartyRequested`. **Coop already relays.** |
| `Hero.SetName` | Conditional | Publishes a name-change message, returns `IsServer`. Coop owns it. |
| `ItemRoster.AddToCounts` | Conditional | Runs **locally** on the client (returns true); only the server publishes roster changes. Client change diverges silently. |
| `MBEquipmentRoster..ctor` | Conditional | Lifetime-registration patch. |
| `Campaign.set_TimeControlMode` | Policy | Original only on server / allowed scope. |
| `GiveGoldAction.ApplyBetweenCharacters` | — | Not in the gate list by method; reached some other way (intercepted/TargetMethods). Needs a check. |
| `Hero.VolunteerTypes` write (MLW) | synced member | Client write is local; server never hears of it. |

**Finding 1: `Conditional` is too coarse to decide relays.** 290 of Coop's 360 blocked methods are Conditional, and every
reached gate above except time control is Conditional. The plan's rule "Conditional → no relay" would drop almost all
52. Conditional actually hides four different behaviours, and only one of them rules out a relay:

- **Publishes** a request or change message (`MessageBroker.Publish` in the prefix): Coop relays it itself. A second
  relay would apply the change twice. → no relay.
- **ClientDeny**: returns false on a client, no publish (cheats). → report only, on purpose.
- **ClientLocal**: returns true on a client, publishes only on the server (`ItemRoster.AddToCounts`). → relay candidate.
- **Allow / navigation**: UI screens and menus (`PushState`, most `ActivateGameMenu`). → not a world change, no relay.

**Finding 2: many roots are false positives.** 17 of TAOM's 19 console commands only reach `CheckCheatUsage`: cheats
are off for clients by Coop's design. 10 roots across TAOM and IG reach only `PushState` / `ActivateGameMenu`: they
open screens or menus, which works on clients. In IG, 4 menu options, 4 dialog consequences and 1 ViewModel action reach
`LeaveSettlementAction`, which Coop already relays. The other 5 IG dialog roots only set `PlayerEncounter.LeaveEncounter`.

## 2. Samples, one per kind

### Menu consequence: IG `MainMenu::<AddGameMenus>b__1_0` ("Improved Garrison" in the keep)
Reads `Settlement.CurrentSettlement`, writes the mod field `garrisonBehavior.CurrentTownForSettings`, opens the IG UI.
It is **navigation**. Real changes happen later in UI actions (`CascadeMenuExtendButtonVM::ExecuteClick` → leave
settlement, which Coop relays). Relaying the menu option would do nothing useful on the server.
**Verdict: no relay.** The mod field write belongs to state sync (C) at most.

### Menu consequence: TAOM Refuge "Dismantle refuge"
`_refuges.NearestDismantlable()` (position of `MobileParty.MainParty` vs mod-owned refuge data) → `_refuges.Dismantle`
(→ `DestroyPartyAction.Apply`) → `CloseMenu()`. Needs only the player's party. Mod data lives on the server, where
refuge behaviours run as ServerOnly. **But** Coop publishes `DestroyPartyRequested` itself if the destroyer is `MainParty`,
so check the destroyer argument before approving. **Verdict: feasible with a `MainParty` swap. Approve only if it
doesn't reach a Publishes gate.** Found/Upgrade (gold) are the same shape, pending the `GiveGoldAction` check.

### Wait-menu consequence: TAOM `EnlistmentWaitMenuOptions::<Register>b__3_7`
Only `ActivateGameMenu` (navigation). **Verdict: no relay.**

### Dialog consequence: IG `Conversation_improvedgarrison_mobilegarrison_fortify_on_consequence`
Reads `PlayerEncounter.EncounteredParty`, opens a `MultiSelectionInquiry`, sets `PlayerEncounter.LeaveEncounter`. The
**real effect is in the inquiry callback** `Inquirydata_FortifyGarrison(List<InquiryElement>)`. The classifier doesn't
treat that callback as an entry point, so the actual world change is currently invisible to it. A relay would need the
encountered party id plus the selected settlement ids. **Verdict: later phase.** Classifier gap: add delegates passed to
`InquiryData` / `MultiSelectionInquiryData` / `TextInquiryData` as PlayerInput roots (kind `InquiryCallback`).

### Patch on `*_on_consequence`: MLW `RecruitPatch2::Prefix` on `game_menu_recruit_volunteers_on_consequence`
Reads `Settlement.CurrentSettlement` and each notable's `VolunteerTypes`, swaps entries for custom troops (writes
`Hero.VolunteerTypes`, a Coop-synced member), fills static `TroopsBehavior.recruits`. Then vanilla opens the recruitment
screen, and Coop relays the actual hire through `RecruitmentVM.OnDone` (Conditional). On a client the swap is local, so
the player sees custom troops but the server hires from its own, unswapped list.
**Verdict: feasible, best phase-1 case.** Needs the player's party in the settlement (swap `MainParty`, so
`Settlement.CurrentSettlement` resolves), running outside `AllowedThread` so `VolunteerTypes` replicates.
**Ordering risk:** the relay must land before the player confirms the hire. It does in practice (the screen is open for
seconds), but the server's result reply should be logged so a lost race shows up.

### ViewModel action: TAOM `SupplyOrderScreenVM::ExecuteConfirm`
Reads VM-only state (row quantities, selected source, escort) and calls
`ISupplyOrderService.TryPlaceOrder(source, goods, troops, escort, out reason, placedFromCamp)`. The VM can't be rebuilt on
the server. The service call has serialisable arguments, but choosing that boundary is per-mod judgement.
**Verdict: report only.** Same for IG `GarrisonUIVM::ExecuteTransfer`.

### Console command: TAOM `TimeControlCheats::RescueTime` and the 18 other cheats
**Verdict: report only.** Coop disables cheats for clients on purpose; don't bypass that.

## 3. Running as a player on the server

- Coop's `ResolvedMainHeroContext.ResolvedMainHero` is an `internal static` **[ThreadStatic]** field. It's reachable by
  reflection, but only takes effect when set on the thread that runs the action (the game thread).
- `BarterPlayerContext` is internal. Write our own `PlayerContextScope`: save and set `Game.PlayerTroop`,
  `Campaign.MainParty`, `Campaign.PlayerDefaultFaction`, the resolved main hero; restore all in `finally`.
- `CallOriginalPolicy.IsOriginalAllowed()` is true on an `AllowedThread` or inside Coop's operation scopes. A relay must
  run **outside** them so Coop's server-side patches publish the changes.
- `MenuCallbackArgs`: none of the approved samples read `args` beyond `optionLeaveType` in conditions. Consequences can
  receive a fresh `MenuCallbackArgs(menuContext: null, text: null)`. Treat any root reading `args.MenuContext` as not relayable.

## 4. Decisions for A1–A3 (changes to the plan)

1. **Split Conditional in `CoopSinks.Classify`** by the prefix's IL: calls `MessageBroker.Publish` → `Publishes`;
   `IsClient` branch returning false with no publish → `ClientDeny`; returns true on client and publishes only on the
   server → `ClientLocal`. Keep `Conditional` for the rest. Relay rule: propose a relay only if no reached gate is
   `Publishes` or `ClientDeny`, and at least one reached effect is `ClientLocal`, `Policy`, `ClientSkip` or a synced-member write.
2. **Classifier false positives first** (before A1, or as its first commit): `GameStateManager.PushState` and
   non-intercepted `ActivateGameMenu` are presentation/navigation, not sinks; `CheckCheatUsage` gets its own
   "cheats are disabled for players" reason instead of NeedsRelay.
3. **Phase-1 relay kinds:** `PatchOnConsequence` and `MenuConsequence`. **Later:** `InquiryCallback` (new root kind),
   `DialogConsequence`. **Never relayed:** `ViewModelAction`, `ConsoleCommand`.
4. **Live-test target for A3:** MLW `RecruitPatch2` (visible: the custom troops offered and hired match on the server).
   Backup: TAOM Refuge dismantle, if it passes rule 1.
5. Open check for A1: how `GiveGoldAction.ApplyBetweenCharacters` became a sink (not a gated method in the catalogue).
