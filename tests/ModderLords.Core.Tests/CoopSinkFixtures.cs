// Fixture for CoopSinksTests: Coop-shaped patches compiled into this test assembly, which is scanned as if it were
// GameInterface.dll. The scan matches Harmony/Coop types by simple name, so these fakes stand in for the real ones.
namespace ModderLords.Core.Tests.CoopFakes
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public sealed class HarmonyPatch : Attribute
    {
        public HarmonyPatch() { }
        public HarmonyPatch(Type type) { }
        public HarmonyPatch(string method) { }
        public HarmonyPatch(Type type, string method) { }
        public HarmonyPatch(Type type, string method, MethodType kind) { }
    }
    public sealed class HarmonyPrefix : Attribute { }
    public sealed class HarmonyTranspiler : Attribute { }
    public enum MethodType { Normal, Getter, Setter, Constructor }

    public static class AccessTools
    {
        public static object? Field(Type type, string name) => null;
        public static object? Property(Type type, string name) => null;
        public static object? Method(Type type, string name) => null;
    }
    public static class ModInformation { public static bool IsServer => false; public static bool IsClient => true; }
    public static class CallOriginalPolicy { public static bool IsOriginalAllowed() => true; }
    public sealed class AutoSyncRegistry { public void AddField(object? field) { } public void AddProperty(object? property) { } }
    public sealed class MessageBroker { public static MessageBroker Instance { get; } = new(); public void Publish(object? source, object message) { } }

    // "Vanilla" targets.
    public class VanillaUpgrader { public void RegisterEvents() { } public void UpgradeReadyTroops() { } }
    public class VanillaRecruitment { public void RegisterEvents() { } }
    public class VanillaGold { public static void ApplyInternal() { } public int Gold { get; set; } }
    public class VanillaArena { public void game_menu_arena() { } }

    // Shape 1: class-level type, method-level name, body exactly `return IsServer`.
    [HarmonyPatch(typeof(VanillaUpgrader))]
    internal class GateUpgrader
    {
        [HarmonyPatch("RegisterEvents")]
        private static bool Prefix() => ModInformation.IsServer;
    }

    // Shape 2: branches on IsServer and does client work (RecruitmentCampaignBehavior's form).
    [HarmonyPatch(typeof(VanillaRecruitment))]
    internal class GateRecruitment
    {
        [HarmonyPatch("RegisterEvents")]
        [HarmonyPrefix]
        private static bool PrefixRegisterEvents(VanillaRecruitment __instance)
        {
            if (ModInformation.IsServer) return true;
            Touch(__instance);
            return false;
        }
        private static void Touch(object o) { }
    }

    // Class-level type + name, conventional Prefix name with no attribute.
    [HarmonyPatch(typeof(VanillaGold), "ApplyInternal")]
    internal class GateGold
    {
        private static bool Prefix() => ModInformation.IsServer;
    }

    // Not a gate: never consults authority.
    [HarmonyPatch(typeof(VanillaArena))]
    internal class NotAGate
    {
        [HarmonyPatch("game_menu_arena")]
        [HarmonyPrefix]
        private static bool DisableArena() => true;
    }

    // Property setter through MethodType, deferring to CallOriginalPolicy.
    [HarmonyPatch(typeof(VanillaGold), "Gold", MethodType.Setter)]
    internal class PolicyGate
    {
        private static bool Prefix()
        {
            if (CallOriginalPolicy.IsOriginalAllowed()) return true;
            return false;
        }
    }

    public class VanillaLeave { public static void ApplyForParty(object party) { } }
    public class VanillaCheats { public static bool CheckCheatUsage(ref string error) => true; }
    public class VanillaRoster { public int AddToCounts(int n) => n; }

    // Publishes: the client's call becomes Coop's own request (LeaveSettlementAction's form).
    [HarmonyPatch(typeof(VanillaLeave), "ApplyForParty")]
    internal class PublishGate
    {
        private static bool Prefix(object party)
        {
            if (CallOriginalPolicy.IsOriginalAllowed()) return true;
            MessageBroker.Instance.Publish(party, "leave");
            return ModInformation.IsServer;
        }
    }

    // ClientDeny: refused on clients, nothing published (CheatsOnClientsPatch's form).
    [HarmonyPatch(typeof(VanillaCheats), "CheckCheatUsage")]
    internal class DenyGate
    {
        private static bool Prefix(ref string error)
        {
            if (ModInformation.IsClient)
            {
                error = "disabled";
                return false;
            }
            return true;
        }
    }

    // ClientLocal: runs locally on clients, nothing published (ItemRosterPatch.AddToCountsPrefix's form).
    [HarmonyPatch(typeof(VanillaRoster), "AddToCounts")]
    internal class LocalGate
    {
        private static bool Prefix(VanillaRoster __instance)
        {
            if (CallOriginalPolicy.IsOriginalAllowed()) return true;
            if (ModInformation.IsClient)
            {
                if (__instance.AddToCounts(0) > 0) Log("client changed a managed roster");
                return true;
            }
            return true;
        }
        private static void Log(string s) { }
    }

    internal class SyncDeclarations
    {
        public void Register(AutoSyncRegistry registry)
        {
            registry.AddField(AccessTools.Field(typeof(VanillaGold), "_gold"));
            registry.AddProperty(AccessTools.Property(typeof(VanillaGold), "Gold"));
        }
    }

    [HarmonyPatch]
    internal class ListedPatches
    {
        private static IEnumerable<object?> TargetMethods()
        {
            yield return AccessTools.Method(typeof(VanillaUpgrader), "UpgradeReadyTroops");
        }

        [HarmonyTranspiler]
        private static IEnumerable<object> VolunteerTranspiler(IEnumerable<object> instructions)
        {
            var field = AccessTools.Field(typeof(VanillaUpgrader), "_volunteers");
            foreach (var i in instructions) yield return field ?? i;
        }
    }
}
