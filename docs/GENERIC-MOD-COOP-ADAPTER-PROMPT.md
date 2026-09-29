# Generic ModderLords + BannerlordCoop adapter prompt

Copy everything below the divider into a new coding-agent task and replace the bracketed inputs. The instructions are
intentionally mod-agnostic.

---

You are adapting an arbitrary Mount & Blade II: Bannerlord mod to work through ModderLords with BannerlordCoop.

## Target inputs

- ModderLords repository/worktree: `[MODDERLORDS_REPO]`
- Target mod folder: `[TARGET_MOD_PATH]`
- Target module ID: `[TARGET_MOD_ID]`
- Target mod version: `[TARGET_MOD_VERSION]`
- Bannerlord installation: `[BANNERLORD_PATH]`
- Bannerlord version: `[BANNERLORD_VERSION]`
- Installed BannerlordCoop/workshop path: `[COOP_PATH]`
- Intended DLC configuration: `[DLC_STATE]`
- Disposable data/profile/save directory: `[TEST_DATA_PATH]`
- Desired compatibility scope: `[FEATURES_OR_FULL_FUNCTION]`
- Known third-party compatibility providers: `[KNOWN_PROVIDERS]`
- Publication allowed: `[NO_UNLESS_EXPLICITLY_APPROVED]`

## Objective

Produce a safe, compiled ModderLords compatibility adapter that makes the requested target-mod functionality
server-authoritative and synchronizes the required client read model through the existing ModderLords operation
system. Work toward full functionality, but never present a partially validated tier as production-ready.

The target mod may be pinned to the exact reviewed version when its private serialized state, reflected fields,
constructors, or broad internal behavior surface are part of the adapter contract. BannerlordCoop must not be
version-pinned or whole-file-hash-pinned. Coop changes frequently: bind it through narrow runtime capability and
exact-signature probes so compatible updates continue to work and incompatible updates fail closed.

Do not publish a release or enable production validation without explicit maintainer approval.

## Questions to resolve

Ask these at the beginning if the inputs do not answer them. Continue safe read-only discovery while waiting.

1. Is local decompilation permitted for interoperability analysis?
2. May the adapter strictly pin this target-mod version?
3. Does full functionality include campaign simulation, player UI/actions, battles/missions, dynamic objects,
   save/reload, late join, reconnect, and multiple simultaneous players?
4. May you create disposable profiles, saves, server data, module overlays, and local test packages?
5. Which DLCs and optional dependencies must be supported?
6. Are any separate compatibility mods/providers in scope?
7. Will a human be available for character creation, connection, and UI-driven native tests?
8. May target-mod or Coop files be modified? Default: no.
9. May anything be pushed, released, or uploaded? Default: no.

## Safety and licensing rules

- Read the repository's `AGENTS.md` and development/release instructions before changing anything.
- Inspect Git state and work on an isolated, clearly named branch or worktree. Preserve unrelated changes.
- Do not modify target-mod files or BannerlordCoop files in place.
- Do not redistribute target-mod or Coop binaries, source, decompiled output, or assets.
- Treat Coop source as interoperability reference, not code to copy. Write original adapter code.
- If target-mod decompilation is permitted, do not commit decompiled source.
- Use disposable saves and data/profile directories. Never risk the user's normal campaign or configuration.
- Do not publish. Build local packages only when authorized or needed for testing.
- Keep production contract validation flags false until the full acceptance matrix passes.
- Never weaken global validation to get one adapter running.

## Phase 1: exact baselines

Before implementing:

1. Record exact versions and SHA-256 hashes for the target-mod DLLs and manifest, Bannerlord/DLC state, current Coop
   assemblies, ModderLords commit/branch, and disposable save.
2. Inventory target submodules, entry DLLs, XML/assets, dependencies, load-order declarations, and client/server
   restrictions.
3. Inspect the current installed Coop build and current upstream repository. Installed assemblies are runtime truth.
4. Run ModderLords operation analysis against the exact client and server module sets. Preserve method identities,
   fingerprints, authority findings, gaps, providers, module order, and configuration/environment fingerprints.
5. Search for an existing provider or adapter that owns any intended target. Existing matching coverage, suppression,
   or Harmony ownership wins; never install a competing patch without explicit coexistence proof.
6. Prove a disposable control baseline: target mod loads as far as expected, server reaches serving, save/reload and
   clean stop work, and no new adapter activates.
7. Do not equate "the world loaded" or "it did not crash" with compatibility.

## Phase 2: deep code discovery

Use source where available; otherwise use metadata/IL inspection and permitted local decompilation. Inventory:

- every campaign behavior, event callback, mission behavior/view, model, query, calculation, and cache;
- every Harmony patch, owner ID, target, dynamic `TargetMethods` result, and intercepted member;
- menus, dialogs, inquiries, view models, hotkeys, cheats, console commands, and callbacks;
- every save/`SyncData` field, nested record, pending queue, timer, random state, revision, index, and cache;
- static and instance state read by simulation or UI;
- dynamic object creation/destruction and whether Coop identifies and replicates those objects;
- uses of `Hero.MainHero`, `Clan.PlayerClan`, `MainParty`, player troop/faction globals, and host assumptions;
- TaleWorlds and cross-mod mutations, reflection, DI, delegates, virtual dispatch, and unresolved boundaries;
- UI/render dependencies unsafe on a headless server;
- background threads, file I/O, networking, and nondeterministic behavior.

Trace transitive effects from every root and classify each independently by:

- loading side: client, server, both, or headless-incompatible;
- authority: local presentation, campaign simulation, mission simulation, shared calculation, or mixed;
- interaction: autonomous callback, player command, targeted decision, query, or presentation;
- replication: externally covered, command/result, snapshot, dynamic-object support, local only, or unsupported;
- evidence: offline proved, native proved, pending, conflicting, or untestable.

A getter named `IsServer` or a call to an authority service is not proof. Confirm that mutation is actually dominated by
the guard in control flow.

Create a compatibility matrix before coding. Each row must name the entry point, state read/written, actors/owners,
existing provider, required server gate, player command, client projection, UI consequence, test, and evidence status.

## Phase 3: authority boundary

- Run shared campaign and mission mutations only on the authoritative server.
- Retain client presentation, menus, dialogs, view models, and calculations needed to render synchronized state.
- Do not gate an entire mixed callback just because part of it mutates. Patch mutation-only callees and preserve the
  presentation path.
- Keep uncertain mixed handlers outside the authority gate until their mutation path and UI handoff are understood.
- Turn every player action that changes shared state into an authenticated server command. Never mutate locally first
  and merely queue a request afterward.
- Show pending/rejected/completed client state from a correlated server result.
- Re-resolve identity and ownership on the game thread. Never trust a hero, clan, party, settlement, role, or cost
  asserted by the payload.
- Revalidate permissions, current ownership, prerequisites, costs, bounds, and expected revision before mutation.
- Commands must be bounded, allow-listed, atomic, and idempotent. Reject request-ID reuse with different content.
- Do not apply generic latest-value coalescing to purchases, increments, decisions, or other non-idempotent actions.
- Mark authoritative state dirty only after successful mutation.
- Do not hold a game-thread lock while waiting for network agreement.
- Clean up patches, subscriptions, requests, peers, chunks, and session state on disconnect/end.

## Phase 4: explicit synchronized state

Never serialize arbitrary reflected object graphs. Build a reviewed DTO schema containing only stable string IDs,
bounded primitives/enums, deterministic bounded record lists, and explicit schema/revision fields. Include no live
TaleWorlds objects, behavior instances, arbitrary type metadata, or local paths.

For every target-mod save field decide and document whether it is client-visible authoritative state, server-only,
derived/rebuilt locally, transient but required for reconnect, or deliberately unsupported.

Codec requirements:

- deterministic ordering/serialization and unique record keys;
- finite numbers, bounded strings/collections/payload, valid enums/ranges, and referential integrity;
- explicit schema compatibility behavior;
- malformed input rejected before live state is touched.

Server capture requirements:

- capture on the game thread from one canonical authoritative graph;
- distinguish shared snapshots from actor-specific snapshots;
- coalesce dirty notifications only when semantically safe;
- increment revision only when canonical content changes;
- capture actor-independent state once and fan it out to authorized admitted peers.

Client projection requirements:

- resolve references only by stable ID against valid campaign objects;
- construct and validate the entire replacement graph before commit;
- reject the whole snapshot if any record cannot be materialized;
- atomically swap the read model, rebuild every index/cache through reviewed hooks, then refresh UI;
- defer until required campaign objects exist, reject stale revisions, and preserve old valid state on failure.

Exercise live-empty record families with a discarded synthetic in-memory projection fixture. Never inject synthetic
records into the campaign or save, and report synthetic coverage separately from authentic live-state coverage.

Use ModderLords' existing bounded chunk transport. Do not enlarge global limits to accommodate an inefficient graph.
Test Unicode boundaries, out-of-order and duplicate/conflicting chunks, stale revisions, oversized payloads,
disconnect cleanup, and reassembly reset.

## Phase 5: compiled ModderLords contracts

Implement reviewed compiled adapters, not heuristic runtime transformations. Each contract must declare its unique ID
and version, module/operation/provider, authority and actor binding, effects, transport/lifecycle, exact required
inputs, explicit patch targets, adapter ID, validation flags, and canonical method surfaces.

Pinning rules:

- Strictly pin the target-mod DLL when reflecting/reconstructing a broad private schema. Validate required fields,
  constructors, methods, and record shapes at runtime.
- For narrow patches, capture every canonical method signature/body surface and provider-local caller count.
- Use ModderLords' resolved canonical surface format, not raw IL metadata tokens.
- Refuse missing, changed, unpatchable, ambiguous, or unexpectedly overloaded targets.
- Extend identities to full signatures when overloads exist; do not trust a name-only lookup.
- Treat caller-count drift as an offline review failure. Do not falsely claim it is runtime-enforced.
- Check Harmony ownership before installation. Refuse unreviewed conflicts.
- Ensure every contract target is installed and every installed patch is represented in the contract.
- Register operations before the dispatcher freezes and add the adapter to the compiled runtime registry/factory.
  JSON alone cannot instantiate adapter code.
- Keep legacy behavior gates from overlapping compiled targets and preserve external providers/suppressions.

Do not reuse or broaden an existing adapter-specific validation escape hatch. If native testing needs temporary
activation, obtain approval for a new exact target-specific, process-local allowlist. It must not alter catalog or
production flags and cannot apply to unrelated contracts.

## Phase 6: current-Coop capability binding

Do not add a Coop version or whole-DLL hash as a production requirement. Probe only the current capabilities used:

- exact server admission and client save-load hook signatures;
- message subscribe/unsubscribe, targeted send, and broadcast;
- peer identity and player-to-peer/object resolution;
- connection/disconnection lifecycle and game-thread dispatch;
- any additional object or AutoSync seam genuinely required.

Resolve types/methods from loaded assemblies and verify static/instance form, parameters, return type, patchability,
and ownership. If a seam moved, disappeared, became ambiguous, or acquired another owner, fail closed before campaign
admission. A capability probe proves only that the consumed ABI exists, not semantic compatibility with every future
Coop implementation.

Preserve the authenticated operation flow:

1. Server creates an immutable plan and local input attestation.
2. Runtime verifies integrity, module order, file hashes, loaded assemblies, compiled-contract equality, target
   surfaces, and adapter readiness.
3. Plan freezes before campaign initialization.
4. Server blocks Coop validation before character restoration.
5. Client validates the plan against independently prepared local inputs.
6. Client acknowledges the exact digest and session epoch.
7. Only then may character restoration resume; client save loading stays blocked until agreement/readiness.
8. Commands/snapshots require the admitted peer, current epoch, and digest.
9. Reconnect may agree with the frozen plan but cannot replace it.
10. Mismatch, timeout, changed input, lost readiness, missing capability, or late plan arrival refuses/disconnects.

Custom-message delivery by itself is not proof of safe admission.

## Phase 7: headless server

Assign every dependency deliberately:

- `Run`: code is required and headless-safe.
- `DependencyOnly`: dependency/data must be exposed but submodules must not execute headless.
- `AsShipped`: manifest is already correct for the server.

Record exact client/server orders, manifest module IDs rather than folder names, roles, overlays/junctions, resolver
paths, manifest rewrites, native dependency failures, disabled-DLC discoveries, duplicate assembly copies, and
headless gaps. Classify expected noise rather than suppressing it globally. Required target inputs, selected copies,
module order, and runtime surfaces must still verify exactly.

Prove create/load/save/reload in the disposable headless environment before synchronization testing. Diagnose a
headless loading failure independently from replication.

## Phase 8: tests

Offline tests must cover at least:

- contract match/mismatch, provider conflict, suppression, and disabled automatic compatibility;
- exact target-surface coverage and Cecil/reflection parity;
- missing, changed, ambiguous, overloaded, unpatchable, and conflicting-owner failures;
- runtime registry recognition, module-order/input mismatch, and readiness loss after late assembly loading;
- admission bounds, timeout, duplicate validation, disconnect, and one-time resume;
- bounded commands, unauthenticated actors, ownership changes, deduplication, request-ID content mismatch,
  rejection-without-mutation, atomic failures, and client pending/result/disconnected states;
- snapshot authorization, deterministic codec/order, malformed IDs/records/enums/ranges/numbers, and size bounds;
- chunk ordering, Unicode, stale/conflicting chunks, and disconnect reset;
- deferred and failed projection, old-state preservation, all index/cache rebuilds, and synthetic empty families;
- cleanup/reinitialization, dirty-state coalescing, revision deduplication, and provider coexistence.

Run the complete repository test suites, normal build, and optional integration fixture—not only focused adapter tests.
Record exact pass counts and commit hashes.

## Phase 9: native acceptance matrix

Use disposable profiles, saves, ports, and logs, preserving evidence from failures as well as successes.

1. Control server with no new contract active.
2. Target-mod server baseline without adapter mutation.
3. Isolated adapter server: frozen plan, ready adapters, installed gates, serving, server-side callbacks, clean save.
4. Client startup with current Coop probes and client admission barrier.
5. One-client join: digest/epoch agreement, exactly-once resume, no early save load, full snapshot reassembled,
   validated, committed, and UI refreshed.
6. One real player action: local mutation suppressed; authenticated server command validates actor/ownership/rules;
   correlated result returns; snapshot reconciles client.
7. Independent server-side mutation propagates without a client command.
8. Two simultaneous clients observe identical shared state.
9. Unauthorized/stale actions are rejected without mutation.
10. Late join receives current complete state.
11. Disconnect/reconnect restores current state without duplicate effects.
12. Save, clean stop, restart, reconnect, and compare state.
13. Plan/input/module-order mismatch fails before restoration/save loading.
14. Changed target surface or missing target fails closed.
15. Missing/moved Coop capability fails closed without Coop pinning.
16. Coexistence with installed providers and actual Harmony owners.
17. Relevant mission/battle transitions.
18. Soak long enough for periodic callbacks, state churn, coalescing, and payload growth.
19. Repeat for each supported DLC configuration.

Capture commands/profiles, module lists/roles, hashes, ports/data paths, plan digest/epoch, readiness, Harmony owners,
request/result IDs, snapshot revisions/bytes/chunks/record counts, authority counts, save evidence, exit codes, all
relevant logs, and warning classifications. Manual "it worked" evidence is useful but does not replace logs.

## Production gate and strict non-claims

Leave `OfflineValidated` and `RuntimeValidated` false until every in-scope offline and native item passes, including
one/two clients, commands, late join, reconnect, save/restart, relevant battle/mission paths, projections, provider
coexistence, and target/Coop fail-closed behavior. Require maintainer review before changing the flags.

Never toggle flags merely because the project builds, tests pass, a server serves, a snapshot serializes, or a client
reaches the main menu.

Maintain a `Not yet proved` section. Never conflate:

- static analysis with runtime coverage;
- fingerprint match with successful patch installation;
- provider recognition with provider activity;
- server startup with client admission;
- client startup with campaign join;
- message delivery with pre-campaign agreement;
- snapshot capture with client projection commit;
- synthetic fixtures with authentic live state;
- command submission with completion;
- one client's UI with replicated state;
- no crash with correct authority;
- save creation with correct persistence;
- one target version with future Coop compatibility;
- a capability probe with semantic equivalence;
- unit tests or a server-only run with full-function compatibility.

## Deliverables and working style

Deliver an isolated branch/worktree, compatibility matrix, fingerprint manifest, compiled contracts and runtime
registration, authority adapter, explicit DTO/codec, server capture/client projection, actor-aware commands and UI
bindings, Coop capability probes/admission integration, tests/harness, and an evidence report listing passed, failed,
untested, and synthetic-only items. Build a local package/profile only if authorized. Do not release, upload, or alter
production validation without approval.

Report material findings and blockers as they arise. Verify from current code, binaries, and logs instead of guessing.
Continue autonomously through analysis, implementation, offline tests, server tests, and client preparation. Stop
only for genuinely interactive game actions, unavailable credentials, licensing/destructive decisions, or material
scope choices. When stopped, state exactly what passed, what remains, the user's required action, and the evidence to
collect next.

---

## Maintainer caveats for the receiving agent

- A new adapter needs both a contract entry and an explicit runtime factory/registry case.
- Current method-surface runtime validation checks bodies; caller-count drift is an offline recapture/review control.
- Existing `Type::Method` identities need extension or enforced uniqueness for overloads.
- Normal input attestation rejects unresolved gaps. Never generalize another adapter's isolated validation gate.
- The admission barrier refuses unexpected Harmony ownership by design.
- Snapshot bounds are fixed globally; design state within them instead of enlarging them reflexively.
- Shared snapshots must never carry actor-dependent/private state.
- Heuristic classifier/recipe output is diagnostic for new transformations; production authority belongs in compiled,
  reviewed contracts.
