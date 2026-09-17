# Overnight upstream alignment review

Reviewed upstream `0f1971ef93ca2d99cdbd6b6c1fc98fa9d9bf0762`, including PRs 62, 65, 67, 69, 71, 72 and 73, against the operation compatibility plan and local implementation. The upstream checkout is separate at `../_modderlords-upstream-review-20260915`; no merge or remote changes were made. This is a source review, not a multiplayer validation run. Our working base is still `a82889a` (0.9.4).

## Conclusion

The work aligns with the goal of discovering simulation, player interaction and replication boundaries from code. It does **not** align with the agreed activation model. Treat its analysis and fixture tests as reusable inputs; reconcile the runtime before integrating it. The two implementations must not independently patch the same installation.

## Findings

1. **High: heuristic analysis directly authorizes transformations and changes legacy semantics.** `RecipeSet.Build` now replaces whole-behavior legacy recipes with `FromReport` whenever analysis succeeds (lines 95–96). `FromReport` emits handler gates, postfix removals, all discovered player comparisons and relay methods (lines 135–167). These recipes carry names rather than tested contract versions and required mod/provider fingerprints. The plan explicitly preserves schema-v1 behavior and allows only shipped tested contracts to authorize transformations. Keep generated results diagnostic until a matching compiled contract exists; retain legacy selections unchanged.

2. **High: concrete overlap with installed Improved Garrisons coverage.** PR 73 generates relays for `MobileGarrisonSettings.OrderMobileGarrisonToPatrol` and `RecruitmentSettings.SetRecruitmentThreshold`. The inspected installed `CoopModPatch.Adapters.ImprovedGarrisons.IGPatcher.PatchAll` already installs prefixes on both (local study IGPatcher.cs lines 174 and 186). Upstream `RelayGates.Apply` installs its own prefix/finalizer without checking that provider or actual Harmony ownership (lines 32–51). Depending on patch ordering and original suppression, this can create duplicate requests or competing authority behavior. Our external coverage contract must withhold overlapping new work; source analysis of Coop alone does not detect this provider.

3. **High: a role getter is treated as a valid guard without branch proof.** `AuthorityScan.Walker` sets `AuthorityCheck` whenever a recognized getter name occurs at call depth zero or one (line 536); `Decide` reports `AlreadyHandled` (line 366). A method that merely logs `IsServer`, or mutates outside the guarded branch, therefore qualifies. It does not even require the getter's declaring type to be the recognized authority provider. Use control-flow evidence for each mutation, keeping transitive and unresolved paths separate.

4. **High: the relay does not establish command/result/state semantics.** `RelayGates.RelayPrefix` always lets the client execute the original mutation after queuing a request (line 84). `ClientSettingsHandler.HandleRelayResult` only logs the result (lines 103–107). Server invocation has no per-operation semantic bounds or deduplication receipt; the request sequence is echoed but never used to suppress repeats. `RelayCoalescer` applies latest-value behavior to every action, although that policy is valid for setters and can drop distinct increment/purchase actions. There is no operation-specific snapshot or rejection reconciliation. PR 73 itself acknowledges that the server execution path is untested and other players' mod state does not synchronize. Reuse transport/codec ideas under compiled operation policies, with pending UI, receipts, bounded validation and snapshot reconciliation.

5. **High: globally swapping the player is not a proven generalized actor binding.** `ServerRelay.PlayerScope` changes `Game.PlayerTroop`, campaign MainParty, PlayerDefaultFaction and Coop's resolved hero field (lines 139–181). Missing setters are silently skipped. If a setter throws partway through construction, the scope is never constructed, so Dispose cannot restore earlier changes; restoring values can also throw. Synchronous downstream callbacks observe the temporary globals, and deferred work runs after restoration. This requires narrow, tested operation contracts; identifying an owned argument is insufficient proof of safe context substitution.

6. **High: unresolved ownership becomes non-player ownership.** `ServerPlayerChecks` returns false on registry/helper exceptions (lines 42, 53 and 60). For an AI-only resource condition this can award a player's clan as AI. Rewriting all discovered comparisons in simulation/session/query code also assumes they mean “any player,” even when they may mean the particular actor. The resource-adder contract instead resolves all registered players' live clans, including disconnected entries, and suspends the operation on uncertainty.

7. **High: mutable/late recipes conflict with frozen session activation.** `ClientSettingsHandler` requests recipes again at CampaignReady and immediately applies incoming recipes (lines 48–61). `ServerSettingsHandler` reads the current shared recipes file for each requesting client. `LaunchSession.WriteRecipes` overwrites that shared module file (line 447); it is not a session-specific snapshot. There is no plan digest admission check or refusal when required gates arrive after callbacks. The preview itself is guarded by `applySideEffects`, so this is not a claim that every preview writes recipes. The risk is shared recipe replacement across launches/clients and acceptance of late application.

## Work worth retaining

- Read-only `System.Reflection.Metadata`/PEReader analysis; no mod assembly execution is needed for this portion. Mono.Cecil is not the only viable metadata reader.
- Discovery of event/delegate roots, generated state-machine bodies, virtual dispatch, Harmony metadata and UI-registering callbacks.
- Coop gate and AutoSync registration discovery, with its source hash, as evidence. Registration still is not proof of runtime transport coverage.
- Handler splitting that aims to preserve client menus/dialogs, and tests using original fixtures and real Harmony.
- Authority detail UI and CLI presentation, adapted to the separate loading/authority/interaction/replication/verification dimensions.
- Unrelated startup, map, diagnostics and bundled-compat fixes. The baseline attempted in this task used the older local base, so its timeout must not be attributed to current upstream.

## Integration direction

1. Preserve both checkouts and reconcile against the pinned upstream commit. Do not overlay the two dirty trees or retain two automatic patch planners.
2. Keep upstream launcher/map fixes and diagnostic discovery/tests. Consolidate their evidence into the production analysis report rather than maintaining competing authority verdicts.
3. Restore the boundary between legacy configuration and validated contracts. Put unvalidated generated gates, comparison rewrites and generic relays behind diagnostic output; a checkbox enabling old server-only behavior is not a tested operation contract.
4. Recognize TAOM.CoopCompat and CoopModPatch first; reject any new contract whose actual targets overlap their patches.
5. Use compiled actor/permission/transport policy, command receipts and revisioned snapshots. Start with the original fixture and the narrow resource-adder operation, not generic IG relays already served by a provider.
6. Freeze session-specific plans before callbacks, attest required fingerprints, and refuse failed prerequisites. Re-run bounded integration only after the newer startup baseline is established.

The local operation implementation also remains unfinished: its resource-adder validation flags are false, native acceptance is pending, and full configuration attestation needs completion. This review does not endorse it as already production-ready; it identifies which architecture should govern reconciliation.
