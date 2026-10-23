namespace ModderLords.CompatSync.Coop.LivingEconomy;

/// <summary>Living Economy's Coop compat: ModderLords.Compat calls <see cref="Register"/> once, when this assembly loads.</summary>
public static class CoopCompatPlugin
{
    public static void Register()
    {
        CompatPlugins.Ticks.Add(("LivingEconomy.LivingEconomyLayer.Tick", LivingEconomyLayer.Tick));
        CompatPlugins.Ticks.Add(("LivingEconomy.LeStateMirror.ServerTick", LeStateMirror.ServerTick));
        CompatPlugins.Summaries.Add(LivingEconomyLayer.Summary);
        CompatPlugins.StateFeeds.Add(new StateFeed("Living Economy", LeStateMirror.Prefix)
        {
            ClientApply = LeStateMirror.ClientApply,
            Reset = LeStateMirror.Reset,
            CaptureForJoin = LeStateMirror.CaptureForJoin,
            SetBroadcast = send => LeStateMirror.Broadcast = send,
            SetRequestFull = ask => LeStateMirror.RequestFull = ask,
        });
    }
}
