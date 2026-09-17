# Peer harness audit — 2026-09-15

## Result and limits

The harness has been audited and its process control tested without starting Bannerlord. Twenty-five original fake-peer checks pass. The fixture builds and its entry assembly passes a metadata-only check for unwanted eager Coop/runtime dependencies. The Windows PowerShell 5.1 entry point successfully forwards to PowerShell 7.

This is **not native multiplayer acceptance**. No game process was launched during this audit. Resource-adder activation remains withheld, and command/result, reconnect, resource replication, and provider coexistence still need native validation.

Read-only preflight of `_operation-peers-20260915` found matching staged fixture/runtime files and an intact empty diagnostic plan. Its disposable save sidecar contains **zero registered players**. The unattended command test is therefore blocked before starting either peer. An actual test player must be created and saved through Coop's normal character flow; inventing a registry entry would invalidate ownership testing.

## Evidence behind the corrections

| Assumption | Inspection and conclusion |
| --- | --- |
| `--stop-after` keeps the host alive for that duration | The launcher `RunEngine` races serving against that timeout, then sends `stop` after five seconds. Run 044924 records that exact stop. Removed the flag; the supervisor owns deadlines. |
| Discovering the fixture manifest means the client loads it | Inspected actual client and server `TaleWorlds.MountAndBlade.Module`: `LoadSubModules` calls `CheckIfSubmoduleCanBeLoadable` before loading the entry assembly. `GetSubModuleValiditiy` accepts `custom` only in custom dedicated-server mode. Empty tags pass both sides. Run 045146 discovered the manifest and reached the initial menu without loading the old bootstrap. |
| The original fixture could reference Coop directly in its entry assembly | Run 043336 failed while the engine resolved those dependencies before the runtime was ready. The separate entry assembly now references only framework/game APIs; the driver loads after Coop.Core and the compatibility runtime are present. |
| Every joined player proceeds to campaign | Inspected captured `ResolveCharacterState.ResolveCharacter`: a missing/stale player registration goes to character creation. The prepared save has no players. The fixture now reports that state explicitly; preflight refuses unattended command testing with an empty registry. |
| Steam explained the earlier startup exit | Not established. Absence of a Steam process was only an observation. The revised check describes process presence separately from account authentication; it does not claim to verify sign-in. |
| Parent process exit means every child is gone | False. A fake peer demonstrates a descendant surviving its parent. A handshake worker is assigned to a kill-on-close job before it receives permission to start the target, so target descendants inherit containment. Cleanup closes the job even after parent exit. |
| Any completed-result string plus `snapshot=1` proves a round trip | False (`snapshot=10` also matched). Run-scoped JSON events now require one submitted request, a matching completed result with value/revision 1, exactly one server counter execution, and a post-submission snapshot value exactly 1. |

Inspected assembly hashes (SHA-256):

- Client `TaleWorlds.MountAndBlade.dll`: `19387F31557FF840D14F378F6BBDF1D58FCFF406AB9FF2294DFBB3E49A50B87E`
- Server `TaleWorlds.MountAndBlade.dll`: `BFB066AEB7F73C88A8B085C2426E4F7C53934430DB9C59F3B4592804041CC4A6`
- Captured `Coop.Core.dll`: `90121C59FF8B6D379933CE01D9A4C4BB842FCFE730CD5131C1072D4332A47CF1`

Decompilation was retained in the separate local study directory. No third-party source was added to the repository.

## Process and evidence contract

- Default invocation is **Preflight**, which starts no game. Explicit native stages are `ClientStartup` and `CommandSnapshot`.
- `ClientStartup` starts only the client and observes initial-menu readiness. It cannot count as a join or replication test.
- `CommandSnapshot` requires fixture bootstrap, driver, server registration, and an actual structured serving event before starting the client. A listening UDP socket alone is insufficient.
- Each run has a unique GUID. Fixture events from a different run are rejected; partial JSON lines wait for completion and malformed completed lines fail.
- Startup and join deadlines are monotonic and do not restart when another progress event arrives.
- Both stdout and stderr stream immediately to disk. Each stream is capped at 16 MiB; overflow fails the run while pipes continue draining until cleanup.
- Outcomes distinguish the last incomplete stage, an input requirement, a natural exit, and a forced termination. Cleanup has bounded waits and records errors per peer rather than abandoning cleanup of later peers.
- The full process environment is not exported. Local control files hold only environment overrides/removals. Connection/control files are local launch material and must not be included in shared diagnostic bundles.
- Test copies are compared against current fixture/runtime build hashes, preventing an old staged DLL from being silently tested after a rebuild. Preparation preserves a disposable save's sidecar when present.

## Offline checks

Run `tools/PeerHarness/Test-PeerHarness.ps1` in PowerShell 7. It uses original fake PowerShell processes and the same supervisor, worker, event evaluator, deadline logic, and cleanup functions as the native harness. It does not require Steam or game assemblies. Windows CI runs this suite with a three-minute job-step timeout.

Coverage: exact correlation; missing server/client checkpoints; registered-but-not-serving; required character creation; rejected/wrong/duplicate results; duplicate mutation; value 10 vs value 1; premature result and old snapshot; empty player registry; stale/malformed/partial events; executable startup failure; live logs and graceful stop; exit code 23; hung peer; simultaneous pipe pressure; output cap; nonextending deadlines; parent exit with a surviving child; isolation from unrelated processes.

Final local suite evidence: `artifacts/peer-harness/251adb66ddb84148a6e2d936ec6cf846/results.json` (25 passing checks). Earlier fake-test failure identified a sharing-mode bug when reading a log still open for writing; that was fixed and the suite rerun successfully.

## Next native prerequisites

1. Independently validate client startup, with explicit stage selection and the current staged fingerprints.
2. Prepare one real registered character in the disposable campaign through an interactive Coop session. Keep original saves untouched. Confirm that the account used for the test owns that registration.
3. Only then run the command/snapshot stage. A successful stage proves the original counter fixture only.
4. Reconnect, second-player ownership, the resource-adder mutation routes, and external-provider coexistence remain separate acceptance checks.

Client configuration-path isolation is not yet proven for every native/managed API. `BANNERLORD_USER_DIR` alone is not evidence that all configuration writes are redirected. The driver avoids Coop's `/autoconnect` path, whose code explicitly rewrites the normal user engine configuration. No additional native run is authorized by this audit document itself.

## Native startup checkpoint — 2026-09-16

The isolated ClientStartup stage passed. Run: ../_operation-peers-20260915/runs/20260916-224518-91e8e16a8aed490689a36d9b6d06fbd9/result.json. Run-scoped events confirm bootstrap.loaded, driver.loaded, and menu.ready. The supervisor terminated its owned client after success; cleanup recorded no errors. No server was started and no campaign joined. The disposable save still has zero registered players. Next prerequisite is interactive creation and saving of a real test character through Coop, before command/snapshot testing. Startup success does not approve any resource-adder or multiplayer contract.

## Interactive preparation checkpoint — 2026-09-16

Run `../_operation-peers-20260915/runs/20260916-225317-e211b0862d21410d805c7162146e3d9c` failed. Server was serving and the loopback client reached Coop CharacterCreationState at 22:55:17. Native engine logs show VideoPlaybackState, followed by a return to the initial screen at 22:55:48. Coop reports a session-cancellation exception in DisconnectHandler/CoopFinalizer. This is not proof of the initiating cause. The user saw a stopped message; manual retries targeted default port 4200 rather than this run's 4397. Neither a visible character-creation form nor a completed character was verified. Registration remains empty, and no fixture counter command ran.

Both owned peers stopped without cleanup errors (client natural exit -1; server graceful shutdown). Raw local Coop/engine logs were preserved inside the run directory, not exported. No automatic relaunch was performed. Resource-adder, command/snapshot, reconnect and provider acceptance remain pending.

PrepareCharacter now has an explicit visible target and bounded human-input deadline (default 600, maximum 900 seconds). Observation disables command submission; campaign admission must be followed by updated disposable save files and a player registration. Returning to MainMenuState now emits connection.ended and fails preparation instead of waiting for the human deadline. Native verification of this new checkpoint is pending. Fixture build passes with zero warnings/errors; fake harness suite has 29 passing checks in `artifacts/peer-harness/cfddaafef121474ca6f0c5785bc483c6/results.json`. Updated fixture files were restaged only into the isolated workspace. Investigate the initial intro-to-menu disconnect before another native attempt; character.required alone must never be described as a visible UI checkpoint.
