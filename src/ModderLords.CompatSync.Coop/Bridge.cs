namespace ModderLords.CompatSync.Coop;

/// <summary>Entry points the submodule calls by reflection; keeps every Coop reference inside this assembly.</summary>
public static class Bridge
{
    public const int ProtocolVersion = 1;

    /// <summary>Server: broadcast settings objects whose values changed since the last capture. No-op elsewhere.</summary>
    public static void Tick()
    {
        global::Coop.Core.Server.Services.ModderLordsCompat.Handlers.ServerSettingsHandler.Current?.BroadcastChanges();
        global::Coop.Core.Client.Services.ModderLordsCompat.Handlers.ClientSettingsHandler.Current?.ApplyPending();
        EncounterOptionGate.EnsureInstalled();
        EncounterOptionGate.Tick();
        BehaviorGate.RetryPendingPostfixes();
    }

    /// <summary>Client, every quarter second: sends relayed player actions whose control has settled. Cheap when none are waiting.</summary>
    public static void RelayTick() =>
        global::Coop.Core.Client.Services.ModderLordsCompat.Handlers.ClientSettingsHandler.Current?.FlushRelays();

    /// <summary>The verification counter line for this side (server should count gated behaviours, a client should stay at 0).</summary>
    public static string VerificationSummary() => BehaviorGate.VerificationSummary();
}
