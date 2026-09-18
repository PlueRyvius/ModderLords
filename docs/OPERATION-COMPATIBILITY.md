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

## What a contract pins: the methods, not the file

A contract used to be pinned to whole files: every required DLL by SHA-256, refused on any mismatch. That is the
right guard for Coop's own assemblies, which an adapter reaches into by reflection across a wide surface — there is
no small set of methods that stands for the dependency. It was the wrong guard for the provider mod itself. An
adapter depends on the handful of methods it patches, so pinning the file meant a texture fix, an unrelated bug fix
or a plain rebuild silently disabled the contract, and a per-update maintenance cost bought nothing.

Contracts now carry a `TargetSurfaces` entry per target: the method's signature and body, hashed, plus how many call
sites it has in the provider's own assembly. `Requires` gains `Strict`. Coop's assemblies stay strict and still
refuse on any file change; the provider's own assembly is no longer strict, because its surfaces are the dependency.
Every required file must still be present and loaded.

**Raw IL cannot be hashed directly.** Operand tokens are metadata row indices that renumber whenever anything else
in the assembly changes, so a raw IL hash would move on every rebuild — the same false alarm as the file hash. Both
readers resolve every token to a name first and hash that canonical text (`MethodSurface`, `CecilSurface`).

**Two readers, one format.** The maintainer captures offline with Cecil, because a provider assembly references
TaleWorlds types and cannot be loaded for reflection outside the game. The game re-checks at activation with
reflection, over the assembly it actually loaded. `MethodSurfaceParityTests` compares the two over every method of a
real assembly, so the pair cannot drift apart unnoticed. Known deliberate limits of the canonical form: generic
arguments are reduced to the open type, and a `calli` signature is recorded as a marker only.

**The caller count is the other half.** An IL hash catches the patched method changing. It cannot catch a NEW caller
of it appearing elsewhere in the provider — which is exactly what would widen an adapter's blast radius without
touching a byte of the method it patches. The count is captured offline and reviewed on every recapture.

Capture with `dotnet run --project tools/CaptureProviderContracts`. It rewrites `provider-contracts.json` in place,
updating only the modules installed on the machine and leaving the rest untouched, and it never sets a validation
flag: a recaptured contract still has to pass offline and integration validation before it can activate.

## Verification and outstanding acceptance

The solution and optional integration fixture build. Offline tests cover cross-assembly calls, callbacks, mixed presentation/simulation, player-context propagation, inherited dispatch, constant DI, direct authority branches, unresolved reflection, shared models, malformed inputs, dynamic payload selection, contract matching/conflicts, profile round trips, commands and snapshots. Installed corpus analysis recognized all five external coverage entries, including camp suppression, and activated no new contract.

One isolated baseline was attempted on 2026-09-14 using `Run-Vanilla.ps1 -Stage Control`, separate ports 7398/4398, disposable data and a 120-second engine timeout. It did not reach the required serving checkpoint; the harness recorded a timeout and stopped the owned engine. Evidence is under the workspace `_operations-integration-proof/runs/20260914-201400-703-Control-operation_baseline`. No repeated launches were attempted after that failed prerequisite.

After reconciling upstream launcher fixes, a fresh isolated baseline passed on 2026-09-15: 71 seconds, serving checkpoint reached, clean exit, no new contract activated. Evidence: `_operations-reconciled-proof/runs/20260915-024536-173-Control-operation_reconciled_baseline/result.json`. The reconciled suite has 574 passing Core tests and 32 passing App tests. Original resource-award policy fixtures now cover configured amounts, duplicate callbacks, disconnected owners, ownership changes and single-player callback frequency; these remain distinct from engine replication validation.

Pending acceptance: real server/client resource awards and configured amount comparison, actual AutoSync installation and state propagation, fixture reconnect flow over Coop, and runtime coexistence with the external providers. The native adapter has not passed its complete offline engine fixture matrix either. Do not turn on its validation flags to bypass these checks.

Analysis remains conservative and incomplete for dynamic reflection, uncertain aliases, unrecognized loaders and dispatch. Engine effects are an explicit initial rule set, not complete engine semantic coverage. Direct guard evidence applies to the witnessed mutation, not every transitive mutation. Runtime plan agreement currently validates the shipped active contract and its required assembly hashes; full configuration fingerprint attestation still needs integration validation. No release was published.

## Per-mod compatibility: what gates it

There is no app-wide experimental switch. One was built (PR #78) and dropped: the per-mod **Server-only logic** tick
already gates that code and is off unless a user sets it, so a second off-by-default gate over the same code bought
only column hiding. See `docs/DEVELOPMENT.md`, "Per-mod compatibility paused".

A mod that is not ticked has nothing written into `recipes.json` — no handler gates, postfix removals, player-check
rewrites, relays, state sync or tracing. `compat-db.json` ships no record that pre-ticks it, so no profile picks the
experimental path up from the database alone. Normal server guards, settings synchronization, roles, compatibility
records and battle-scene exclusions are unaffected, and read-only operation analysis is always available.

Ticking it enables explicit legacy schema-v1 behavior gates and opt-in entry-point tracing. Generated handler gates,
player comparison rewrites, relay proposals and static-state transformations remain diagnostic even then. Only
separately shipped, validated compiled operation contracts whose target surfaces still match may authorize new
transformations.

Existing profile values are preserved; nothing here resets user roles or settings-sync choices. Running sessions
retain their captured profile and operation plan.
