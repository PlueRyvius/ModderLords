using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModderLords.Operations;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;
using TaleWorlds.ObjectSystem;

namespace ModderLords.CompatSync.Coop.Operations;

/// <summary>
/// Actor-aware command bridge for Bellum's first player-action tier. The client keeps Bellum's native confirmation
/// UI, but the final mutation is replaced with an authenticated ID-only operation and revalidated on the server.
/// </summary>
public sealed class BellumCivileCommandAdapter : ICompatibilityAdapter
{
    internal const string OperationId = "bellum-civile.command.v1";
    private const string HarmonyId = "ModderLords.Compat.BellumCivile.Commands.v1";
    private readonly Harmony harmony = new Harmony(HarmonyId);
    private readonly Dictionary<string, string> pending = new Dictionary<string, string>(StringComparer.Ordinal);
    private BellumCommandOperation? operation;
    private global::Coop.Core.Client.Services.ModderLordsCompat.Handlers.OperationClientHandler? client;
    private bool installed;
    private static BellumCivileCommandAdapter? currentClient;

    internal static readonly string[] Targets =
    {
        "BellumCivile.UI.VanillaTabs.Kingdoms.Hierarchy.HierarchyTitleNodeVM::StartPlayerFabrication",
        "BellumCivile.UI.VanillaTabs.Kingdoms.Hierarchy.HierarchyTitleNodeVM::CompletePlayerUsurpation",
        "BellumCivile.UI.VanillaTabs.Kingdoms.Hierarchy.HierarchyTitleNodeVM::CompletePlayerDissolution",
        "BellumCivile.UI.VanillaTabs.Kingdoms.Hierarchy.HierarchyTitleNodeVM::CompletePlayerRename",
        "BellumCivile.UI.VanillaTabs.Kingdoms.Hierarchy.HierarchyTitleNodeVM::CompletePlayerServiceChange",
        "BellumCivile.UI.VanillaTabs.Kingdoms.Hierarchy.HierarchyTitleNodeVM::CompletePlayerGrant",
        "BellumCivile.UI.VanillaTabs.Kingdoms.Hierarchy.HierarchyTitleNodeVM::CompletePlayerRevocation",
        "BellumCivile.Behaviors.SuccessionLawBehavior::TryApplyPlayerGenderLaw",
        "BellumCivile.Behaviors.SuccessionLawBehavior::TryApplyPlayerSuccessionLaw",
    };

    public string Id => "bellum-civile.commands.v1";
    public AdapterReadiness Readiness { get; private set; } = AdapterReadiness.Waiting;
    public string Detail { get; private set; } = "Waiting for pinned Bellum player-action targets";

    public bool ValidateTargets(out string reason)
    {
        foreach (var target in Targets)
        {
            var methods = Resolve(target);
            if (methods.Count != 1)
            {
                reason = methods.Count == 0 ? "Pinned Bellum command target is missing: " + target
                    : "Pinned Bellum command target is ambiguous: " + target;
                return false;
            }
            if (methods[0] is not MethodInfo method || method.GetMethodBody() == null
                || method.ReturnType != (target.IndexOf("SuccessionLawBehavior", StringComparison.Ordinal) >= 0 ? typeof(bool) : typeof(void)))
            {
                reason = "Pinned Bellum command target does not have the reviewed bool shape: " + target;
                return false;
            }
        }
        reason = "";
        return true;
    }

    public void Install()
    {
        if (!ValidateTargets(out var reason))
        {
            Readiness = AdapterReadiness.Failed; Detail = reason; throw new InvalidOperationException(reason);
        }
        try
        {
            if (OperationProcessSide.IsServer)
            {
                operation = new BellumCommandOperation();
                global::Coop.Core.Server.Services.ModderLordsCompat.Handlers.OperationServerHandler.Register(operation);
                Detail = Targets.Length + " Bellum player actions registered for server execution";
            }
            else
            {
                client = global::Coop.Core.Client.Services.ModderLordsCompat.Handlers.OperationClientHandler.Current
                    ?? throw new InvalidOperationException("Operation client handler is unavailable");
                currentClient = this;
                client.ResultReceived += OnResult;
                foreach (var target in Targets)
                {
                    var prefix = target.IndexOf("SuccessionLawBehavior", StringComparison.Ordinal) >= 0
                        ? new HarmonyMethod(typeof(BellumCivileCommandAdapter), nameof(ClientBoolCommandPrefix))
                        : new HarmonyMethod(typeof(BellumCivileCommandAdapter), nameof(ClientVoidCommandPrefix));
                    harmony.Patch(Resolve(target).Single(), prefix: prefix);
                }
                installed = true;
                Detail = Targets.Length + " Bellum player actions bound to authenticated Coop commands";
            }
            Readiness = AdapterReadiness.Ready;
        }
        catch
        {
            Dispose(); Readiness = AdapterReadiness.Failed; Detail = "Bellum player-action bridge could not be installed"; throw;
        }
    }

    public void Dispose()
    {
        if (operation != null) global::Coop.Core.Server.Services.ModderLordsCompat.Handlers.OperationServerHandler.Unregister(operation);
        if (client != null) client.ResultReceived -= OnResult;
        if (installed) harmony.UnpatchAll(HarmonyId);
        if (ReferenceEquals(currentClient, this)) currentClient = null;
        pending.Clear(); operation = null; client = null; installed = false;
        Readiness = AdapterReadiness.Disabled; Detail = "Disabled";
    }

    public static bool ClientVoidCommandPrefix(MethodBase __originalMethod, object __instance, object[] __args)
    {
        if (OperationProcessSide.IsServer) return true;
        if (!TrySubmit(__originalMethod, __instance, __args, out var error))
            InformationManager.DisplayMessage(new InformationMessage("Bellum action was not sent: " + error, Colors.Red));
        return false;
    }

    public static bool ClientBoolCommandPrefix(MethodBase __originalMethod, object[] __args, ref bool __result)
    {
        if (OperationProcessSide.IsServer) return true;
        __result = TrySubmit(__originalMethod, null, __args, out var error);
        SetOutDefaults(__originalMethod, __args, __result ? "" : error);
        return false;
    }

    private static bool TrySubmit(MethodBase method, object? instance, object[] args, out string error)
    {
        try
        {
            var adapter = currentClient ?? throw new InvalidOperationException("Bellum command bridge is unavailable");
            var command = FromCall(method.Name, instance, args);
            var payload = BellumCommandCodec.Serialize(command);
            if (adapter.client == null || !adapter.client.Submit(OperationId, payload, out var requestId))
                throw new InvalidOperationException("Coop command channel is unavailable");
            adapter.pending[requestId] = command.Kind;
            error = ""; return true;
        }
        catch (Exception ex) { error = ex.GetBaseException().Message; return false; }
    }

    private void OnResult(OperationResult result)
    {
        if (!pending.TryGetValue(result.RequestId, out var kind)) return;
        pending.Remove(result.RequestId);
        if (result.State == RequestState.Completed)
        {
            client?.RequestSnapshot(BellumCivileAdapter.OperationId);
            InformationManager.DisplayMessage(new InformationMessage("Bellum action completed: " + Friendly(kind), Colors.Green));
        }
        else InformationManager.DisplayMessage(new InformationMessage("Bellum action rejected: " + result.Detail, Colors.Red));
    }

    private static BellumCommand FromCall(string method, object? instance, object[] args)
    {
        var revision = BellumStateMirror.Current?.Revision
            ?? throw new InvalidOperationException("Bellum state has not synchronized yet");
        var command = new BellumCommand { ExpectedRevision = revision };
        switch (method)
        {
            case "StartPlayerFabrication": command.Kind = "title.fabricate"; command.Arguments["titleId"] = InstanceTitleId(instance); break;
            case "CompletePlayerUsurpation":
                command.Kind = "title.usurp"; command.Arguments["titleId"] = InstanceTitleId(instance); command.Arguments["elevation"] = Number(args[0]); break;
            case "CompletePlayerDissolution": command.Kind = "title.dissolve"; command.Arguments["titleId"] = InstanceTitleId(instance); break;
            case "CompletePlayerRename":
                command.Kind = "title.rename"; command.Arguments["titleId"] = InstanceTitleId(instance); command.Arguments["name"] = (string?)args[0] ?? ""; break;
            case "CompletePlayerServiceChange":
                command.Kind = "title.service"; command.Arguments["titleId"] = InstanceTitleId(instance); command.Arguments["level"] = Number(args[0]); break;
            case "CompletePlayerGrant":
                command.Kind = "title.grant"; command.Arguments["titleId"] = InstanceTitleId(instance); command.Arguments["recipientClanId"] = ExtractId(args[0], "StringId"); break;
            case "CompletePlayerRevocation": command.Kind = "title.revoke"; command.Arguments["titleId"] = InstanceTitleId(instance); break;
            case "TryApplyPlayerGenderLaw":
                command.Kind = "succession.gender"; command.Arguments["kingdomId"] = ExtractId(args[0], "StringId"); command.Arguments["law"] = Number(args[1]); break;
            case "TryApplyPlayerSuccessionLaw":
                command.Kind = "succession.house"; command.Arguments["kingdomId"] = ExtractId(args[0], "StringId"); command.Arguments["law"] = Number(args[1]); break;
            default: throw new InvalidOperationException("Unsupported Bellum command target");
        }
        return command;
    }

    private static void SetOutDefaults(MethodBase method, object[] args, string reason)
    {
        var parameters = method.GetParameters();
        for (var i = 0; i < parameters.Length; i++)
        {
            if (!parameters[i].ParameterType.IsByRef) continue;
            var type = parameters[i].ParameterType.GetElementType()!;
            args[i] = (type == typeof(string) ? reason : type.IsValueType ? Activator.CreateInstance(type) : null)!;
        }
    }

    private static string Friendly(string kind) => kind.Replace('.', ' ');
    private static string InstanceTitleId(object? instance)
        => ExtractId(AccessTools.Property(instance?.GetType(), "Title")?.GetValue(instance, null), "TitleId");
    private static string ExtractId(object? value, string property)
        => (string?)AccessTools.Property(value?.GetType(), property)?.GetValue(value, null)
            ?? throw new InvalidOperationException("Bellum action is missing " + property);
    private static string Number(object? value) => Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

    private sealed class BellumCommandOperation : IServerOperation
    {
        private static readonly HashSet<string> Kinds = new HashSet<string>(new[]
        {
            "title.fabricate", "title.usurp", "title.dissolve", "title.rename", "title.service", "title.grant", "title.revoke",
            "succession.gender", "succession.house",
        }, StringComparer.Ordinal);

        public string Id => OperationId;
        public int MaxPayloadBytes => BellumCommandCodec.MaxBytes;

        public bool Validate(Actor actor, string payload, out string reason)
        {
            try
            {
                var command = BellumCommandCodec.Deserialize(payload);
                if (!Kinds.Contains(command.Kind)) throw new OperationRejectedException("Unsupported Bellum action");
                if (Campaign.Current == null) throw new OperationRejectedException("Campaign is unavailable");
                var hero = ResolveHero(actor.HeroId);
                if (hero?.Clan == null || hero.Clan.StringId != actor.ClanId) throw new OperationRejectedException("Actor clan ownership changed");
                var revision = BellumCivileAdapter.RefreshServerRevision();
                if (revision < 0 || command.ExpectedRevision != revision)
                    throw new OperationRejectedException("Political state changed; review the refreshed Bellum screen and try again");
                ValidateArguments(command);
                reason = ""; return true;
            }
            catch (OperationRejectedException ex) { reason = ex.Message; return false; }
            catch { reason = "Malformed Bellum action"; return false; }
        }

        public string Execute(Actor actor, string payload)
        {
            var command = BellumCommandCodec.Deserialize(payload);
            var hero = ResolveHero(actor.HeroId) ?? throw new OperationRejectedException("Actor hero is unavailable");
            var clan = hero.Clan ?? throw new OperationRejectedException("Actor clan is unavailable");
            using (new global::ModderLords.CompatSync.Coop.ServerRelay.PlayerScope(hero, hero.PartyBelongedTo))
            {
                switch (command.Kind)
                {
                    case "title.fabricate": RunFabrication(clan, command); break;
                    case "title.usurp": RunTitleService("TryExecuteUsurpation", clan, command, EnumValue("BellumCivile.FeudalSovereignElevationChoice", command.Arguments["elevation"])); break;
                    case "title.dissolve": RunTitleService("TryExecuteDissolution", clan, command); break;
                    case "title.rename": RunTitleService("TryExecuteRename", clan, command, command.Arguments["name"]); break;
                    case "title.service": RunTitleService("TrySetServiceLevel", clan, command, EnumValue("BellumCivile.FeudalServiceLevel", command.Arguments["level"])); break;
                    case "title.grant": RunTitleService("TryExecuteGrant", clan, command, ResolveClan(command.Arguments["recipientClanId"])); break;
                    case "title.revoke": RunTitleService("TryExecuteRevocation", clan, command); break;
                    case "succession.gender": RunSuccession("TryApplyPlayerGenderLaw", command, "BellumCivile.GenderSuccessionLaw"); break;
                    case "succession.house": RunSuccession("TryApplyPlayerSuccessionLaw", command, "BellumCivile.HouseSuccessionLaw"); break;
                    default: throw new OperationRejectedException("Unsupported Bellum action");
                }
            }
            BellumSnapshotBroadcast.MarkDirty();
            return "{}";
        }

        private static void ValidateArguments(BellumCommand command)
        {
            string[] required;
            switch (command.Kind)
            {
                case "title.rename": required = new[] { "titleId", "name" }; break;
                case "title.service": required = new[] { "titleId", "level" }; break;
                case "title.usurp": required = new[] { "titleId", "elevation" }; break;
                case "title.grant": required = new[] { "titleId", "recipientClanId" }; break;
                case "succession.gender": case "succession.house": required = new[] { "kingdomId", "law" }; break;
                default: required = new[] { "titleId" }; break;
            }
            if (command.Arguments.Count != required.Length || required.Any(k => !command.Arguments.TryGetValue(k, out var value) || string.IsNullOrWhiteSpace(value)))
                throw new OperationRejectedException("Bellum action arguments are incomplete");
            if (command.Kind == "title.rename" && command.Arguments["name"].Length > 80)
                throw new OperationRejectedException("Title name is too long");
            if (command.Kind.StartsWith("title.", StringComparison.Ordinal)) ResolveTitle(command.Arguments["titleId"]);
            if (command.Kind == "title.grant") ResolveClan(command.Arguments["recipientClanId"]);
            if (command.Kind.StartsWith("succession.", StringComparison.Ordinal)) ResolveKingdom(command.Arguments["kingdomId"]);
            if (command.Kind == "title.service") EnumValue("BellumCivile.FeudalServiceLevel", command.Arguments["level"]);
            if (command.Kind == "title.usurp") EnumValue("BellumCivile.FeudalSovereignElevationChoice", command.Arguments["elevation"]);
            if (command.Kind == "succession.gender") EnumValue("BellumCivile.GenderSuccessionLaw", command.Arguments["law"]);
            if (command.Kind == "succession.house") EnumValue("BellumCivile.HouseSuccessionLaw", command.Arguments["law"]);
        }

        private static void RunFabrication(Clan clan, BellumCommand command)
        {
            var behavior = Behavior("BellumCivile.Behaviors.FeudalClaimFabricationBehavior");
            InvokeBool(behavior, "TryStartFabrication", new object?[] { clan, ResolveTitle(command.Arguments["titleId"]), true, null, null });
        }

        private static void RunTitleService(string method, Clan clan, BellumCommand command, object? extra = null)
        {
            var args = new List<object?> { clan, ResolveTitle(command.Arguments["titleId"]) };
            if (extra != null) args.Add(extra);
            if (method == "TryExecuteGrant") args.Add(null);
            if (method == "TryExecuteRevocation") args.Add(false);
            args.Add(null);
            InvokeBool(null, method, args.ToArray(), "BellumCivile.FeudalTitlePlayerActionService");
        }

        private static void RunSuccession(string method, BellumCommand command, string enumType)
        {
            var behavior = Behavior("BellumCivile.Behaviors.SuccessionLawBehavior");
            InvokeBool(behavior, method, new object?[] { ResolveKingdom(command.Arguments["kingdomId"]), EnumValue(enumType, command.Arguments["law"]), null });
        }

        private static void InvokeBool(object? receiver, string methodName, object?[] args, string? typeName = null)
        {
            var type = receiver?.GetType() ?? AccessTools.TypeByName(typeName!)
                ?? throw new InvalidOperationException("Bellum command type is unavailable: " + typeName);
            var method = AccessTools.GetDeclaredMethods(type).SingleOrDefault(m => m.Name == methodName && m.GetParameters().Length == args.Length)
                ?? throw new InvalidOperationException("Bellum command method is unavailable: " + type.FullName + "::" + methodName);
            bool result;
            try { result = (bool)(method.Invoke(receiver, args) ?? false); }
            catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
            if (result) return;
            var reason = args.LastOrDefault()?.ToString();
            throw new OperationRejectedException(string.IsNullOrWhiteSpace(reason) ? "Bellum rejected the action" : reason!);
        }

        private static object ResolveTitle(string id)
        {
            var behavior = Behavior("BellumCivile.Behaviors.FeudalTitleBehavior");
            return AccessTools.Method(behavior.GetType(), "GetTitle", new[] { typeof(string) })?.Invoke(behavior, new object[] { id })
                ?? throw new OperationRejectedException("The selected Bellum title no longer exists");
        }

        private static Hero? ResolveHero(string id) => MBObjectManager.Instance.GetObject<Hero>(id);
        private static Clan ResolveClan(string id) => Clan.All.FirstOrDefault(x => x.StringId == id)
            ?? throw new OperationRejectedException("The selected clan no longer exists");
        private static Kingdom ResolveKingdom(string id) => Kingdom.All.FirstOrDefault(x => x.StringId == id)
            ?? throw new OperationRejectedException("The selected kingdom no longer exists");

        private static object EnumValue(string typeName, string value)
        {
            var type = AccessTools.TypeByName(typeName) ?? throw new InvalidOperationException("Bellum enum is unavailable: " + typeName);
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                throw new OperationRejectedException("Invalid Bellum action choice");
            var result = Enum.ToObject(type, number);
            if (!Enum.IsDefined(type, result)) throw new OperationRejectedException("Invalid Bellum action choice");
            return result;
        }

        private static object Behavior(string typeName)
        {
            var type = AccessTools.TypeByName(typeName) ?? throw new InvalidOperationException("Bellum behavior type is unavailable: " + typeName);
            var instance = AccessTools.Property(type, "Instance")?.GetValue(null, null);
            if (instance != null) return instance;
            var campaign = Campaign.Current ?? throw new InvalidOperationException("Campaign is unavailable");
            return AccessTools.Method(typeof(Campaign), "GetCampaignBehavior")?.MakeGenericMethod(type).Invoke(campaign, null)
                ?? throw new InvalidOperationException("Bellum behavior is unavailable: " + typeName);
        }
    }

    private static List<MethodBase> Resolve(string identity)
    {
        var split = identity.Split(new[] { "::" }, 2, StringSplitOptions.None);
        if (split.Length != 2) return new List<MethodBase>();
        var type = AccessTools.TypeByName(split[0]);
        return type == null ? new List<MethodBase>() : AccessTools.GetDeclaredMethods(type)
            .Where(m => m.Name == split[1]).Cast<MethodBase>().ToList();
    }
}
