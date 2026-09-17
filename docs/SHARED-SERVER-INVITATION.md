# Shared server invitation proposal

Status: design proposal from the 2026-09-17 cleanup discussion; not implemented or an acceptance claim.

Extend the existing ModListFile import/export model into a server invitation. The file should describe the selected launch profile: Workshop published-file IDs, module identities and load order, required configuration fingerprints, game/Coop/provider fingerprints, compatibility-plan digest, and a server address with explicit port or supported Steam lobby identity. Do not distribute mod binaries or copy local absolute paths. Keep host credentials out of ordinary exports; a protected server can ask for its password when joining.

Suggested flow: host exports invitation; recipient opens it in Modderlords; launcher shows server and required subscriptions; recipient chooses Install missing mods; Steam handles subscriptions/downloads; launcher waits for actual installed files, resolves dependencies and compares fingerprints; Join Coop launches the matched profile targeting that server. The invitation creates/selects a separate profile and must not change an active session's frozen plan. Do not unsubscribe unrelated items or enable every item the user happens to subscribe to.

Steam ISteamUGC.SubscribeItem provides asynchronous subscription/download scheduling. Whether the standalone launcher can initialize the necessary Bannerlord Steam context must be validated before promising a one-click implementation. Opening a Workshop collection is a possible assisted fallback. A subscription success callback is not installation readiness. Check download/update state, installed files and the expected content before allowing a managed join.

Subscriptions help acquire the same mods but do not pin an old release, preserve local edits, supply private/unavailable items, establish load order, or synchronize relevant settings. Mismatches should identify the item and required action. The invitation cannot authorize an unshipped compatibility adapter or suppress unresolved analysis findings.

Current code foundation: src/ModderLords.Core/Export/ModListFile.cs carries Workshop source links, ordered module versions and roles; ClientManifest.cs identifies Workshop origins. Current launcher code has no implemented automatic Workshop subscription or invitation-to-Coop connection flow. Keep server selection separate from the fixture-only connection driver. Use an explicit port so a manual default of 4200 cannot silently replace the invitation endpoint.

Reference: https://partner.steamgames.com/doc/api/ISteamUGC?l=english#SubscribeItem
