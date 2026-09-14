// Fixture for ModCodeModelTests: a mod-shaped slice compiled into this test assembly. The analysis matches engine and
// Harmony types by simple name, so these fakes stand in for TaleWorlds and HarmonyLib.
namespace ModderLords.Core.Tests.ModFakes
{
    public sealed class FakeEvent<T> { public void AddNonSerializedListener(object owner, Action<T> handler) { } }
    public static class CampaignEvents
    {
        public static FakeEvent<object?> DailyTickPartyEvent { get; } = new();
        public static FakeEvent<CampaignGameStarter> OnSessionLaunchedEvent { get; } = new();
    }
    public sealed class CampaignGameStarter
    {
        public void AddGameMenuOption(string menu, string option, string text, Func<bool>? condition, Action? consequence) { }
        public void AddPlayerLine(string id, string input, string output, string text, Func<bool>? condition, Action? consequence) { }
    }
    public abstract class CampaignBehaviorBase { public abstract void RegisterEvents(); public abstract void SyncData(object store); }
    public class DefaultPartyWageModel { public virtual int GetCharacterWage(object character) => 0; }
    public abstract class MissionLogic { public virtual void OnAgentRemoved() { } }
    public abstract class ViewModel { }
    public abstract class MBSubModuleBase { protected virtual void OnSubModuleLoad() { } }
    public sealed class HarmonyPostfix : Attribute { }
    public sealed class Harmony { public void Patch(object? original, object? prefix = null, object? postfix = null, object? transpiler = null, object? finalizer = null) { } }
    public sealed class HarmonyMethod { public HarmonyMethod(Type type, string name) { } }

    public class Roster { public int Count; public void AddToCounts(int n) { Count += n; } }
    public interface IShedService { void Shed(Roster roster); }
    public sealed class ShedService : IShedService { public void Shed(Roster roster) => roster.AddToCounts(-1); }
    public static class Services { public static IShedService Shed = new ShedService(); }

    public sealed class ModBehavior : CampaignBehaviorBase
    {
        public override void RegisterEvents()
        {
            CampaignEvents.DailyTickPartyEvent.AddNonSerializedListener(this, OnDailyTickParty);
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, AddMenus);
        }
        public override void SyncData(object store) { }
        private void OnDailyTickParty(object? party) => Services.Shed.Shed(new Roster());
        private void AddMenus(CampaignGameStarter starter)
        {
            starter.AddGameMenuOption("town", "mod_option", "text", MenuCondition, MenuConsequence);
            starter.AddPlayerLine("line", "start", "close", "hello", null, () => Services.Shed.Shed(new Roster()));
        }
        private bool MenuCondition() => true;
        private void MenuConsequence() { }
    }

    public sealed class ModWageModel : DefaultPartyWageModel { public override int GetCharacterWage(object character) => 1; }

    [CoopFakes.HarmonyPatch(typeof(DefaultPartyWageModel), "GetCharacterWage")]
    internal static class WagePatch
    {
        [HarmonyPostfix]
        private static void Tweak(ref int __result) { __result++; }
    }

    public sealed class ModMission : MissionLogic { public override void OnAgentRemoved() { } }

    public sealed class ModVM : ViewModel { public void ExecuteDone() { } public void Refresh() { } }

    public sealed class ModSubModule : MBSubModuleBase
    {
        protected override void OnSubModuleLoad()
        {
            var harmony = new Harmony();
            harmony.Patch(CoopFakes.AccessTools.Method(typeof(DefaultPartyWageModel), "GetCharacterWage"), null, new HarmonyMethod(typeof(ModSubModule), nameof(ManualPostfix)));
        }
        public static void ManualPostfix() { }
        public static object? Reflect() => typeof(ModVM).GetMethod("ExecuteDone")!.Invoke(null, null);
    }
}
