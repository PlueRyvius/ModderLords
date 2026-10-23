namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>Fourberie's Coop compat: ModderLords.Compat calls <see cref="Register"/> once, when this assembly loads.</summary>
public static class CoopCompatPlugin
{
    public static void Register()
    {
        CompatPlugins.Ticks.Add(("Fourberie.FourberieLayer.Tick", FourberieLayer.Tick));
        CompatPlugins.Ticks.Add(("Fourberie.FbBooks.ServerTick", FbBooks.ServerTick));
        CompatPlugins.Ticks.Add(("Fourberie.FbMirrorClient.ClientTick", FbMirrorClient.ClientTick));
        CompatPlugins.Ticks.Add(("Fourberie.FbEffects.ClientTick", FbEffects.ClientTick));
        CompatPlugins.Summaries.Add(FourberieLayer.Summary);
        CompatPlugins.ClientCampaignReady.Add(FbMirrorClient.CampaignReady);
        CompatPlugins.ClientSessionEnded.Add(FbMirrorClient.SessionEnded);
    }
}
