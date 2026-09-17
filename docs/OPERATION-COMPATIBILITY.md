# Operation compatibility implementation

The launcher and CLI share a read-only .NET 10 analyzer. It reports operations and evidence separately from shipped compatibility contracts. An unknown operation cannot generate a runtime patch. ModularSmithing2 is excluded from the launch analysis service.

## Use

In the Mods view, choose **Analyze operations**. The report belongs to the captured profile and selected module copies; changing the preview invalidates row summaries. Expand operation evidence in the details window. The automatic compatibility setting defaults on and preserves existing legacy behavior rules.

CLI examples:

```powershell
dotnet run --project src/ModderLords.Cli -- analyze --profile MyProfile
dotnet run --project src/ModderLords.Cli -- analyze --profile-file profile.json --json
```

`--cache-dir` overrides the graph cache location. Exports contain method identities, hashes, findings and decisions, rather than decompiled source or local installation paths. Graph caches are keyed by assembly content and analysis rules; composed reports include selection order and configuration fingerprints. Engine dependencies retain type metadata; calls into engine code use explicit effect rules or unresolved boundaries.

## Components

- `ModderLords.Analysis`: Cecil metadata and IL discovery, call and registration edges, hierarchy and constant DryIoc registration traversal, conservative effects, direct branch guard evidence, fingerprinted contracts and plan decisions.
- `OperationAnalysisService` and `OperationPreparation`: selected launch inputs and immutable session preparation output. Previewing does not overwrite a staged plan.
- `ModderLords.Operations`: engine-independent command receipts, pending/results, actor validation, snapshot revisions, deferred application and session freeze policies.
- `ModderLords.CompatSync.Coop/Operations`: authenticated peer transport, live actor resolution on the game thread, compiled adapter installation, Harmony overlap checks, cleanup and readiness.
- `ModderLords.OperationFixture`: original opt-in integration module with a private per-player counter. It does not alter campaign objects or replace external adapters.

## Contract status

The captured installation recognizes TAOM careers, elite-emissary purchases, Improved Garrisons management and My Little Warband editing/synchronization. TAOM Make Camp is explicitly suppressed by its provider. Recognition proves matching inspected bytes, not successful runtime patch installation.

The ClansResourceAdder adapter changes only the award callback gate and AI-clan predicate. It retains the original thresholds, amounts and initialization. In a managed session it excludes live clans of every registered player, including disconnected players, and suspends on unresolved ownership. Duplicate callbacks during one campaign day are suppressed. Single-player execution follows the original methods.

**The resource-adder contract is not approved for automatic activation.** Both validation flags remain false. The protocol and policy fixture tests are not substitutes for the required engine integration checks. No runtime transformation is authorized by the analysis heuristics.

The inspected AutoSync path is: Hero.Gold property prefix / Clan._influence field interception → local set message → generated subscription → network broadcast or coalescer → client object lookup and value application under AllowedThread. AutoSyncPatchCollector catches installation failures, so registrations and generated templates alone cannot prove those patches are active. Real mutation/state comparison and actual patch ownership checks remain acceptance prerequisites.

## Verification and outstanding acceptance

The solution and optional integration fixture build. Offline tests cover cross-assembly calls, callbacks, mixed presentation/simulation, player-context propagation, inherited dispatch, constant DI, direct authority branches, unresolved reflection, shared models, malformed inputs, dynamic payload selection, contract matching/conflicts, profile round trips, commands and snapshots. Installed corpus analysis recognized all five external coverage entries, including camp suppression, and activated no new contract.

One isolated baseline was attempted on 2026-09-14 using `Run-Vanilla.ps1 -Stage Control`, separate ports 7398/4398, disposable data and a 120-second engine timeout. It did not reach the required serving checkpoint; the harness recorded a timeout and stopped the owned engine. Evidence is under the workspace `_operations-integration-proof/runs/20260914-201400-703-Control-operation_baseline`. No repeated launches were attempted after that failed prerequisite.

After reconciling upstream launcher fixes, a fresh isolated baseline passed on 2026-09-15: 71 seconds, serving checkpoint reached, clean exit, no new contract activated. Evidence: `_operations-reconciled-proof/runs/20260915-024536-173-Control-operation_reconciled_baseline/result.json`. The reconciled suite has 574 passing Core tests and 32 passing App tests. Original resource-award policy fixtures now cover configured amounts, duplicate callbacks, disconnected owners, ownership changes and single-player callback frequency; these remain distinct from engine replication validation.

Pending acceptance: real server/client resource awards and configured amount comparison, actual AutoSync installation and state propagation, fixture reconnect flow over Coop, and runtime coexistence with the external providers. The native adapter has not passed its complete offline engine fixture matrix either. Do not turn on its validation flags to bypass these checks.

Analysis remains conservative and incomplete for dynamic reflection, uncertain aliases, unrecognized loaders and dispatch. Engine effects are an explicit initial rule set, not complete engine semantic coverage. Direct guard evidence applies to the witnessed mutation, not every transitive mutation. Runtime plan agreement currently validates the shipped active contract and its required assembly hashes; full configuration fingerprint attestation still needs integration validation. No release was published.

## Simple and Advanced host compatibility

Host profiles default to Simple compatibility, including older profiles without a mode field. On the Mods tab, leave Advanced compatibility unchecked for Simple mode. Enable it to restore manual controls. Simple mode hides compatibility role/behavior controls, compatibility record editing, the Mod settings tab, and the server compatibility toggles. Read-only operation analysis remains available.

Simple launches use a detached effective profile: shipped role defaults, server guards enabled, settings sync disabled, automatic validated contracts enabled, no server-authoritative behavior recipes or client-side exceptions, and no manual map-distance-cache override. Local compatibility database overrides are excluded from server preparation. Mod selection, selected module copies, load order, saves and network settings remain intact. Simple does not mean every mod is multiplayer compatible; prerequisites and unresolved findings still apply.

Saved Advanced choices and settings sidecars are preserved. Switching back restores them. Server previews, host-side client launch, and profile-based server preparation use the effective mode; an already-running session retains its captured profile. Changes take effect on the next launch.

Verified 2026-09-17: 600 Core and 33 App tests pass, including old-profile defaults, saved override preservation, mode switching, effective host/client settings, and existing running-session snapshot tests. Actual UI inspection confirmed Simple/Advanced controls and paired Server settings hide/restore correctly. No game/server was launched during this UI check.
