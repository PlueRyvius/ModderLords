using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using ModderLords.CompatSync.Coop.Taom;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>
/// fourb-parties, relay half (docs/FOURBERIE-LAYER-PLAN.md, phase 4, tier T1). Some of Fourberie's player actions put new
/// parties into the world: a caravan to "insure", the raiders hired to hit it. Made on a player's game they would be
/// phantoms (Coop shares no party a player's game creates), and the scheme could never play out. These methods are
/// carried out by the server instead: the player's game skips the method and sends its arguments (heroes, settlements,
/// numbers, by id), and the server runs the same Fourberie method as that player, with their book. The parties
/// replicate to every game, and the book rows the method set (which caravan, which raiders) come back to the player.
///
/// Only methods in <see cref="Methods"/> are carried, only void ones, and only with arguments the book codec can write;
/// each is run in order, so a second method can rely on what the first set up.
/// </summary>
internal static class FbRelay
{
    internal static (string Type, string Method, int Params)[] Methods => FbRelayTable.Methods;

    private static readonly Dictionary<string, MethodInfo> ByKey = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
    private static readonly Dictionary<MethodBase, string> Keys = new Dictionary<MethodBase, string>();
    private static long _sent, _ran, _refused;

    internal static string KeyOf(MethodBase m) => m.DeclaringType?.FullName + "::" + m.Name;

    internal static void Bind(IEnumerable<MethodInfo> methods)
    {
        foreach (var m in methods)
        {
            var key = KeyOf(m);
            ByKey[key] = m;
            Keys[m] = key;
        }
    }

    /// <summary>Client prefix: in a co-op session the server does this; send it there and skip it here.</summary>
    internal static bool ClientPrefix(object?[] __args, MethodBase __originalMethod)
    {
        if (!FourberieLayer.IsCoopClient || !Keys.TryGetValue(__originalMethod, out var key)) return true;
        var parameters = __originalMethod.GetParameters();
        var args = new JArray();
        for (var i = 0; i < parameters.Length; i++)
        {
            if (!FbBookCodec.TryEncodeValue(parameters[i].ParameterType, __args[i], FbGameRefs.Instance, out var token, out var problem))
            {
                Log.Warn($"{FourberieLayer.Tag}relay: {key} cannot be sent ({parameters[i].Name}: {problem}); it runs on this game only");
                return true;
            }
            args.Add(token);
        }
        TaomActions.Send?.Invoke(FourberieLayer.Feature, "relay", new[] { key, args.ToString(Formatting.None) });
        _sent++;
        Log.Info($"{FourberieLayer.Tag}relay: sent {__originalMethod.Name} to the server");
        return false;
    }

    /// <summary>Server, game thread, inside TaomActions.Run: runs one relayed method as the sender, with their book.</summary>
    internal static TaomActionOutcome Run(Hero hero, MobileParty? party, IList<string> args)
    {
        if (args.Count != 2 || !ByKey.TryGetValue(args[0], out var method) || args[1].Length > 64 * 1024) return Refuse("");
        JArray values;
        try { values = JArray.Parse(args[1]); }
        catch (JsonException) { return Refuse(""); }
        var parameters = method.GetParameters();
        if (values.Count != parameters.Length) return Refuse("");
        var decoded = new object?[parameters.Length];
        var problems = new List<string>();
        for (var i = 0; i < parameters.Length; i++)
        {
            if (!FbBookCodec.TryDecodeValue(parameters[i].ParameterType, values[i], FbGameRefs.Instance, problems, parameters[i].Name, out decoded[i])
                || (decoded[i] == null && values[i].Type != JTokenType.Null))
            {
                Log.Warn($"{FourberieLayer.Tag}relay: {method.Name} from {hero.Name} refused: {FourberieLayer.Some(problems, 2)}");
                return Refuse("The server could not carry this out (" + method.Name + ").");
            }
        }
        var running = TaomActions.Running;
        TaomActions.Running = false;   // let what the method shows reach the player through the notice forwarder
        try
        {
            using var scope = FbBooks.Enter(hero, party);
            method.Invoke(null, decoded);
            _ran++;
            Log.Info($"{FourberieLayer.Tag}relay: {method.Name} ran for {hero.Name}");
            return new TaomActionOutcome(true, "");
        }
        catch (Exception ex)
        {
            Log.Warn($"{FourberieLayer.Tag}relay: {method.Name} failed for {hero.Name}: {ex.GetBaseException().Message}");
            return Refuse("The server could not carry this out (" + method.Name + ").");
        }
        finally { TaomActions.Running = running; }
    }

    private static TaomActionOutcome Refuse(string message)
    {
        _refused++;
        return TaomActionOutcome.Fail(message);
    }

    internal static string Summary() => $"relayed sent {_sent}, ran {_ran}, refused {_refused}";
}

internal sealed class FbRelayComponent : IFbComponent
{
    private const string Owner = "ModderLords.Fourberie.Relay";
    private readonly List<MethodInfo> _found = new List<MethodInfo>();

    public string Id => "relay";

    public string? SkipReason(FbContext context)
    {
        if (context.Fields == null) return "no books: " + context.FieldsProblem;
        _found.Clear();
        var missing = new List<string>();
        foreach (var (type, name, count) in FbRelay.Methods)
        {
            var m = context.Method(type, name, count);
            if (m == null || !m.IsStatic || m.ReturnType != typeof(void)) missing.Add(type.Substring(type.LastIndexOf('.') + 1) + "." + name);
            else _found.Add(m);
        }
        if (_found.Count == 0) return "Fourberie changed; none of the relayed methods were found";
        if (missing.Count > 0) Log.Warn(FourberieLayer.Tag + "relay: not found (Fourberie changed?), these stay on players' games: " + FourberieLayer.Some(missing));
        return null;
    }

    public string Install(FbContext context)
    {
        FbRelay.Bind(_found);
        if (context.IsServer) return $"{_found.Count} player action(s) that create parties are carried out here for the player";
        var h = new Harmony(Owner);
        var prefix = new HarmonyMethod(typeof(FbRelay), nameof(FbRelay.ClientPrefix)) { priority = Priority.First };
        foreach (var m in _found) h.Patch(m, prefix: prefix);
        return $"{_found.Count} player action(s) that create parties are sent to the server";
    }
}
