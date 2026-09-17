# Original operation integration fixture

The current procedure and evidence are in [the peer harness audit](../../docs/PEER-HARNESS-AUDIT.md). This fixture is original test code, excluded from the release, and never changes game resources.

- `Run-OperationPeers.ps1` now defaults to **Preflight** and starts no game. It forwards from Windows PowerShell 5.1 to PowerShell 7 when available.
- Explicit `-Stage ClientStartup` is a standalone client startup check. Explicit `-Stage CommandSnapshot` requires registered players and verifies a correlated counter command and snapshot.
- Explicit `-Stage PrepareCharacter` starts a visible client and waits up to `-CharacterSeconds` (default 600, maximum 900) for human character creation. It disables fixture command submission and then requests a server save, requiring updated save files and a registered player. This is preparation, not command/snapshot acceptance.
- The currently prepared disposable campaign has **no registered players**. It cannot complete unattended command testing until a real test character has been created and saved through Coop.
- `Test-PeerHarness.ps1` exercises the same supervisor using fake processes; it needs neither Bannerlord nor Steam.

Build `ModderLords.OperationFixture.csproj` to build the dependency-free bootstrap and late-loaded driver. Preparation copies both DLLs plus the shared manifest into isolated module folders. Changed fixture/runtime hashes cause preflight to refuse stale copies. The empty diagnostic plan authorizes no third-party contract.

Native acceptance remains pending. Server startup succeeded in prior runs; a working native command/snapshot round trip has not been demonstrated. Do not enable resource-adder contract validation flags based on fixture compilation or offline harness tests.
