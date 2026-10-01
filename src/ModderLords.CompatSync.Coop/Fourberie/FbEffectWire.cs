using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>
/// The wire form of fourb-effects (docs/FOURBERIE-LAYER-PLAN.md, phase 6, tier T2): what Fourberie did to the world on a
/// player's game, for the server to do again as that player. Three kinds of op, in order:
/// - "call": a game action replayed with the same arguments (by key and JSON values, as relayed methods are);
/// - "delta": an amount added to a settlement's or clan's number (security, loyalty, prosperity, food, militia, influence);
/// - "troops" / "items": the net change to the player's own party, per troop or item.
/// Bounds are checked on both ends; anything out of bounds is dropped, never clamped silently into a different action.
/// Free of game and Coop types so the tests compile it in.
/// </summary>
public static class FbEffectWire
{
    public const int MaxOps = 200;
    public const int MaxPayload = 256 * 1024;

    /// <summary>Largest believable single change, per delta target property.</summary>
    public static readonly IReadOnlyDictionary<string, float> DeltaBounds = new Dictionary<string, float>(StringComparer.Ordinal)
    {
        ["Security"] = 100, ["Loyalty"] = 100, ["Prosperity"] = 100000, ["FoodStocks"] = 100000, ["Militia"] = 1000, ["Influence"] = 100000,
    };

    public const int MaxTroopChange = 2000;
    public const int MaxItemChange = 100000;

    public sealed class Op
    {
        public string Kind = "";
        public string Key = "";          // call: method key; delta: "town|settlement|clan:Property"; troops: "m" or "p"; items: ""
        public string Target = "";       // delta: object id; troops/items: character or item id
        public JArray Args = new JArray(); // call: argument values
        public float Amount;             // delta: amount; troops/items: count
        public int Wounded, Xp;          // troops
        public string Modifier = "";     // items
    }

    public static Op Call(string key, JArray args) => new Op { Kind = "call", Key = key, Args = args };
    public static Op Delta(string targetKind, string property, string id, float amount) => new Op { Kind = "delta", Key = targetKind + ":" + property, Target = id, Amount = amount };

    public static string Pack(IEnumerable<Op> ops)
    {
        var array = new JArray();
        foreach (var op in ops.Take(MaxOps))
            array.Add(new JObject
            {
                ["k"] = op.Kind, ["c"] = op.Key, ["t"] = op.Target, ["a"] = op.Args, ["n"] = op.Amount,
                ["w"] = op.Wounded, ["x"] = op.Xp, ["m"] = op.Modifier,
            });
        return array.ToString(Formatting.None);
    }

    /// <summary>The ops in a payload; null when the payload is malformed or out of bounds (then nothing at all is applied).</summary>
    public static List<Op>? Unpack(string payload)
    {
        if (payload.Length > MaxPayload) return null;
        try
        {
            if (JToken.Parse(payload) is not JArray array || array.Count > MaxOps) return null;
            var ops = new List<Op>();
            foreach (var t in array)
            {
                if (t is not JObject o) return null;
                var op = new Op
                {
                    Kind = (string?)o["k"] ?? "", Key = (string?)o["c"] ?? "", Target = (string?)o["t"] ?? "",
                    Args = o["a"] as JArray ?? new JArray(), Amount = (float?)o["n"] ?? 0f, Wounded = (int?)o["w"] ?? 0,
                    Xp = (int?)o["x"] ?? 0, Modifier = (string?)o["m"] ?? "",
                };
                if (!Valid(op)) return null;
                ops.Add(op);
            }
            return ops;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException or InvalidCastException or ArgumentException) { return null; }
    }

    public static bool Valid(Op op)
    {
        if (float.IsNaN(op.Amount) || float.IsInfinity(op.Amount)) return false;
        switch (op.Kind)
        {
            case "call":
                return op.Key.Length is > 0 and < 200 && op.Args.Count <= 12;
            case "delta":
            {
                var colon = op.Key.IndexOf(':');
                if (colon <= 0 || op.Target.Length == 0) return false;
                var kind = op.Key.Substring(0, colon);
                var property = op.Key.Substring(colon + 1);
                return kind is "town" or "settlement" or "clan" && DeltaBounds.TryGetValue(property, out var bound) && Math.Abs(op.Amount) <= bound;
            }
            case "troops":
                return op.Key is "m" or "p" && op.Target.Length > 0 && Math.Abs(op.Amount) <= MaxTroopChange
                    && Math.Abs(op.Wounded) <= MaxTroopChange && Math.Abs(op.Xp) <= 10_000_000 && op.Amount == Math.Round(op.Amount);
            case "items":
                return op.Target.Length > 0 && Math.Abs(op.Amount) <= MaxItemChange && op.Amount == Math.Round(op.Amount);
            default:
                return false;
        }
    }
}

/// <summary>
/// Net changes to the player's own party, per troop or item, until they can be sent. Fourberie takes the party's troops
/// out for a mission and puts them back afterwards; netted, that is no change at all, and nothing is sent.
/// </summary>
public sealed class FbRosterNet
{
    private readonly Dictionary<(string Roster, string Id), (int Count, int Wounded, int Xp)> _troops = new();
    private readonly Dictionary<(string Item, string Modifier), int> _items = new();

    public bool IsEmpty => _troops.Count == 0 && _items.Count == 0;

    public void Troops(string roster, string characterId, int count, int wounded, int xp)
    {
        _troops.TryGetValue((roster, characterId), out var v);
        _troops[(roster, characterId)] = (v.Count + count, v.Wounded + wounded, v.Xp + xp);
    }

    public void Items(string itemId, string modifierId, int amount)
    {
        _items.TryGetValue((itemId, modifierId), out var v);
        _items[(itemId, modifierId)] = v + amount;
    }

    /// <summary>The non-zero net changes as ops, in a stable order; empties the accumulator.</summary>
    public List<FbEffectWire.Op> Drain()
    {
        var ops = new List<FbEffectWire.Op>();
        foreach (var pair in _troops.OrderBy(p => p.Key.Roster, StringComparer.Ordinal).ThenBy(p => p.Key.Id, StringComparer.Ordinal))
            if (pair.Value.Count != 0 || pair.Value.Wounded != 0 || pair.Value.Xp != 0)
                ops.Add(new FbEffectWire.Op { Kind = "troops", Key = pair.Key.Roster, Target = pair.Key.Id, Amount = pair.Value.Count, Wounded = pair.Value.Wounded, Xp = pair.Value.Xp });
        foreach (var pair in _items.OrderBy(p => p.Key.Item, StringComparer.Ordinal).ThenBy(p => p.Key.Modifier, StringComparer.Ordinal))
            if (pair.Value != 0)
                ops.Add(new FbEffectWire.Op { Kind = "items", Target = pair.Key.Item, Modifier = pair.Key.Modifier, Amount = pair.Value });
        _troops.Clear();
        _items.Clear();
        return ops;
    }
}
