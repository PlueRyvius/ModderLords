# TAOM co-op layer — status and resume plan (2026-09-22)

## Context

The maintainer switched ModderLords from general, all-mods compat fixes to a **TAOM-specific co-op layer**. Goal:
every TAOM feature works in co-op. First must-have: no crashes or blocks getting in. The maintainer doesn't play TAOM,
so the work runs from source code, with short live tests at checkpoints. The rules agreed so far:
- Our own code.
- TAOM.CoopCompat (the TAOM team's patch) is a reference only; we don't install it, because it overwrites files.
- No PRs to TAOM upstream. Anything critical upstream is only noted in the map.

## What was done this session

**1. Blank culture list diagnosed: TAOM's install had been deleted, not a co-op bug.**
- At 2026-09-20 00:52:35, something bulk-deleted the contents of `%LOCALAPPDATA%\ModderLords\overlay\profile1\{TAOM, TAOM.Dependencies, TAOM_Map}`.
- Those "shadow" folders link into the real mod folders, so the delete followed the links. It emptied TAOM's ModuleData, GUI, Prefabs, ModuleSounds, Shaders and server bin, plus the matching folders in TAOM.Dependencies and TAOM_Map.
- The shadows are still empty. The actor is unknown: ModderLords' own code doesn't delete that way, and no Claude session was active.
- The maintainer reinstalled TAOM, which is now **v2.0.28**.
- Rule: never recursive-delete under `overlay\` or `client-launches\`; unlink the junctions first.
- The reinstall also reset `TAOM.Dependencies\coop-modules.txt`, which lost `CoopNightly`. The launcher's compat-db `EnsureLines` re-adds it at launch.

**2. TAOM source found:** github.com/haterade22/TAOM, branch `bannerlord-1.4.5`.
- Verified: module Id TAOM, v2.0.28, same files as the installed DLL.
- Licence: MIT for code (reusable with its notice); CC BY-NC-SA for assets.
- Sparse clone (code and docs only) at `D:\Work\Claude\Tech Support\TAOM-src`.
- Key reading: `docs/features/coop-interop.md`, `docs/research/bannerlordcoop-internals.md`, per-feature docs, `TAOM.Tests/Features/CoopInterop/*`.
- Decompiles of TAOM, Coop.Core and GameInterface are in the session scratchpad (`decomp\`). Regenerate with `ilspycmd -p -o <dir> <dll>`.

**3. Key finding.** TAOM is co-op-aware: it has authority predicates and about a dozen features restricted to the host.
But it refuses to depend on any co-op mod, so it **cannot send anything between peers**. Where a client needs the
server to act, it declines. Where the server changes TAOM state, clients never hear about it. ModderLords already has
that network channel (`ModderLords.CompatSync.Coop`), so **our layer is that missing channel**.

**4. PRs (all OPEN, none merged, none run live):**

| PR | Branch | Content |
|---|---|---|
| #107 | `docs/taom-layer-plan` | `docs/TAOM-LAYER-PLAN.md`, the plan (decision 2 there is outdated, see below) |
| #108 | `docs/taom-map` | `docs/TAOM-MAP.md`: backlog P0–P6 plus a feature inventory |
| #109 | `feat/taom-layer-skeleton` | Code, module version **0.1.8**. Core 777 and App 40 tests green |

**#109 contents:**
- **Launcher** (`src/ModderLords.Coop/Launch/TaomLaunchPolicy.cs`, wired into `LaunchSession.Prepare`):
  - `InstallProblems` refuses a TAOM launch if TAOM, TAOM.Dependencies, TAOM_Map or LOTRLOME_Armory lacks ModuleData, or TAOM lacks GUI.
  - `SyncProblem` refuses TAOM with Settings sync off.
  - Tests are in `tests/ModderLords.Core.Tests/TaomLaunchPolicyTests.cs`.
- **In-game** (`src/ModderLords.CompatSync.Coop/Taom/`):
  - `TaomLayer.cs`: called from `Bridge.Tick`, and inert unless TAOM is an active module. Components implement `ITaomComponent`: a reflection check of the TAOM members they use, then `SkipReason` or `Install`, then a log line `TAOM layer: <id> on/off: …` in `Documents\Mount and Blade II Bannerlord\Configs\ModLogs\ModderLords.Compat-{server|client}.log`.
  - `coop-detection` warns when TAOM's `CoopPresence.IsActive` is false.
  - `server-binary` warns when the server loaded TAOM from outside `Win64_Shipping_Server`.
  - `join-grant` (`TaomJoinGrant.cs` and `TaomHandlers.cs`) works in four steps:
    1. The client postfixes TAOM's `PlayerPossessionService.CaptureCharacterCreationChoices`.
    2. At `CampaignReady` the client sends `NetworkTaomJoinChoices`.
    3. The server resolves the player's hero through `IPlayerManager` and `IObjectManager`, then runs TAOM's `IJoinReconciliationService.ReapplyCharacterCreationPackage` under `ServerRelay.PlayerScope` (made `internal`). It records the hero in TAOM's persisted `PlayerPossessionBehavior._reconciledHeroIds`.
    4. The server replies with `NetworkTaomJoinResult`. `Final=false` means "player not known yet, send again".
- A Release build of the branch exists at `src\ModderLords.App\bin\Release\net10.0-windows\ModderLords.exe`.

**Design decision changed from the plan (update #107):** the TAOM layer lives inside the existing
`ModderLords.Compat` module (`CompatSync.Coop/Taom/`). It is not a separate `ModderLords.TAOM` module, and it doesn't
use the `Operations` framework. That framework needs validation flags and strict hashes before anything activates,
and it has never run live. Isolation still holds, because the layer is inert without TAOM. The deploy path is reused,
and components use reflection checks instead of IL pins.

## Path forward

### Step 1: live checkpoint (the maintainer, when available)
Use the Release build above, on the TAOM profile:
- Settings sync ON.
- Experimental compatibility OFF.
- TAOM.CoopCompat unticked.
- "Use a map mod's distance cache" ON.

Then:
1. Host a new world, join, and create a new character. The culture list should be populated.
2. Note the culture picked and the gold on arrival. Enter a town and play about 5 minutes.
3. Agent reads:
   - the launch log in `%LOCALAPPDATA%\ModderLords\logs`
   - `ModderLords.Compat-server.log` / `-client.log`
   - TAOM's client log at `<game>\bin\Win64_Shipping_Client\Logs\taom_debug_*.log`, looking for `[Possession]`, `[SpecRes]` and `CareerCreation`
   - `Coop_client.log` and `Coop_server.log` (open with shared read)
4. Expect these lines:
   - `coop-detection on: … active (CoopNightly)`
   - `server-binary on`
   - server: `join-grant: applied TAOM's character-creation package to '<hero>'`
   - gold that includes the culture bonus, not doubled
5. Fix whatever it shows, then get the maintainer to merge #107, #108 and #109.

### Step 2: code work that doesn't need a test (from `docs/TAOM-MAP.md`)
- **P2 relays** (TAOM declines these on clients). Pattern: a client prefix on TAOM's refusal point sends a request, and the server runs TAOM's code for that player under `PlayerScope`.
  - Elite emissary: `EliteEmissaryInquiryPresenter.ExecutePurchase`. CoopCompat reference: `EmissaryPurchaseProtocol`.
  - Messengers: the send path.
  - Siege-defence accept.
  - Career-quest start: client-side `MBObjectBase` creation (P0.6).
- **P3 TAOM state to clients.** Prototype a generic `SyncData` mirror: the server serialises a chosen TAOM behaviour's `SyncData` and sends it, and clients load it through the same `SyncData`. Start with War of the Ring and Momentum (phase and meter), then special-resource balances and careers.
- **P4 per-player context on the dedicated server.** Extend `ServerPlayerChecks`/`PlayerComparisonRewriter` to TAOM's `IPlayerContextAdapter`, so War of the Ring participation, siege defence and careers see each player, not the idle world-gen hero.
- **P5 settings parity.** Confirm MCM settings sync covers `TaomSettings` (243 settings, 180 affecting the simulation), and compare TAOM's `SettingsFingerprint` log lines on both sides.
- **P1 guards.** Add these in the order live logs show them. CoopCompat's list is the checklist: null guards, nested kingdom decisions, battle spawn.
- **P0.4 Armory on server.** Reconcile the stale "LOTRLOME_Armory Broken on server" compat-db warning with `TaomLaunchPolicy`, which requires the Armory.
- **P6 missions.** Read Coop's `Missions` assembly to learn how battles split between server and clients, before touching TAOM mission logic.

### Housekeeping
- Update `docs/TAOM-LAYER-PLAN.md` (#107) decision 2 to the "inside ModderLords.Compat" design.
- Bump the module version (`SyncSubModule.Version` + `_Module/SubModule.xml`) on every client-visible change.
- Rules: one PR per change, branched from `origin/main`, never stacked. Changes that depend on #109 wait for it to merge or go into #109 while it's open.

## Verification
- Every PR: `dotnet build ModderLords.slnx`, then `dotnet test` on Core and App tests. Log test output to a file first; piping it into `Select-Object` kills the run.
- Every batch: one short live checklist like Step 1. The agent reads the logs listed there.
- After server-side changes, also smoke-test a vanilla-map launch.

## Live test script (batch of 2026-09-23: modules 0.1.20 to 0.1.22)

Host a TAOM world on the new build and join as a client. After each step, the agent reads
`ModderLords.Compat-server.log` / `-client.log` for the `TAOM layer:` lines shown.

1. **Start-up.** Both logs show `party-components on`, `refuge on`, `supply-lines on` and, on the client,
   `player-switcher-guard on`.
2. **New character.** No hero picker appears on the face screen (client: `hero picker hidden`). After joining, the
   party stands at the culture's start settlement (client: `party placed at <culture>'s starting settlement`).
3. **Supply order.** In a town: Order Supplies, pick goods, confirm. The screen closes, "A supply caravan has set out"
   appears, and a caravan party shows on the map (server: `supply order ... placed for <you>`; server:
   `sent SupplyCaravanComponent`; client: `SupplyCaravanComponent attached`). Wait for it: the goods arrive in your
   inventory and gold went down once.
4. **Refuge.** Camp (field or fortified), wait until ready, camp menu: Establish a refuge here, pick a warden. The
   party screen for the refuge opens (server: `refuge '...' founded for <you>`; client: `RefugePartyComponent
   attached`). Move troops in. Wait for the raise, then walk away and back: Enter refuge shows your garrison.
   Try Dismantle: the troops return to your party.
5. **Second player** (if available): they must not see or manage your refuge, and their caravans go to them.
6. **Culture conversion** mirror: client logs `state mirror applied CultureConversionBehavior`.
7. **One battle with wargs, elephants or spiders** (for the battle review): note anything that looks doubled, or
   agents dying on one screen only.

### Additions (modules 0.1.24 to 0.1.26)
8. **Messages.** When a caravan arrives, the "supplies have arrived" line appears on YOUR screen (it used to print
   only on the server).
9. **Field Commission.** Win a battle against the odds with your own troops getting kills. Afterwards, if a troop type
   has enough merit, TAOM offers a promotion. Accept and name them: a new companion appears and one soldier leaves
   (server: `promoted to companion`). If no offer ever comes, check whether the client log shows the battle ending.
10. **Battle gate.** Fight a two-player battle with creatures. Every client log lists `battle gate: <TAOM class> ->
    Agent.<action> on an agent another machine controls`. Send me those lines; they decide what goes into
    `Documents\Mount and Blade II Bannerlord\Configs\ModLogs\taom-battle-gate-client.txt` (one class per line).
