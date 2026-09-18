# Root compatibility plan (draft, 2026-09-02)

Goal: make single-player mods work under Coop by fixing the seams every mod hits, not by writing an
adapter per mod. First cut = "loads and runs on the server without crashing"; second cut = server-authoritative mod
state through Coop's own sync framework. A single shared client module is acceptable. All classes of mods matter.

## What CoopModPatch really does (decompiled survey, 4 adapters)

Every adapter is the same skeleton: find `GameInterface.ContainerProvider.TryGetContainer` and `Common.ModInformation.IsServer`
by reflection, poll for a session, resolve `INetwork` + `IMessageBroker`, then hand-roll a request/push/broadcast cycle for
one mod's state, plus a few Harmony patches:

| Adapter | Patches | Custom state it ships |
|---|---|---|
| ImprovedGarrisons | StoryMode-free load path, skip main-menu/ribbon UI on the host, `InformationManager` routing | town settings, party ledger, config push |
| MBOptionScreen (MCM) | filter the MCM options list on clients | every registered settings object, serialised property by property |
| MyLittleWarband | register generated troop objects with Coop's `MBObjectBasePatches`, recruitment postfixes, save replay | troop-tree ledger |
| SettlementCultureDrift | `SkipOnCoopClientPrefix` on `OnDailyTick`, broadcast postfix, transpiler rerouting culture writes | culture record table |

Plus a `ServerSetup` class that only exists to edit Hex's policy files and mirror DLLs into the server bin. With the
junction overlay and the resolver hook that class is unnecessary.

## Coop's real extension seams (verified in source, development branch, 2026-09-02)

1. **Object registry is open to any assembly.** `GameInterface.Registry.RegistryModule.GetRegistries()` scans
   `AppDomain.CurrentDomain.GetDomainTypes()` for concrete `IAutoRegistry<T>` implementers and auto-activates them in
   the container. A class in *our* assembly deriving `AutoRegistryBase<T>` for a mod-owned type T gets: constructor
   prefixes that assign network ids and replicate creation to clients (`LifetimePatches<T>`), destroy postfixes,
   `RegisterAllObjects` on join with id remapping. Nothing to patch; Coop finds it.
2. **Field/property sync is data-driven.** `AutoSyncRegistry.AddField / AddProperty / AddSerializer` (public) drive a
   Roslyn build (`AutoSyncBuilder.Build`) that generates setter transpilers and network messages per member. Coop only
   scans its own assembly for `IAutoSync` declaration classes, so registrations from outside must be added to the
   resolved `AutoSyncRegistry` before `AutoSyncBuilder.Build()` runs (one Harmony prefix on that public method, or a
   maintainer-blessed hook). The generated code handles value types, references to registered objects, lists,
   dictionaries, queues, MBList, arrays, read-only fields.
3. **Server-authority gate is one predicate.** Every Coop patch stands down when
   `CallOriginalPolicy.IsOriginalAllowed()` is true: inside an `AllowedThread` scope, or when `ISyncPolicy.AllowOriginal()`
   says so (server side). Mod code running on the server therefore replicates naturally through Coop's existing
   patches as long as it runs *outside* `AllowedThread` (Coop's own rule for its server logic).
4. **Public plumbing**: `ContainerProvider.TryResolve<T>`, `IMessageBroker`, `INetwork`, `IObjectManager`,
   `IPlayerManager`, `ModInformation.IsServer/IsClient/BuildVersion`. ModularSmithing2's adapter already probes 15 of
   these by name and degrades gracefully when one is missing; that probe pattern is the template.

## Design: `ModderLords.Compat`

One module, one assembly, enabled on server and on every client (version-matched by Coop's validator like any mod).
Loaded on the server through the overlay like anything else. Contains no Coop code; binds to the seams above by name
with a compat probe so a Coop update degrades to "compat disabled" instead of a crash.

### Layer 0: run without crashing (server-only, first cut)

Harmony patches on TaleWorlds entry points, active only when `StartupInfo.DedicatedServerType != None`:

- `InformationManager.ShowInquiry / ShowMultiSelectionInquiry / DisplayMessage / AddQuickInformation`: log and
  auto-resolve (Coop's own core already auto-accepts inquiries; extend to the rest).
- `ScreenManager.PushScreen / PopScreen`, `GauntletLayer` construction, `MapScreen.Instance` readers: no-op / null-safe.
- UI game states requested by mods: ignored with a log line.
- `StoryMode` / `CustomBattle` type references: a tiny reference-only shim assembly containing the public type names
  mods bind to (empty behaviours), loaded by the resolver hook only when the real assembly is absent. Removes the
  "could not load type from StoryMode" class of failures without shipping TaleWorlds code.
- Diagnostic: every guard hit is logged once per mod so the console shows why a mod is quiet on the server.

Launcher side: an assembly scan at overlay time (IL metadata only, no execution) lists per mod which of these seams it
touches, feeding a "server-safe / guarded / needs review" badge in the Mods tab.

### Layer 1: behaviours run on the server only (both sides)

Generic rule engine driven by a per-mod JSON recipe (shipped in a compat database, editable in the app):

- `CampaignBehaviorBase` event handlers listed in the recipe (`OnDailyTick`, `OnHourlyTick`, `OnSettlementEntered`, ...)
  get `return ModInformation.IsServer` prefixes: clients stop simulating, the server's effects replicate through Coop's
  existing object sync (parties, heroes, settlements, gold, rosters).
- `IDataStore.SyncData` on clients becomes read-only for the listed behaviours (the host's save is the truth).

Auto-derivable defaults: any behaviour whose handlers only mutate registered game objects (parties, clans, settlements,
heroes, item rosters) needs nothing beyond the prefix. The IL scan can find those.

### Layer 2: mod-owned state through Coop's sync (both sides)

- **Settings (Layer 2a, shipped v0.6/v0.7)**: one generic bridge with two sources behind `ISettingsSource`
  (`src/ModderLords.CompatSync`): `McmSettingsSource` (MCM v5 `SettingsDefinitions` by reflection, MCM's own
  SettingsIds) and `StaticSettingsSource` (plain settings classes in community mod assemblies, ids
  `static:<Assembly>:<Type>`). Discovery rule for the static source, mirrored by the launcher's IL scan
  (`AssemblyScan.SettingsClasses`): a non-generic class whose name ends in Settings/Setting/Config/Configs/
  Configuration/Options, or that has a public static `Instance|Current|Settings|Config|Default` of its own type;
  reachable through static members, that singleton, or one hop `Holder.Instance.<Config|Settings|Configuration|
  Options|Current>`; public settable bool/number/string/enum members; excluding ViewModel/behaviour/MBSubModuleBase/
  ScreenBase/GameModel bases, MCM-derived types (incl. generic bases), `[SaveableClass]` types, any namespace segment
  SaveData/Saveable/Serialization, namespace tails Data/DataTypes, names containing Template/Snapshot/Dto/ViewModel/VM,
  and singleton-only types ending Manager/UI/Screen/Widget/Behavior/Handler/Service/Controller/Patch. Non-auto
  singleton getters are read only once a campaign exists (or after 60 s). Hints: `CompatRecord.SettingsTypes` /
  `IgnoreSettingsTypes` → `recipes.json` `Mods[].Settings.Include/Exclude` → `SettingsHints`. Persist: MCM
  `SaveSettings`, else a zero-arg Save*/Write*/Store*/Persist*/Serialize*/Flush* or Create*/Update*Config* method on
  the object, its holder or the holder type. Wire: unchanged `NetworkSettingsSnapshot` (id + payload); clients keep
  snapshots for objects not created yet and apply them from the tick; a new `Campaign.Current` re-applies. Host
  overrides: `<profile>.settings.json` staged as `overrides.json` in the live dir, applied by `Overrides` on the
  server at start and on campaign change, acked in `ack-overrides.json`. Not done: marking options read-only in the
  client MCM UI; per-save (in-save) settings data, which is Layer 2 state, not settings.
- **Mod-owned objects**: for each type in the recipe, a generated `AutoRegistryBase<T>` (id = recipe-declared key,
  e.g. `StringId` or a field) so instances created on the server appear on clients with stable network ids. Replaces
  MyLittleWarband's hand-written registry patch.
- **Mod-owned fields**: recipe lists `Type.Field` pairs; registered into `AutoSyncRegistry` before the AutoSync build
  so setter writes on the server replicate to clients through Coop's generated messages. Replaces Drift's transpiler and
  IG's state broadcast for plain fields.
- **Collections keyed by registered objects** (per-town settings, per-party ledgers): supported by AutoSync's dictionary
  builders when key and value types are registered or by-value.

What stays per-mod: logic that must run differently on client vs server (MyLittleWarband's save replay). Those remain
small adapters, but written against the Compat API instead of raw Coop internals.

### Layer 3: compatibility database

Shipped (v0.6): one file, `compat-db.json` next to the launcher, `{ SchemaVersion, Records[] }`. Each record: `Id`,
`Verdict` (Works / NeedsRecipe / Broken / Unknown), `TestedVersions`, `TestedCoopVersion`, the launcher defaults for a
mod new to a profile (`DefaultRole`, `ServerAuthoritative`, `ClientSideBehaviors`), `KeepSubModules` (submodule classes
that survive DependencyOnly; MCM's settings core), `Notes`, `Url`, `UpdatedAt`. The user's own records live in
`%LOCALAPPDATA%\ModderLords\compat-db.local.json` with the same shape; a local record replaces the bundled one whole, by
id. `Record…` on the Mods tab writes a local record; `Export…` / `Import…` share them (import keeps whichever record has
the newer `UpdatedAt`). `ModderLords.Core.Compat.CompatDb` replaced the hardcoded role and keep-submodule tables. The
recipe itself (guards, synced fields, settings classes from Layer 2) is not in the record yet; the behaviours list is.

`ClientLoadsAfterCoop` records that a mod patches Coop and must load after it **on a player's machine**. It exists
because no manifest carries the fact: CoopMarriage depends only on Harmony, ButterLib, UIExtenderEx, MCM and the
official modules, so `ModuleSorter` cannot place it, and loading it before Coop makes its `TargetMethod()` return
null, `PatchAll` throw out of `OnSubModuleLoad`, and Bannerlord die at startup with `0xE0434352`. `CompatDb.ClientFollowsCoop()`
feeds the flagged ids to `LoadOrder.Compute` as `knownToFollowCoop`; the server pins Coop after the community block
regardless, so the flag only ever moves a client order. Add one for any mod whose submodule binds to Coop's assemblies
during `OnSubModuleLoad` — a mod that defers that binding to a later hook (ModularSmithing2 retries at
`OnBeforeInitialModuleScreenSetAsRoot`) is order-independent and does not need it.

## Order of work

1. Layer 0 guards + IL scan + badges (server-only, no client module yet). Verify with ImprovedGarrisons and HealOnKill
   running without CoopModPatch.
2. Client module skeleton with the compat probe; MCM settings bridge (Layer 2a). Verify: server MCM values reach clients.
3. Layer 1 recipe engine; recipes for IG and Drift. Verify: CoopModPatch disabled, both mods behave the same.
4. Layer 2b/2c registry + field sync through AutoSync. Verify with MyLittleWarband troops appearing on clients.
5. Database + UI.

## Risks and boundaries

- Coop is source-available; binding to its public types from a separate module is interop, but step 2 onward should be
  raised with the Coop team on Discord first (Hex and the DedicatedServer repo are explicitly authorised projects in
  their AGENTS.md; we would want the same standing). Nothing in this plan copies or redistributes their code.
- The AutoSync registration hook is the one place we would touch Coop internals at runtime (a prefix on
  `AutoSyncBuilder.Build`). Everything else uses public interfaces, and the probe disables compat rather than crash when a
  seam moves.
- Field sync on hot setters can flood the network (Coop excludes `Clan.CurrentTotalStrength` for that reason); recipes
  need a coalesce flag and the console should show message rates per synced member.
