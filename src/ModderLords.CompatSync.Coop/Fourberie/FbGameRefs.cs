using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.Core;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.ObjectSystem;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>
/// The game objects a Fourberie book refers to, written by StringId and found again on the other machine. Coop gives a
/// party the server creates the same StringId on every player's game, so ids are the right join key. A time is its
/// tick count, a troop roster row is (character id, count, wounded, xp).
/// </summary>
internal sealed class FbGameRefs : IBookRefs
{
    internal static readonly FbGameRefs Instance = new FbGameRefs();

    private static readonly FieldInfo? Ticks = typeof(CampaignTime).GetField("_numTicks", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly ConstructorInfo? FromTicks = typeof(CampaignTime).GetConstructor(
        BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(long) }, null);

    /// <summary>
    /// On a player's game: whether Coop knows a party (it exists on the server too). A party only this game has (a
    /// phantom Coop never shares) is not written, so its id never reaches the server's copy of the book. Null on the server.
    /// </summary>
    internal static Func<object, bool>? IsShared { get; set; }

    /// <summary>Null when this game build moved what the time codec needs.</summary>
    internal static string? Problem => Ticks == null || FromTicks == null ? "CampaignTime's tick count is not reachable" : null;

    public bool TryEncode(object value, out JToken token)
    {
        token = JValue.CreateNull();
        switch (value)
        {
            case CampaignTime t:
                token = (long)Ticks!.GetValue(t);
                return true;
            case TroopRosterElement e:
                token = new JArray(e.Character?.StringId, e.Number, e.WoundedNumber, e.Xp);
                return true;
            case Village v:
                token = v.Settlement?.StringId;
                return true;
            case MobileParty p:
                if (IsShared != null && !IsShared(p)) return false;
                token = p.StringId;
                return true;
            case Hero h:
                token = h.StringId;
                return true;
            case Settlement s:
                token = s.StringId;
                return true;
            case Clan c:
                token = c.StringId;
                return true;
            case Kingdom k:
                token = k.StringId;
                return true;
            case CharacterObject ch:
                token = ch.StringId;
                return true;
            case PartyBase pb:
                if (pb.MobileParty != null) { if (IsShared != null && !IsShared(pb.MobileParty)) return false; token = "p:" + pb.MobileParty.StringId; }
                else if (pb.Settlement != null) token = "s:" + pb.Settlement.StringId;
                else return false;
                return true;
            case SkillObject skill:
                token = skill.StringId;
                return true;
            case TraitObject trait:
                token = trait.StringId;
                return true;
            case Alley alley:
                if (alley.Settlement == null) return false;
                token = new JArray(alley.Settlement.StringId, alley.Settlement.Alleys.IndexOf(alley));
                return true;
            default:
                return false;
        }
    }

    public bool TryDecode(Type type, JToken token, out object? value, out string? problem)
    {
        value = null;
        problem = null;
        if (type == typeof(CampaignTime))
        {
            if (token.Type != JTokenType.Integer) { problem = "time is not a tick count"; value = CampaignTime.Zero; return true; }
            value = FromTicks!.Invoke(new object[] { (long)token });
            return true;
        }
        if (type == typeof(TroopRosterElement))
        {
            if (token is not JArray { Count: 4 } a || a[0].Type != JTokenType.String) { problem = "troop row is malformed"; return true; }
            var character = MBObjectManager.Instance.GetObject<CharacterObject>((string)a[0]!);
            if (character == null) { problem = "troop '" + a[0] + "' not found"; return true; }
            var number = Math.Max(0, (int)a[1]!);
            value = new TroopRosterElement(character) { Number = number, WoundedNumber = Math.Min(number, Math.Max(0, (int)a[2]!)), Xp = Math.Max(0, (int)a[3]!) };
            return true;
        }

        if (type == typeof(Alley))
        {
            if (token is not JArray { Count: 2 } a || a[0].Type != JTokenType.String || a[1].Type != JTokenType.Integer) { problem = "alley is malformed"; return true; }
            var alleys = Settlement.Find((string)a[0]!)?.Alleys;
            var index = (int)a[1]!;
            value = alleys != null && index >= 0 && index < alleys.Count ? alleys[index] : null;
            if (value == null) problem = "alley " + a + " not found";
            return true;
        }
        if (type == typeof(PartyBase))
        {
            var text = token.Type == JTokenType.String ? (string)token! : "";
            value = text.StartsWith("p:", StringComparison.Ordinal) ? MobileParty.All.FirstOrDefault(p => p.StringId == text.Substring(2))?.Party
                : text.StartsWith("s:", StringComparison.Ordinal) ? Settlement.Find(text.Substring(2))?.Party : null;
            if (value == null) problem = "party '" + text + "' not found";
            return true;
        }

        if (!IsRef(type)) return false;
        if (token.Type != JTokenType.String) { problem = type.Name + " reference is not an id"; return true; }
        var id = (string)token!;
        value = Find(type, id);
        if (value == null) problem = type.Name + " '" + id + "' not found";
        return true;
    }

    private static bool IsRef(Type t) =>
        t == typeof(Hero) || t == typeof(MobileParty) || t == typeof(Settlement) || t == typeof(Village)
        || t == typeof(Clan) || t == typeof(Kingdom) || t == typeof(CharacterObject) || t == typeof(IFaction)
        || t == typeof(SkillObject) || t == typeof(TraitObject);

    private static object? Find(Type type, string id)
    {
        if (type == typeof(Hero)) return MBObjectManager.Instance.GetObject<Hero>(id) ?? Hero.FindFirst(h => h.StringId == id);
        if (type == typeof(Settlement)) return Settlement.Find(id);
        if (type == typeof(Village)) return Settlement.Find(id)?.Village;
        if (type == typeof(MobileParty)) return MobileParty.All.FirstOrDefault(p => p.StringId == id);
        if (type == typeof(Clan)) return Clan.All.FirstOrDefault(c => c.StringId == id);
        if (type == typeof(Kingdom)) return Kingdom.All.FirstOrDefault(k => k.StringId == id);
        if (type == typeof(CharacterObject)) return MBObjectManager.Instance.GetObject<CharacterObject>(id);
        if (type == typeof(IFaction)) return (IFaction?)Kingdom.All.FirstOrDefault(k => k.StringId == id) ?? Clan.All.FirstOrDefault(c => c.StringId == id);
        if (type == typeof(SkillObject)) return MBObjectManager.Instance.GetObject<SkillObject>(id);
        if (type == typeof(TraitObject)) return MBObjectManager.Instance.GetObject<TraitObject>(id);
        return null;
    }
}
