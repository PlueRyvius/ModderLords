# Join barrier decision

Source inspection of the captured Coop build found a missing admission barrier. ModderLords' plan exchange currently runs alongside Coop's own join state machine; it does not delay character resolution or save loading.

Evidence from the local study (not redistributed): `ResolveCharacterState.Handle_ClientValidate` calls `ResolveCharacter`, which can restore a player's party and immediately call `ConnectionLogic.TransferSave`. On the client, `ReceivingSavedDataState.Handle_NetworkGameSaveDataReceived` directly calls `LoadSaveData` and `Logic.LoadSavedData`. Neither awaits the operation-plan acknowledgement. Checking late arrival in ModderLords.OnGameStart cannot establish that all earlier mod callbacks were protected.

Therefore successful message delivery is not proof of pre-campaign compatibility. The contract validation flags remain false.

Recommended integration: a compiled, fingerprint-bound Coop admission adapter owned by ModderLords. In managed sessions only, intercept client-validation progression before player restoration/character creation, retain at most one pending validation per peer, initiate plan exchange, then resume once after verified acknowledgement. Bound waiting and payload storage; disconnect on timeout, mismatched input or missing compatibility support. Discard pending state on disconnect and session end. Never hold a game-thread lock while waiting. Add a client load barrier as defense in depth. Check actual Harmony ownership before installing either hook. No installed Coop DLL edits.

This introduces an explicit Coop-version support boundary for managed operation sessions. An unknown Coop build must refuse managed activation; ordinary diagnostic/legacy sessions retain their existing behavior. Alternative: require a Coop-provided admission extension point, which avoids ModderLords intercepting join methods but makes completion dependent on upstream Coop changes.

The user approved owning this version-specific join adapter on September 15, 2026. Implementation is in progress; approval does not establish offline or native validation. Native multi-peer acceptance cannot pass honestly without one of these mechanisms. The remaining offline analysis work does not eliminate this lifecycle requirement.


Implementation checkpoint: the bounded ledger and compiled server/client hooks build; offline join-ledger and module-order tests pass. The isolated server successfully installed the diagnostic plan and reached serving state with the original fixture registered. Client initialization is blocked pending a running, signed-in Steam client; no successful join or resource replication is claimed. The captured Coop.Core hash is enforced locally and is now an explicit resource-contract prerequisite.
