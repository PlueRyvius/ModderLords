# Roadmap

## Done
- Phase 0: direct launch of the pristine Coop dedicated server engine.
- Phase 1: junction overlay + resolver hook; community mods load from where they live.
- Phase 2: profiles, rendered server-config.json, save headers, player mod list, WPF app.

## Phase 3 (in progress)
- Version-drift banner: a mod's SubModule.xml version changed since the last launch (players must update; a running server needs a restart).
- Re-sync junctions on demand and before every launch (survives Steam re-downloads of the workshop item).
- mod-config.json editor (difficulty, fast forward, auto pause, cheats, looter multiplier) with the same render-from-template approach as server-config.json.

## Phase 4
- Hardening: crash-safe overlay apply, log rotation, more tests, README walkthrough with screenshots.

## Future: generalized mod compatibility (idea from Andy, 2026-09-02)

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
