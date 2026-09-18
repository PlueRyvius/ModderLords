# Roadmap

## Done

**As Modular Bannerlords Coop (v0.1 - v0.8.7)** — a launcher for the Bannerlord Coop dedicated server:

- Direct launch of the pristine Coop dedicated server engine; no patched server files.
- Junction overlay plus a resolver hook, so community mods load from where they already live.
- Profiles, rendered `server-config.json`, save headers, the player mod list, and the WPF app.
- The version-drift banner, on-demand junction re-sync, and the `mod-config.json` editor.
- Hardening: crash-safe overlay apply, log rotation, preflight checks, integration tests.
- Arbitrary-mod compatibility work: server guards, the compatibility database, server-only logic
  (Layer 1), and host-side mod settings, live or offline.

**The ModderLords pivot (v0.9.0)** — the same project generalised into a mod loader, because what people
actually praised was the mod ordering and loading, not the coop:

- **Phase 0** — renamed throughout, including both in-game module ids. Licence relaxed from PolyForm Strict to
  PolyForm Noncommercial. Profiles migrate from `%LOCALAPPDATA%\ModularCoop` on first run.
- **Phase 1** — `ClientLaunchPlan` / `ClientLaunchSession`: launching the player's own game with a profile's mods,
  via the module token on the command line. No overlay, no hook, no compat module on the player path.
- **Phase 2** — `ModderLords.Coop` split out of `ModderLords.Core`, with the compiler enforcing that Core cannot
  reach the server code. CI now builds the whole app.
- **Phase 3** — Player and Host mode, chosen on first run and switched from the toolbar. Player mode is the mod
  loader and shows nothing about dedicated servers. The host-only commands moved to `HostViewModel`, built only in
  Host mode. Mod-order editing reworked: three bands (frameworks, the game's modules, everything else),
  drag-to-reorder, and one row per installed version so you choose which copy loads.
- **Phase 4** — this document and the README reframed around the mod loader; v0.9.0 released.

## Next

Nothing is committed to. The most likely candidates, roughly in order of how often they have come up:

- **Mods tab niceties**: a right-click column chooser, and an About window.
- **A diagnostics zip** for bug reports (redacting `ServerSettings.Password`).
- **Templated scrollbars.** They are still the WPF default, so they read as light chrome in the dark theme.
- **The CoopNightly ordering asymmetry.** The client sorts it early by dependency while the server pins it last;
  `LauncherDataSync` deliberately never moves the Coop entry.
- **Generalized mod compatibility** — the long-standing idea below, still the most interesting direction for the
  hosting half.

## Future: generalized mod compatibility

CoopModPatch shows that a mod never written for co-op can be made to work with targeted server-side fixes
(ImprovedGarrisons: a StoryMode-free load path, skipping main-menu/ribbon UI on the headless host, syncing its
settings to clients). Most of those fixes fall into a handful of recurring shapes. The launcher is in a good position to
apply generic versions of them without per-mod code, because it already rewrites manifests and sits between the engine
and every assembly load:

1. **Headless guards.** Many crashes are UI-only code running on a server that has no screen: `Gauntlet`/`ViewModel`
   layers, `InformationManager` inquiries, `ScreenManager` pushes. A hook-level rule set (Harmony prefixes that no-op
   well-known UI entry points when `DedicatedServerType != None`) would cover a large class of mods at once.
2. **StoryMode / CustomBattle absence.** Mods that hard-depend on StoryMode types crash on the server. Detect the
   reference at overlay time (assembly metadata scan), warn, and where the dependency is only a behavior registration,
   supply an empty shim assembly with the expected type names so loading succeeds.
3. **Settings sync.** MCM-backed settings are per machine; the server's values should win. A generic "read MCM settings on
   the server, push a snapshot to joining clients" bridge is what CoopModPatch does for two mods by hand; it could be
   done once for any MCM v5 settings class by reflection.
4. **Save-data behaviors.** Mods with `CampaignBehaviorBase.SyncData` state need their data to exist on the host only;
   client copies should be read-only mirrors. Detecting behaviors that write in `OnDailyTick` and auto-restricting them to
   `ModInformation.IsServer` is plausible with a Harmony patch driven by a per-mod allow/deny list.
5. **Compatibility database.** Record per mod id + version: loads headless? needs DependencyOnly? known crash signature?
   fix applied? Ship the list with the launcher and let the community contribute entries; the Mods tab would show a
   "known good / needs shim / known broken" badge before anyone launches.

Boundaries: none of this may modify or bundle Coop's own code (source-available licence); all of it lives in the hook
and the overlay, opt-in per mod, with the effect visible in the console.
