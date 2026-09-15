// Fixture for AuthorityScanTests. Engine-shaped fakes live under TaleWorlds.* namespaces because the classifier decides
// "world state" and "display" by namespace; this test project references no real TaleWorlds assembly, so nothing clashes.
namespace TaleWorlds.Library
{
    public static class InformationManager { public static void DisplayMessage(string message) { } public static void ShowInquiry(object data) { } }
    public class ViewModel { public void ExecuteCommand(string command) { } }
    public sealed class InquiryData { public InquiryData(string title, Action? affirmative, Action? negative) { } }
}

namespace TaleWorlds.Core
{
    public static class MBRandom { public static float RandomFloat => 0.5f; }
    public sealed class GameStateManager { public static GameStateManager Current { get; } = new(); public void PushState(object state) { } }
}

namespace TaleWorlds.CampaignSystem.GameState
{
    public sealed class QuestsState { }
}

namespace TaleWorlds.CampaignSystem.Settlements
{
    public class Town { }
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
    public class Hero { public static Hero? MainHero => null; public Clan? Clan => null; }
    public class Clan { public static Clan? PlayerClan => null; }
    // Engine setters are not mod code; an empty body keeps the walker from following into a fake backing field.
    public static class PlayerEncounter { public static bool LeaveEncounter { get => false; set { } } }
    public static class AuthLeaveAction { public static void ApplyForParty(object? party) { } }
    public static class AuthCheats { public static bool CheckCheatUsage() => true; }
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

    // Improved Garrisons' shape: session handlers that register menus beside server work, a ViewModel whose slider
    // callback writes a setting the server's tick reads, and a tick that asks for "the" player's hero.
    // Not named *Settings: AssemblyScanTests counts settings-shaped classes in this assembly. Like IG's GarrisonSettings,
    // the constructor fills in a default, and several entry points create one; that is initialisation, not a writer.
    public sealed class AuthTownLimits { public int Max { get; set; } public AuthTownLimits() { Max = 5; } }
    public static class AuthTownStore { public static AuthTownLimits Town = new(); }
    public sealed class AuthOption { public AuthOption(Action<int> onChange) { } }

    public sealed class AuthSettingsVM : TaleWorlds.Library.ViewModel
    {
        public readonly List<AuthOption> Options = new();
        public AuthSettingsVM() { Options.Add(new AuthOption(x => AuthTownStore.Town.Max = x)); }
        public void ExecutePrompt() => _ = new InquiryData("sure?", Confirm, null);
        private void Confirm() => new AuthHero().Renown = 9;
    }

    // Improved Garrisons' "Order to patrol": the button's lambda reads screen state (the selected town) and calls a
    // behaviour method that takes only the town. That method is where the action can be relayed.
    public sealed class AuthGuardOrders : CampaignBehaviorBase
    {
        public override void RegisterEvents() { }
        public override void SyncData(object store) { }
        public void OrderPatrol(TaleWorlds.CampaignSystem.Settlements.Town town) => new AuthHero().Renown = 4;
    }

    public sealed class AuthGuardsVM : TaleWorlds.Library.ViewModel
    {
        public static TaleWorlds.CampaignSystem.Settlements.Town? SelectedTown;
        private static readonly AuthGuardOrders Orders = new();
        public readonly List<AuthOption> Buttons = new();
        public AuthGuardsVM() { Buttons.Add(new AuthOption(_ => Orders.OrderPatrol(SelectedTown!))); }
    }

    public sealed class AuthSplitBehavior : CampaignBehaviorBase
    {
        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, Split);
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, Mixed);
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, NonVoid);
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, Shared);
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, PlayerMenus);
            CampaignEvents.DailyTickPartyEvent.AddNonSerializedListener(this, TickSettings);
            CampaignEvents.DailyTickPartyEvent.AddNonSerializedListener(this, TickHost);
            CampaignEvents.DailyTickPartyEvent.AddNonSerializedListener(this, TickResetA);
            CampaignEvents.DailyTickPartyEvent.AddNonSerializedListener(this, TickResetB);
            CampaignEvents.DailyTickPartyEvent.AddNonSerializedListener(this, TickOffer);
            CampaignEvents.DailyTickPartyEvent.AddNonSerializedListener(this, TickOwner);
        }
        public override void SyncData(object store) { }

        private void Split(CampaignGameStarter s) { AddSettingsMenu(s); SetupParties(); }
        private void AddSettingsMenu(CampaignGameStarter s) => s.AddGameMenuOption("town", "settings", "t", null, OpenSettings);
        private void OpenSettings() => GameStateManager.Current.PushState(new object());
        private void SetupParties() => AuthGoldAction.ApplyBetweenCharacters(null, null, 1);

        private void Mixed(CampaignGameStarter s)
        {
            s.AddGameMenuOption("town", "mixed", "t", null, null);
            AuthGoldAction.ApplyBetweenCharacters(null, null, 1);
        }

        private void NonVoid(CampaignGameStarter s) { s.AddPlayerLine("a", "b", "c", "t", null, null); _ = CountParties(); }
        private int CountParties() { AuthGoldAction.ApplyBetweenCharacters(null, null, 1); return 1; }

        private void Shared(CampaignGameStarter s) { s.AddGameMenuOption("town", "shared", "t", null, () => SharedWork()); SharedWork(); }
        private void SharedWork() => AuthGoldAction.ApplyBetweenCharacters(null, null, 2);

        private void PlayerMenus(CampaignGameStarter s)
        {
            s.AddGameMenuOption("town", "leave", "t", null, LeaveTown);
            s.AddGameMenuOption("town", "cheat", "t", null, Cheat);
            s.AddGameMenuOption("town", "quests", "t", null, OpenQuests);
            s.AddGameMenuOption("town", "bye", "t", null, Bye);
        }
        private void LeaveTown() => AuthLeaveAction.ApplyForParty(null);
        private void Cheat() => _ = AuthCheats.CheckCheatUsage();
        private void OpenQuests() => GameStateManager.Current.PushState(new TaleWorlds.CampaignSystem.GameState.QuestsState());

        private void TickSettings(object? party) { if (AuthTownStore.Town.Max > 0) AuthGoldAction.ApplyBetweenCharacters(null, null, AuthTownStore.Town.Max); }
        private void TickResetA(object? party) => _ = new AuthTownLimits();
        private void TickResetB(object? party) => _ = new AuthTownLimits();
        private void TickOffer(object? party) { InformationManager.ShowInquiry("join?"); AuthGoldAction.ApplyBetweenCharacters(null, null, 1); }
        private void Bye() => PlayerEncounter.LeaveEncounter = true;
        private void TickOwner(object? party) { if (OwnerOf(party) == Hero.MainHero) AuthGoldAction.ApplyBetweenCharacters(null, null, 1); }
        private static Hero? OwnerOf(object? party) => null;
        private void TickHost(object? party) { if (Hero.MainHero != null) AuthGoldAction.ApplyBetweenCharacters(null, null, 1); }
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
