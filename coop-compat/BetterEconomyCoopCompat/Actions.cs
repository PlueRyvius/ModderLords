using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace BetterEconomyCoopCompat;

/// <summary>
/// Players' clicks are carried out by the server, as that player.
///
/// Every player action in Living Economy's menus ends in one of the mod's own methods: invest in a town or village,
/// contribute to a town or castle treasury, set a town's policy or tax, start a town or castle project, build an armory
/// or training camp, start or cancel troop training, run an estate action, negotiate village market access, and sign a
/// trade treaty through barter. On a player's game in a co-op session this component catches that call, sends it to
/// the server (this module's own LeAction message, through the Coop adapter), and tells the mod the action is on its
/// way. The server runs the same method with the same arguments inside
/// <see cref="PlayerScope"/> for the sender, so the mod's own checks (does your clan own this town, can you
/// afford it, cooldowns) run as they would for a lone player. Gold, troops and relations change on the server and reach
/// everyone through Coop; the mod's records reach everyone through the state mirror, which is told to send at once.
/// The player then sees the same lines the mod would have shown them.
///
/// The server trusts only the settlement and the plain arguments: the hero is always the sender's own, the sender's
/// party must be at (or have just left) that settlement, and gold amounts must be plausible.
/// </summary>
internal sealed class Actions : IComponent
{
    public const string Feature = "living-economy";
    private const string Owner = "modderlords.bettereconomy.coopcompat.Actions";
    private const string B = "BetterEconomy.Behaviors.";
    private const string TownT = B + "TownEconomyCampaignBehavior";
    private const string CastleT = B + "CastleEconomyCampaignBehavior";
    private const string FeudalT = B + "FeudalEconomyCampaignBehavior";
    private const string VillageDevT = B + "VillageDevelopmentCampaignBehavior";
    private const string ServiceT = "BetterEconomy.UI.SettlementActionService";
    private const string TreatyBarterT = "BetterEconomy.Core.TradeAgreementBarterable";
    private const string TreatyT = B + "TradeAgreementCampaignBehavior";
    private const string OpTreaty = "treaty";

    private enum Shape { Reason, Bool, Void }

    /// <summary>One relayed method: how the client hands it back to the UI, and how the server checks and words the result.</summary>
    private sealed class Spec
    {
        public Spec(string type, string method, int parameters, Shape shape)
        {
            Type = type;
            Method = method;
            Parameters = parameters;
            Shape = shape;
        }

        public string Type { get; }
        public string Method { get; }
        public int Parameters { get; }
        public Shape Shape { get; }
        public string Op => ActionCodec.OpFor(Type, Method) + "/" + Parameters;

        /// <summary>Index of a "was this the player?" flag the server forces to true (TrySetTaxPolicy's playerAction, ...).</summary>
        public int PlayerFlag { get; set; } = -1;
        /// <summary>Index of a gold amount the server sanity-checks.</summary>
        public int Amount { get; set; } = -1;
        /// <summary>Server check before the call: null to go ahead, or the line to show.</summary>
        public Func<Call, string?>? Check { get; set; }
        /// <summary>The mod's own "blocked" line: key and the fallback for an empty reason.</summary>
        public (string Key, string UnknownKey, string UnknownText)? Blocked { get; set; }
        /// <summary>The mod's own success line.</summary>
        public Func<Call, (string Key, string Text, (string, object)[] Vars)>? Success { get; set; }
        public bool SuccessIsYellow { get; set; }
        /// <summary>Bool-shaped methods only: the line when the method said no.</summary>
        public Func<Call, string>? Refused { get; set; }

        public MethodInfo? Target { get; set; }
    }

    /// <summary>The server's view of one relayed call.</summary>
    private sealed class Call
    {
        public Call(Settlement settlement, object? instance, object?[] args)
        {
            Settlement = settlement;
            Instance = instance;
            Args = args;
        }

        public Settlement Settlement { get; }
        public object? Instance { get; }
        public object?[] Args { get; }
        public string Name => Settlement.Name?.ToString() ?? Settlement.StringId;
        public object? Invoke(string method, params object?[] args) => Reflect.Invoke(Instance, method, args);
    }

    private static readonly Spec[] Specs =
    {
        new Spec(TownT, "TrySetPolicy", 2, Shape.Bool)
        {
            Check = c => c.Settlement.IsTown && c.Settlement.OwnerClan == Clan.PlayerClan
                ? null
                : T("ml_le_policy_not_owner", "{TAG} You can change policy only in towns owned by your clan."),
            Success = c => ("bee_policy_changed", "{TAG} {TOWN} policy changed to {POLICY}.",
                new (string, object)[] { ("TOWN", c.Name), ("POLICY", c.Invoke("PolicyName", c.Args[1]) ?? "") }),
            Refused = c => T("ml_le_policy_same", "{TAG} {TOWN} already follows {POLICY}.", ("TOWN", c.Name), ("POLICY", c.Invoke("PolicyName", c.Args[1]) ?? "")),
        },
        new Spec(TownT, "TrySetTaxPolicy", 3, Shape.Bool)
        {
            PlayerFlag = 2,
            Check = c =>
            {
                var (ok, reason) = Reflect.CanWithReason(c.Instance, "CanPlayerSetTaxPolicy", c.Settlement);
                return ok ? null : T("bee_tax_blocked", "{TAG} {REASON}", ("REASON", reason ?? T("bee_tax_reason_unknown", "Tax change is not available.")));
            },
            Success = c => ("bee_tax_changed", "{TAG} {TOWN} tax policy changed to {POLICY}.",
                new (string, object)[] { ("TOWN", c.Name), ("POLICY", c.Invoke("TaxPolicyName", c.Args[1]) ?? "") }),
            Refused = c => T("ml_le_tax_same", "{TAG} {TOWN} already uses {POLICY}.", ("TOWN", c.Name), ("POLICY", c.Invoke("TaxPolicyName", c.Args[1]) ?? "")),
        },
        new Spec(TownT, "TryStartProject", 4, Shape.Reason)
        {
            PlayerFlag = 2,
            Blocked = ("bee_project_blocked", "bee_project_reason_unknown", "Project is not available."),
            Success = c => ("bee_project_started", "{TAG} {TOWN} started {PROJECT}.",
                new (string, object)[] { ("TOWN", c.Name), ("PROJECT", c.Invoke("ProjectName", c.Args[1]) ?? "") }),
        },
        new Spec(TownT, "TryPlayerContributeTreasury", 4, Shape.Reason)
        {
            Amount = 2,
            Blocked = ("bee_treasury_contribute_blocked", "bee_treasury_contribute_reason_unknown", "Treasury contribution is not available."),
            Success = c => ("bee_treasury_contribute_done", "{TAG} Contributed {GOLD}g to {TOWN}'s local treasury. Grant funds are locked from surplus payout for {DAYS} days.",
                new (string, object)[] { ("GOLD", c.Args[2] ?? 0), ("TOWN", c.Name), ("DAYS", Reflect.Setting("PlayerTreasuryGrantLockDays") ?? 14) }),
        },
        new Spec(TownT, "TryPlayerBuildOrUpgradeArmory", 3, Shape.Reason)
        {
            Blocked = ("bee_armory_blocked", "bee_armory_reason_unknown", "Armory is not available."),
            Success = c => ("bee_armory_done", "{TAG} {TOWN} armory is now level {LEVEL}.",
                new (string, object)[] { ("TOWN", c.Name), ("LEVEL", Reflect.Field(c.Invoke("GetOrCreateState", c.Settlement), "ArmoryLevel") ?? 0) }),
        },
        new Spec(FeudalT, "TryApplyEstateAction", 3, Shape.Reason)
        {
            Blocked = ("bee_estate_blocked", "bee_estate_reason_unknown", "Estate action is not available."),
            Success = c => ("bee_estate_done", "{TAG} {TOWN}: {ACTION}.",
                new (string, object)[] { ("TOWN", c.Name), ("ACTION", c.Invoke("EstateActionName", c.Args[1]) ?? "") }),
        },
        new Spec(CastleT, "TryPlayerContributeTreasury", 4, Shape.Reason)
        {
            Amount = 2,
            Blocked = ("bee_castle_blocked", "bee_castle_reason_unknown", "Castle action is not available."),
            Success = c => ("bee_castle_treasury_done", "{TAG} Contributed {GOLD}g to {CASTLE}'s castle treasury.",
                new (string, object)[] { ("GOLD", c.Args[2] ?? 0), ("CASTLE", c.Name) }),
        },
        new Spec(CastleT, "TryStartProject", 4, Shape.Reason)
        {
            PlayerFlag = 2,
            Blocked = ("bee_castle_blocked", "bee_castle_reason_unknown", "Castle action is not available."),
            Success = c => ("bee_castle_project_started", "{TAG} {CASTLE} started {PROJECT}.",
                new (string, object)[] { ("CASTLE", c.Name), ("PROJECT", c.Invoke("ProjectName", c.Args[1]) ?? "") }),
        },
        new Spec(CastleT, "TryPlayerBuildOrUpgradeTrainingCamp", 3, Shape.Reason)
        {
            Blocked = ("bee_castle_blocked", "bee_castle_reason_unknown", "Castle action is not available."),
            Success = c => ("bee_castle_training_camp_done", "{TAG} Training Camp construction started at {CASTLE}: {SUMMARY}",
                new (string, object)[] { ("CASTLE", c.Name), ("SUMMARY", c.Invoke("GetTrainingSummary", c.Settlement) ?? "") }),
        },
        new Spec(CastleT, "TryPlayerStartTraining", 3, Shape.Reason)
        {
            Blocked = ("bee_castle_blocked", "bee_castle_reason_unknown", "Castle action is not available."),
            Success = c => ("bee_castle_training_started", "{TAG} Training started at {CASTLE}.", new (string, object)[] { ("CASTLE", c.Name) }),
        },
        new Spec(CastleT, "TryPlayerCancelTraining", 3, Shape.Reason)
        {
            Blocked = ("bee_castle_blocked", "bee_castle_reason_unknown", "Castle action is not available."),
            Success = c => ("bee_castle_training_cancelled", "{TAG} Training cancelled at {CASTLE}.", new (string, object)[] { ("CASTLE", c.Name) }),
            SuccessIsYellow = true,
        },
        new Spec(VillageDevT, "TryPlayerNegotiateMarketAccess", 3, Shape.Reason)
        {
            Blocked = ("bee_village_market_blocked", "bee_village_market_reason_unknown", "Market access negotiation is not available."),
            Success = c => ("bee_village_market_done", "{TAG} Market access negotiated in {VILLAGE}. Trade diversion is suppressed for {DAYS} days.",
                new (string, object)[] { ("VILLAGE", c.Name), ("DAYS", Reflect.Setting("VillageMarketAccessSuppressDays") ?? 14) }),
        },
        // These two do the whole job, lines included; the server collects the lines it shows.
        new Spec(ServiceT, "TryExecuteTownInvestment", 2, Shape.Void) { Amount = 1 },
        new Spec(ServiceT, "TryExecuteVillageInvestment", 2, Shape.Void) { Amount = 1 },
    };

    private static readonly Dictionary<MethodBase, Spec> ByMethod = new Dictionary<MethodBase, Spec>();
    private static readonly Dictionary<string, Spec> ByOp = new Dictionary<string, Spec>(StringComparer.Ordinal);
    private static FieldInfo? _treatyA;
    private static FieldInfo? _treatyB;
    private static MethodInfo? _treatyApply;
    private static MethodInfo? _createAgreement;
    private static long _sent;
    private static long _ran;

    public string Id => "actions";

    public string? SkipReason(Context context)
    {
        ByMethod.Clear();
        ByOp.Clear();
        var missing = new List<string>();
        foreach (var spec in Specs)
        {
            var m = context.Method(spec.Type, spec.Method, spec.Parameters);
            if (m == null || !ShapeMatches(spec, m)) { missing.Add(Layer.Short(spec.Type) + "." + spec.Method); continue; }
            spec.Target = m;
            ByMethod[m] = spec;
            ByOp[spec.Op] = spec;
        }
        var barter = context.Type(TreatyBarterT);
        _treatyA = barter?.GetField("_offererKingdomId", BindingFlags.Instance | BindingFlags.NonPublic);
        _treatyB = barter?.GetField("_otherKingdomId", BindingFlags.Instance | BindingFlags.NonPublic);
        _treatyApply = barter?.GetMethod("Apply", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
        _createAgreement = context.Type(TreatyT)?.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "CreateAgreement" && m.GetParameters().Length == 4);
        if (_treatyA == null || _treatyB == null || _treatyApply == null || _createAgreement == null) missing.Add("trade treaty barter");
        if (ByMethod.Count == 0) return "Living Economy changed; none of its player actions were found";
        if (missing.Count > 0) Log.Warn("actions not relayed (Living Economy changed?): " + Layer.Some(missing));
        return null;
    }

    /// <summary>The method still has the shape the relay relies on: a Settlement first, and a bool / out string where expected.</summary>
    private static bool ShapeMatches(Spec spec, MethodInfo m)
    {
        var ps = m.GetParameters();
        if (ps.Length == 0 || ps[0].ParameterType != typeof(Settlement)) return false;
        return spec.Shape switch
        {
            Shape.Reason => m.ReturnType == typeof(bool) && ps[ps.Length - 1].IsOut && ps[ps.Length - 1].ParameterType.GetElementType() == typeof(string)
                            && ps[ps.Length - 1].Name == "reason",
            Shape.Bool => m.ReturnType == typeof(bool),
            _ => m.ReturnType == typeof(void),
        };
    }

    public string Install(Context context)
    {
        if (context.IsServer)
        {
            return $"server carries out {ByOp.Count} Living Economy action(s)" + (_createAgreement != null ? " and trade treaties" : "") + " for players";
        }
        var h = new Harmony(Owner);
        foreach (var spec in ByMethod.Values)
        {
            var prefix = spec.Shape switch
            {
                Shape.Reason => nameof(ReasonPrefix),
                Shape.Bool => nameof(BoolPrefix),
                _ => nameof(VoidPrefix),
            };
            h.Patch(spec.Target!, prefix: new HarmonyMethod(typeof(Actions), prefix) { priority = Priority.First });
        }
        if (_treatyApply != null && _createAgreement != null)
            h.Patch(_treatyApply, prefix: new HarmonyMethod(typeof(Actions), nameof(TreatyPrefix)) { priority = Priority.First });
        return $"{ByMethod.Count} Living Economy action(s) are sent to the server during co-op";
    }

    // ---- client ---------------------------------------------------------------------------------------------------

    private static string SentLine() =>
        new TextObject("{=ml_le_sent}Sent to the server; its answer follows.").ToString();

    private static bool ReasonPrefix(MethodBase __originalMethod, object[] __args, ref bool __result, ref string reason)
    {
        if (!TrySend(__originalMethod, __args)) return true;
        __result = false;
        reason = SentLine();
        // Harmony copies __args back over ref/out arguments after a prefix that takes it, so the note goes in both.
        if (__args != null && __args.Length > 0) __args[__args.Length - 1] = reason;
        return false;
    }

    private static bool BoolPrefix(MethodBase __originalMethod, object[] __args, ref bool __result)
    {
        if (!TrySend(__originalMethod, __args)) return true;
        __result = false;
        return false;
    }

    private static bool VoidPrefix(MethodBase __originalMethod, object[] __args) => !TrySend(__originalMethod, __args);

    /// <summary>Client: true when the call was sent to the server and must not run here.</summary>
    private static bool TrySend(MethodBase original, object[] args)
    {
        if (!Layer.IsCoopClient || Net.SendAction is not { } send) return false;
        if (!ByMethod.TryGetValue(original, out var spec)) return false;
        var encoded = new List<string>();
        var ps = original.GetParameters();
        for (var i = 0; i < ps.Length; i++)
        {
            if (ps[i].IsOut) { encoded.Add(""); continue; }
            var value = i < args.Length ? args[i] : null;
            if (value is Settlement s) { encoded.Add(s.StringId); continue; }
            if (value is Hero) { encoded.Add(""); continue; }   // the server uses the sender's own hero
            if (!ActionCodec.TryEncodeValue(value, out var text))
            {
                Log.Warn($"{spec.Method}: argument {ps[i].Name} ({ps[i].ParameterType.Name}) cannot be sent; running it locally");
                return false;
            }
            encoded.Add(text);
        }
        send(spec.Op, encoded);
        _sent++;
        Log.Info($"sent {spec.Method} ({string.Join(", ", encoded.Where(e => e.Length > 0))}) to the server");
        return true;
    }

    /// <summary>Client: a trade treaty agreed in a barter is signed by the server, not here.</summary>
    private static bool TreatyPrefix(object __instance)
    {
        if (!Layer.IsCoopClient || Net.SendAction is not { } send || __instance == null) return true;
        var a = _treatyA?.GetValue(__instance) as string;
        var b = _treatyB?.GetValue(__instance) as string;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return true;
        send(OpTreaty, new[] { a!, b! });
        _sent++;
        Log.Info($"sent trade treaty {a} <-> {b} to the server");
        return false;
    }

    /// <summary>Client: shows the lines the server collected, in the mod's own colours.</summary>
    internal static void ShowLines(IList<string> data)
    {
        foreach (var line in data)
            if (ActionCodec.TrySplitLine(line, out var color, out var text))
                Notices.Show(text, color);
    }

    // ---- server ---------------------------------------------------------------------------------------------------

    /// <summary>Server: true while a relayed action runs; its reply carries the lines, so nothing else is forwarded.</summary>
    [ThreadStatic] internal static bool Running;

    /// <summary>Server, game thread: one player's relayed click, run with that player standing in as "the player".</summary>
    internal static (bool Ok, List<string> Lines) ServerRun(Hero hero, MobileParty? party, string op, IList<string> args)
    {
        if (!Layer.IsCoopServer) return (false, new List<string> { ActionCodec.Line(Colors.Yellow.ToUnsignedInteger(), "This is not a co-op server.") });
        Running = true;
        try
        {
            using (new PlayerScope(hero, party))
                return RunScoped(hero, party, op, args);
        }
        finally { Running = false; }
    }

    private static (bool Ok, List<string> Lines) RunScoped(Hero hero, MobileParty? party, string op, IList<string> args)
    {
        Notices.BeginCapture();
        var lines = new List<string>();
        bool ok;
        try
        {
            ok = op == OpTreaty ? RunTreaty(hero, args, lines) : RunSpec(hero, party, op, args, lines);
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
            Log.Warn($"{op} for {hero.Name} failed: {inner.GetBaseException().Message}");
            lines.Add(ActionCodec.Line(Colors.Red.ToUnsignedInteger(), "The server could not do that: " + inner.GetBaseException().Message));
            ok = false;
        }
        finally
        {
            // Lines the mod itself showed come first, then ours.
            lines.InsertRange(0, Notices.EndCapture());
        }
        if (ok)
        {
            _ran++;
            StateMirror.MarkDirty();
        }
        return (ok, ActionCodec.Cap(lines));
    }

    private static bool RunSpec(Hero hero, MobileParty? party, string op, IList<string> args, List<string> lines)
    {
        if (!ByOp.TryGetValue(op, out var spec) || spec.Target == null)
            return Refuse(lines, "This server does not know that Living Economy action; update Living Economy Coop Compat on the host.");
        var ps = spec.Target.GetParameters();
        if (args.Count != ps.Length) return Refuse(lines, "The server did not understand the request.");

        var values = new object?[ps.Length];
        Settlement? settlement = null;
        for (var i = 0; i < ps.Length; i++)
        {
            var type = ps[i].ParameterType;
            if (ps[i].IsOut) { values[i] = null; continue; }
            if (type == typeof(Settlement))
            {
                settlement = Settlement.Find(args[i]);
                if (settlement == null) return Refuse(lines, "That settlement was not found on the server.");
                values[i] = settlement;
                continue;
            }
            if (type == typeof(Hero)) { values[i] = hero; continue; }
            if (!ActionCodec.TryDecodeValue(args[i], type, out var value)) return Refuse(lines, "The server did not understand the request.");
            values[i] = value;
        }
        if (settlement == null) return Refuse(lines, "The server did not understand the request.");
        if (spec.PlayerFlag >= 0) values[spec.PlayerFlag] = true;
        if (spec.Amount >= 0 && !(values[spec.Amount] is int amount && ActionCodec.IsPlausibleAmount(amount)))
            return Refuse(lines, "That amount is not possible.");
        if (party == null || (party.CurrentSettlement != settlement && Reflect.LastVisited(party) != settlement))
            return Refuse(lines, new TextObject("{=ml_le_not_there}You must be in {SETTLEMENT} to do that.")
                .SetTextVariable("SETTLEMENT", settlement.Name?.ToString() ?? settlement.StringId).ToString());

        var instance = spec.Target.IsStatic ? null : Reflect.BehaviorFor(spec.Target.DeclaringType!);
        if (!spec.Target.IsStatic && instance == null) return Refuse(lines, "Living Economy is not running on this server.");
        var call = new Call(settlement, instance, values);

        if (spec.Check?.Invoke(call) is { } blocked)
        {
            lines.Add(ActionCodec.Line(Colors.Yellow.ToUnsignedInteger(), blocked));
            return false;
        }

        var result = spec.Target.Invoke(instance, values);
        switch (spec.Shape)
        {
            case Shape.Void:
                return true;   // the mod's own lines were captured
            case Shape.Bool:
                if (result is true) return Succeed(spec, call, lines);
                if (spec.Refused != null) lines.Add(ActionCodec.Line(Colors.Yellow.ToUnsignedInteger(), spec.Refused(call)));
                return false;
            default:
                if (result is true) return Succeed(spec, call, lines);
                var reason = values[values.Length - 1] as string;
                if (spec.Blocked is { } b)
                    lines.Add(ActionCodec.Line(Colors.Yellow.ToUnsignedInteger(),
                        T(b.Key, "{TAG} {REASON}", ("REASON", string.IsNullOrEmpty(reason) ? T(b.UnknownKey, b.UnknownText) : reason!))));
                return false;
        }
    }

    private static bool Succeed(Spec spec, Call call, List<string> lines)
    {
        if (spec.Success != null)
        {
            var (key, text, vars) = spec.Success(call);
            var color = spec.SuccessIsYellow ? Colors.Yellow : Colors.Green;
            lines.Add(ActionCodec.Line(color.ToUnsignedInteger(), T(key, text, vars)));
        }
        return true;
    }

    private static bool RunTreaty(Hero hero, IList<string> args, List<string> lines)
    {
        if (args.Count != 2 || _createAgreement == null) return Refuse(lines, "The server did not understand the request.");
        var a = Kingdom.All.FirstOrDefault(k => k.StringId == args[0]);
        var b = Kingdom.All.FirstOrDefault(k => k.StringId == args[1]);
        if (a == null || b == null) return Refuse(lines, "That kingdom was not found on the server.");
        if (a.Leader != hero && b.Leader != hero)
            return Refuse(lines, new TextObject("{=ml_le_treaty_leader}Only a kingdom's ruler can sign a trade agreement.").ToString());
        var behavior = Reflect.BehaviorFor(_createAgreement.DeclaringType!);
        if (behavior == null) return Refuse(lines, "Living Economy is not running on this server.");
        var values = new object?[] { a, b, true, null };
        var created = _createAgreement.Invoke(behavior, values) is true;
        var reason = values[3] as string;
        if (created || reason == "already_active") return true;
        Log.Info($"trade treaty {a.StringId} <-> {b.StringId} refused: {reason}");
        return Refuse(lines, new TextObject("{=ml_le_treaty_refused}The trade agreement could not be signed ({REASON}).")
            .SetTextVariable("REASON", reason ?? "?").ToString());
    }

    private static bool Refuse(List<string> lines, string text)
    {
        lines.Add(ActionCodec.Line(Colors.Yellow.ToUnsignedInteger(), text));
        return false;
    }

    /// <summary>The mod's own Loc.T: a TextObject with the key, the English fallback and the {TAG} prefix filled in.</summary>
    private static string T(string key, string fallback, params (string Name, object Value)[] vars)
    {
        var text = new TextObject("{=" + key + "}" + fallback);
        text.SetTextVariable("TAG", new TextObject("{=bee_tag}[LivingEconomy]").ToString());
        foreach (var (name, value) in vars) text.SetTextVariable(name, value?.ToString() ?? "");
        return text.ToString();
    }

    internal static string Summary() => $"actions sent {_sent}, carried out {_ran}";
}

/// <summary>Small reflection helpers over Living Economy's own types.</summary>
internal static class Reflect
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>The live instance of a behaviour type: the campaign's copy, else the type's static Instance.</summary>
    internal static object? BehaviorFor(Type type) =>
        Layer.Behavior(type) ?? type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

    /// <summary>Calls an instance method by name and argument count; null when it is missing or throws.</summary>
    internal static object? Invoke(object? instance, string method, params object?[] args)
    {
        if (instance == null) return null;
        try
        {
            var m = instance.GetType().GetMethods(Any).FirstOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length);
            return m?.Invoke(instance, args);
        }
        catch { return null; }
    }

    /// <summary>Calls a "bool CanX(Settlement, out string reason)" check.</summary>
    internal static (bool Ok, string? Reason) CanWithReason(object? instance, string method, Settlement settlement)
    {
        if (instance == null) return (false, null);
        var m = instance.GetType().GetMethods(Any).FirstOrDefault(x => x.Name == method && x.GetParameters().Length == 2);
        if (m == null) return (true, null);   // the mod dropped the check; its own method still guards the change
        var args = new object?[] { settlement, null };
        var ok = m.Invoke(instance, args) is true;
        return (ok, args[1] as string);
    }

    private static PropertyInfo? _lastVisited;
    private static bool _lastVisitedLooked;

    /// <summary>
    /// The party's last visited settlement, read by name: a player who opens a menu and the server's view of their party
    /// can be a moment apart (leaving the town as the request arrives). Null when this game version has no such member.
    /// </summary>
    internal static object? LastVisited(TaleWorlds.CampaignSystem.Party.MobileParty party)
    {
        if (!_lastVisitedLooked)
        {
            _lastVisitedLooked = true;
            _lastVisited = typeof(TaleWorlds.CampaignSystem.Party.MobileParty).GetProperty("LastVisitedSettlement", BindingFlags.Public | BindingFlags.Instance);
        }
        try { return _lastVisited?.GetValue(party); } catch { return null; }
    }

    internal static object? Field(object? instance, string field) =>
        instance?.GetType().GetField(field, Any)?.GetValue(instance);

    /// <summary>A value from Living Economy's static settings class (BetterEconomySettings).</summary>
    internal static object? Setting(string name) =>
        AccessTools.TypeByName("BetterEconomy.Config.BetterEconomySettings")?.GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
}
