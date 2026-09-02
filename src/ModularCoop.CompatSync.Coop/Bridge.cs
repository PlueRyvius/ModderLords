namespace ModularCoop.CompatSync.Coop;

/// <summary>Entry points the submodule calls by reflection; keeps every Coop reference inside this assembly.</summary>
public static class Bridge
{
    public const int ProtocolVersion = 1;

    /// <summary>Server: broadcast settings objects whose values changed since the last capture. No-op elsewhere.</summary>
    public static void Tick()
        => global::Coop.Core.Server.Services.ModularCoopCompat.Handlers.ServerSettingsHandler.Current?.BroadcastChanges();
}
