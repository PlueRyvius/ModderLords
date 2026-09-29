# Bellum Civile adapter status

Scope: Bellum Civile `1.3.1`, Bannerlord `1.4.8`, War Sails disabled. Bellum is intentionally version-pinned;
Coop is not. This adapter does not modify or redistribute Bellum files.

## Completed foundation

- The pinned Bellum world creates, saves, reloads, reaches Coop `SERVING`, and shuts down cleanly.
- Coop managed admission is guarded by exact capability/signature probes instead of a historical `Coop.Core.dll`
  hash. Session attestation still requires the server and client to agree on the bytes selected for that session.
- `bellum-civile.state` and `bellum-civile.authority` are bundled compiled contracts with both validation flags
  false. They therefore report `ValidationRequired` and cannot activate automatically. The exact environment token
  `bellum-civile-1.3.1-isolated` can activate only those two contracts in a disposable validation process; it does
  not alter the catalog or production flags.
- Bellum state is copied into an explicit ID-only schema. Live TaleWorlds/Bellum objects and arbitrary reflected
  graphs are never serialized.
- The schema now has explicit ID/value codecs for all 36 reviewed nontrivial Bellum `SyncData` fields. In addition
  to the primary political records, it covers war score/will, foreign wars, services and drift, fabrication,
  civil-war/faction snapshots, relation history, dynastic succession, and every reviewed pending-resolution record.
- Snapshot transfer is bounded at 32 UTF-8 chunks of 224 KiB, supports out-of-order delivery, rejects conflicting
  chunks and stale revisions, and reassembles only inside the authenticated session epoch.
- The client mirror replaces its read model only after a complete schema-valid snapshot.
- Reviewed server mutation handlers mark the shared Bellum snapshot dirty. Changes are coalesced for at least two
  seconds, captured once, revision-deduplicated, and fanned out to all currently admitted actors through the same
  bounded transport. Actor-specific snapshot operations cannot opt into this shared path accidentally.
- The current client projection reconstructs every represented Bellum state family in the 36-field schema,
  including faction, title/claim, succession, regency, treaty/tribute, council, feud, mercenary, war-score/will,
  foreign-policy, service/drift/fabrication, relation/memory, dynastic/cadet and pending-resolution state. It resolves
  TaleWorlds references only by stable IDs against existing campaign objects and invokes Bellum's pinned
  lookup/index/cache rebuild hooks.
- The live probe now goes beyond signature checks: it builds the authentic projection graph, then deep-clones that
  snapshot and adds an in-memory representative of every empty record family and nested constructor. Both graphs
  pass codec and materialization validation; the coverage fixture is discarded and never touches the campaign or
  disk. Committing the authentic graph in a joined client remains pending.
- `bellum-civile.authority` now records 71 exact Bellum method surfaces. It gates ordinary campaign mutation
  callbacks and mutation-only callees while retaining mixed UI/presentation handlers on clients. The Bellum DLL is
  still strictly pinned; the contract's validation flags remain false, so these patches cannot activate in a normal
  launch yet.
- Five mixed callbacks are deliberately excluded from whole-method gating: council incident presentation, dynamic
  mercenary departure presentation, treaty daily/presentation processing, and the war-score map widget. Their
  authoritative state and UI handoff must be completed before activation.
- The isolated dedicated-server activation now passes end to end. The plan freezes both adapters before campaign
  initialization, the save reaches `SERVING`, Bellum mutation handlers execute only on the server, and shutdown
  saves cleanly. A bounded real-client startup also passes the current Coop capability probes and installs the
  client save-load admission barrier. No client has joined this validation save yet.

## Live snapshot evidence

An opt-in one-shot probe (`MODDERLORDS_BELLUM_SNAPSHOT_PROBE=<absolute path>`) was run against the disposable
Bellum save on 2026-09-28. It does not activate the contract or patch Bellum.

The exact previously validated save loaded, reached `SERVING`, built the authentic and discarded full-family client
projection graphs without mutating server state, emitted a 368,682-byte schema-valid authentic snapshot, then saved
and stopped cleanly with exit code 0. Snapshot counts were 205 titles, 29 factions, 8 succession-law records, 48
council-office records, 4 war-score records, 73 war-will records, 73 pressure records, 4 active foreign wars, 606
relation-memory records, and 8 dynastic-succession records. The live save had no active claims, treaties, tributes,
regencies, feuds, mercenary bands, client kingdoms, services, drift, fabrications or pending resolutions at this
checkpoint; those empty families were exercised only in the discarded fixture. The payload contains no live
`TaleWorlds` objects or arbitrary type metadata. Its SHA-256 was
`633001F16437C39C5A81BF632524376F25883B309A7B324C6516F9640181F800`.

An earlier 5,579,137-byte capture containing 26,562 relation memories separately proved that the same codec and
chunk transport remain below their 7 MiB bound under a much larger real payload.

Evidence:

- Full-family projection launch: `C:\Users\A\AppData\Local\Temp\modderlords-launch-20260928-193919.log`
- Snapshot: `D:\Design\Bannerlord Mods\_bellum-civile-analysis-data\live\BellumCompatSmoke-full-family-projection-snapshot.json`
- Isolated two-adapter server activation: `C:\Users\A\AppData\Local\Temp\modderlords-launch-20260928-195407.log`
- Client startup/admission log: `C:\Users\A\Documents\Mount and Blade II Bannerlord\Configs\ModLogs\ModderLords.Compat-client.log`

## Deliberately not claimed

The adapter is not playable compatibility yet. The first simulation-authority and full read-model tiers are
implemented but deliberately disabled pending native client admission and UI/action work. Player actions are not
yet actor-aware server commands, and the five mixed callbacks remain outside the authority gate. No successful
client admission, late join, reconnect, political command, dynamic-object replication, or long soak is claimed.
The validation flags must remain false until those paths pass the integration matrix.

Next validation/implementation order: join one disposable client and verify authenticated plan agreement plus the
initial full snapshot; then add faction/title/treaty/council/succession commands with ownership and
expected-revision checks; player-targeted decisions; two-player, reconnect, save/restart and soak validation.
