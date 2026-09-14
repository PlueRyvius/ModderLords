// Fixture for AuthorityScanTests. Engine-shaped fakes live under TaleWorlds.* namespaces because the classifier decides
// "world state" and "display" by namespace; this test project references no real TaleWorlds assembly, so nothing clashes.
namespace TaleWorlds.Library
{
    public static class InformationManager { public static void DisplayMessage(string message) { } }
    public class ViewModel { public void ExecuteCommand(string command) { } }
}

namespace TaleWorlds.Core
{
    public static class MBRandom { public static float RandomFloat => 0.5f; }
}

namespace TaleWorlds.CampaignSystem
{
    public class AuthHero { public int Gold; public int Renown; }
    public static class AuthGoldAction
    {
        public static void ApplyBetweenCharacters(AuthHero? giver, AuthHero? receiver, int gold) { }
        public static void ApplyInternal() { }
    }
    public class AuthVanillaBehavior { public void RegisterEvents() { } public void UpgradeReadyTroops() { } }
    public class AuthVanillaAlliance { public void StartAlliance() { } }
    public class AuthPartyWageModel { public virtual int GetWage() => 0; }
    public class AuthTownVisit { public void game_menu_recruit_on_consequence() { } }
}

namespace ModderLords.Core.Tests.AuthFakes
{
    using TaleWorlds.CampaignSystem;
    using TaleWorlds.Core;
    using TaleWorlds.Library;
    using ModderLords.Core.Tests.ModFakes;

    public static class AuthLog { public static int Dropped; }

    public interface ISession { bool IsAuthority { get; } }
    public sealed class Session : ISession { public bool IsAuthority => true; }

    public sealed class AuthBehavior : CampaignBehaviorBase
    {
        public static int Momentum;
        public static int[] Slots = new int[3];
        private readonly ISession _session = new Session();

        public override void RegisterEvents()
        {
            CampaignEvents.DailyTickPartyEvent.AddNonSerializedListener(this, TickGold);
            CampaignEvents.DailyTickPartyEvent.AddNonSerializedListener(this, TickSynced);
            CampaignEvents.DailyTickPartyEvent.AddNonSerializedListener(this, TickRandom);
            CampaignEvents.DailyTickPartyEvent.AddNonSerializedListener(this, TickGuarded);
            CampaignEvents.DailyTickPartyEvent.AddNonSerializedListener(this, TickMessage);
            CampaignEvents.DailyTickPartyEvent.AddNonSerializedListener(this, TickMomentum);
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, Menus);
        }
        public override void SyncData(object store) { }

        // AuthLog.Dropped is plumbing: written by three entry points, read by a menu consequence.
        private void TickGold(object? party) { AuthLog.Dropped++; AuthGoldAction.ApplyBetweenCharacters(null, null, 5); }
        private void TickSynced(object? party) { AuthLog.Dropped++; new AuthHero().Gold = 5; }
        private void TickRandom(object? party) { AuthLog.Dropped++; _ = MBRandom.RandomFloat; }
        private void TickGuarded(object? party) { if (_session.IsAuthority) AuthGoldAction.ApplyBetweenCharacters(null, null, 1); }
        private void TickMessage(object? party) => InformationManager.DisplayMessage("hello");
        private void TickMomentum(object? party) => Momentum++;

        private void Menus(CampaignGameStarter starter)
        {
            starter.AddGameMenuOption("town", "buy_renown", "text", ShowMomentum, BuyRenown);
            starter.AddGameMenuOption("town", "say_hi", "text", null, SayHi);
        }
        private bool ShowMomentum() => Momentum > 0;
        private void BuyRenown() => new AuthHero().Renown = 3;
        private void SayHi() { _ = AuthLog.Dropped; InformationManager.DisplayMessage("hi"); }

        public static void FillSlots() => Slots[0] = 1;
    }

    [CoopFakes.HarmonyPatch(typeof(AuthVanillaBehavior), "UpgradeReadyTroops")]
    internal static class AuthUpgradePatch
    {
        [HarmonyPostfix]
        private static void Shed() => new AuthHero().Gold = 1;
    }

    [CoopFakes.HarmonyPatch(typeof(AuthVanillaAlliance), "StartAlliance")]
    internal static class AuthAlliancePatch
    {
        [HarmonyPostfix]
        private static void After() { }
    }

    [CoopFakes.HarmonyPatch(typeof(AuthPartyWageModel), "GetWage")]
    internal static class AuthWagePatch
    {
        [HarmonyPostfix]
        private static void Wage(ref int __result) { __result = 2; }
    }

    // base.OnAgentRemoved() is a plain call: it must not reach AuthMissionB's override.
    public sealed class AuthMissionA : MissionLogic { public override void OnAgentRemoved() { base.OnAgentRemoved(); } }
    public sealed class AuthMissionB : MissionLogic { public override void OnAgentRemoved() => new AuthHero().Renown = 1; }

    // "ViewModel" ends in "Model"; its commands are still player input, not a query.
    [CoopFakes.HarmonyPatch(typeof(TaleWorlds.Library.ViewModel), "ExecuteCommand")]
    internal static class AuthCareerPatch
    {
        [HarmonyPostfix]
        private static void Open() => new AuthHero().Renown = 2;
    }

    [CoopFakes.HarmonyPatch(typeof(AuthTownVisit), "game_menu_recruit_on_consequence")]
    internal static class AuthRecruitPatch
    {
        [CoopFakes.HarmonyPrefix]
        private static bool Swap()
        {
            new AuthHero().Renown = 1;
            return true;
        }
    }
}
