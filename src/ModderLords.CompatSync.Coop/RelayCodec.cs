using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.ObjectSystem;

namespace ModderLords.CompatSync.Coop;

/// <summary>Arguments of a relayed call on the wire: primitives as invariant text, game objects by StringId (a Town by its settlement's).</summary>
public static class RelayCodec
{
    public static bool TryEncode(object?[] args, List<string> kinds, List<string> values, out string why)
    {
        why = "";
        foreach (var a in args)
        {
            switch (a)
            {
                case bool b: kinds.Add("b"); values.Add(b ? "1" : "0"); break;
                case int i: kinds.Add("i"); values.Add(i.ToString(CultureInfo.InvariantCulture)); break;
                case long l: kinds.Add("l"); values.Add(l.ToString(CultureInfo.InvariantCulture)); break;
                case float f: kinds.Add("f"); values.Add(f.ToString("R", CultureInfo.InvariantCulture)); break;
                case double d: kinds.Add("d"); values.Add(d.ToString("R", CultureInfo.InvariantCulture)); break;
                case string s: kinds.Add("s"); values.Add(s); break;
                case Town t when t.Settlement != null: kinds.Add("Town"); values.Add(t.Settlement.StringId); break;
                case Settlement st: kinds.Add("Settlement"); values.Add(st.StringId); break;
                case Hero h: kinds.Add("Hero"); values.Add(h.StringId); break;
                case Clan c: kinds.Add("Clan"); values.Add(c.StringId); break;
                case MobileParty p: kinds.Add("MobileParty"); values.Add(p.StringId); break;
                case CharacterObject co: kinds.Add("CharacterObject"); values.Add(co.StringId); break;
                case ItemObject it: kinds.Add("ItemObject"); values.Add(it.StringId); break;
                case CultureObject cu: kinds.Add("CultureObject"); values.Add(cu.StringId); break;
                default:
                    why = "an argument of type " + (a?.GetType().Name ?? "null") + " cannot be sent";
                    return false;
            }
        }
        return true;
    }

    public static bool TryDecode(IList<string> kinds, IList<string> values, ParameterInfo[] parameters, out object?[] args, out string why)
    {
        args = new object?[parameters.Length];
        why = "";
        if (kinds.Count != parameters.Length || values.Count != parameters.Length)
        {
            why = $"expected {parameters.Length} argument(s), got {kinds.Count}";
            return false;
        }
        for (var i = 0; i < parameters.Length; i++)
        {
            var text = values[i];
            object? value;
            try
            {
                value = kinds[i] switch
                {
                    "b" => text == "1",
                    "i" => int.Parse(text, CultureInfo.InvariantCulture),
                    "l" => long.Parse(text, CultureInfo.InvariantCulture),
                    "f" => float.Parse(text, CultureInfo.InvariantCulture),
                    "d" => double.Parse(text, CultureInfo.InvariantCulture),
                    "s" => text,
                    "Town" => Settlement.Find(text)?.Town,
                    "Settlement" => Settlement.Find(text),
                    "Hero" => Hero.FindFirst(h => h.StringId == text),
                    "Clan" => Clan.FindFirst(c => c.StringId == text),
                    "MobileParty" => MobileParty.All.FirstOrDefault(p => p.StringId == text),
                    "CharacterObject" => MBObjectManager.Instance.GetObject<CharacterObject>(text),
                    "ItemObject" => MBObjectManager.Instance.GetObject<ItemObject>(text),
                    "CultureObject" => MBObjectManager.Instance.GetObject<CultureObject>(text),
                    _ => null,
                };
            }
            catch (Exception ex) when (ex is FormatException or OverflowException) { value = null; }
            if (value is null || !parameters[i].ParameterType.IsInstanceOfType(value))
            {
                why = $"argument {i + 1} ({kinds[i]} '{text}') was not found or has the wrong type";
                return false;
            }
            args[i] = value;
        }
        return true;
    }
}
