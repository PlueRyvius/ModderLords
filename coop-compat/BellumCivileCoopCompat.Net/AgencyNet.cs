using System;

namespace BellumCivileCoopCompat.Net
{
    /// <summary>The network part's entry: the module calls Init when it loads it; Coop's handlers attach when a session starts.</summary>
    public static class AgencyNet
    {
        private static IAgencyChannel _attached;

        public static void Init() => SubModuleLog("Agency: network part initialised (Coop's messaging).");

        public static void Attach(IAgencyChannel channel, string side)
        {
            _attached = channel;
            Agency.Channel = channel;
            SubModuleLog("Agency: the " + side + " channel is open (protocol " + Agency.Protocol + ").");
        }

        public static void Detach(IAgencyChannel channel)
        {
            if (!ReferenceEquals(_attached, channel))
                return;
            _attached = null;
            Agency.Channel = null;
        }

        public static void Fail(string where, Exception ex) =>
            SubModuleLog("Agency: the " + where + " channel failed: " + ex.GetBaseException().Message);

        // SubModule.Log is internal to the module; reach it by reflection so this assembly needs nothing more.
        private static void SubModuleLog(string text)
        {
            try { typeof(Agency).Assembly.GetType("BellumCivileCoopCompat.SubModule")?.GetMethod("Log", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.Invoke(null, new object[] { text }); }
            catch { }
        }
    }
}
