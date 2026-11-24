using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Localization;

namespace BellumCivileCoopCompat
{
    // Player agency, Bellum's own actions (docs/BELLUM-COOP-COVERAGE.md, stage 3d, A3 and A4): titles, lordships, claim
    // feud petitions, succession law, an elective succession vote, dismissing a councillor.
    //
    // Each of these buttons shows Bellum's own screens and questions and then calls one of Bellum's methods that changes
    // the campaign (FeudalTitlePlayerActionService.TryExecuteGrant, RealmLawBehavior.TryApplyPlayerLaw, ...), with no
    // UI inside it. On a player's PC that final call is caught: it is sent to the server with its arguments by id (the
    // player's own clan and hero as "the player", since a player's ids differ between PCs), and does nothing here. The
    // server runs the same method as that player (Bellum's own checks and costs apply), tells them what Bellum answered
    // when it refused, and sends them its state so their screens show the result.
    public static class RemoteCalls
    {
        private const string B = "BellumCivile.Behaviors.";

        /// <summary>Bellum's methods a player's PC sends to the server: (type, method).</summary>
        public static readonly (string Type, string Method)[] Targets =
        {
            ("BellumCivile.FeudalTitlePlayerActionService", "TryExecuteRenunciation"),
            ("BellumCivile.FeudalTitlePlayerActionService", "TryExecuteUsurpation"),
            ("BellumCivile.FeudalTitlePlayerActionService", "TryExecuteRevocation"),
            ("BellumCivile.FeudalTitlePlayerActionService", "TryExecuteDissolution"),
            ("BellumCivile.FeudalTitlePlayerActionService", "TryExecuteRename"),
            ("BellumCivile.FeudalTitlePlayerActionService", "TrySetServiceLevel"),
            ("BellumCivile.FeudalTitlePlayerActionService", "TryExecuteGrant"),
            (B + "FeudalClaimFabricationBehavior", "TryStartFabrication"),
            (B + "SuccessionChallengeBehavior", "StartPlayerChallenge"),
            (B + "LordshipBehavior", "TryRevoke"),
            (B + "LordshipBehavior", "TryGrantFromPlayerHouse"),
            (B + "ClaimFeudBehavior", "TryPetitionFeud"),
            (B + "PrivyCouncilBehavior", "TryDismissOfficeHolderByRuler"),
            (B + "RealmLawBehavior", "TryApplyPlayerLaw"),
            (B + "ElectiveSuccessionBehavior", "SetPlayerSupport"),
        };

        public const string Sent = "Sent to the server, which carries it out for you.";

        private static readonly Dictionary<string, MethodInfo> ByKey = new Dictionary<string, MethodInfo>();
        private static readonly Dictionary<MethodBase, string> KeyOf = new Dictionary<MethodBase, string>();
        private static int _sent, _done;

        /// <summary>What the server last carried out for a player, for tests: "key result".</summary>
        public static readonly List<string> Carried = new List<string>();

        public static string Summary() => _done > 0 ? "; Bellum actions carried out for players " + _done : _sent > 0 ? "; Bellum actions sent to the server " + _sent : "";

        public static void Install(Harmony harmony)
        {
            var missing = new List<string>();
            foreach (var (typeName, name) in Targets)
            {
                var type = AccessTools.TypeByName(typeName);
                var methods = type?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name == name && !m.IsAbstract && !m.IsGenericMethodDefinition).ToList() ?? new List<MethodInfo>();
                var method = methods.Count == 1 ? methods[0] : null;
                if (method == null || method.GetParameters().Any(p => !p.IsOut && !Encodable(p.ParameterType)))
                {
                    missing.Add(Short(typeName) + "." + name + (methods.Count > 1 ? " (overloaded)" : ""));
                    continue;
                }
                var key = Short(typeName) + "." + name;
                ByKey[key] = method;
                KeyOf[method] = key;
                var prefix = method.ReturnType == typeof(bool) ? nameof(SendBool) : method.ReturnType == typeof(void) ? nameof(SendVoid) : null;
                if (prefix == null) { missing.Add(key + " (returns " + method.ReturnType.Name + ")"); continue; }
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(RemoteCalls), prefix) { priority = Priority.First });
            }
            Agency.Handle("call", OnCall);
            SubModule.Log("Bellum actions: a player's titles, lordships, feud petitions, succession law and council dismissals are carried out by the server ("
                + ByKey.Count + " of " + Targets.Length + ").");
            if (missing.Count > 0)
                SubModule.Log("Bellum actions: not found in this Bellum version: " + string.Join(", ", missing));
        }

        private static string Short(string typeName) => typeName.Substring(typeName.LastIndexOf('.') + 1);

        // ---- player's PC: the call goes to the server ----

        private static bool SendBool(MethodBase __originalMethod, object[] __args, ref bool __result)
        {
            if (!Coop.OnPlayerPc)
                return true;
            __result = false;
            Send(__originalMethod, __args);
            return false;
        }

        private static bool SendVoid(MethodBase __originalMethod, object[] __args)
        {
            if (!Coop.OnPlayerPc)
                return true;
            Send(__originalMethod, __args);
            return false;
        }

        private static void Send(MethodBase method, object[] args)
        {
            var parameters = method.GetParameters();
            var sent = false;
            if (KeyOf.TryGetValue(method, out var key) && Agency.Channel != null)
            {
                var tokens = new List<string> { key };
                for (var i = 0; i < parameters.Length; i++)
                    tokens.Add(parameters[i].IsOut ? "~" : Encode(args[i]));
                sent = Agency.ToServer("call", tokens.ToArray());
            }
            if (sent) _sent++;
            var line = sent ? Sent : PlayerActions.Line;
            // Bellum shows the reason it gives back; say where the action went.
            for (var i = 0; i < parameters.Length; i++)
            {
                if (!parameters[i].IsOut) continue;
                var type = parameters[i].ParameterType.GetElementType();
                if (type == typeof(string)) args[i] = line;
                else if (type == typeof(TextObject)) args[i] = new TextObject("{=!}" + line);
            }
            SubModule.Tell(line);
        }

        // ---- arguments by id ----

        private static bool Encodable(Type type) =>
            type == typeof(Clan) || type == typeof(Hero) || type == typeof(Kingdom) || type == typeof(Settlement) || type == typeof(string)
            || type == typeof(bool) || type == typeof(int) || type == typeof(float) || type.IsEnum || type.Name == "FeudalTitleRecord";

        private static string Encode(object value)
        {
            switch (value)
            {
                case null: return "~";
                case Clan clan: return ReferenceEquals(clan, Clan.PlayerClan) ? "@clan" : "c:" + clan.StringId + "|" + clan.Leader?.Name;
                case Hero hero: return ReferenceEquals(hero, Hero.MainHero) ? "@hero" : "h:" + hero.StringId + "|" + hero.Name;
                case Kingdom kingdom: return "k:" + kingdom.StringId + "|" + kingdom.Name;
                case Settlement settlement: return "s:" + settlement.StringId;
                case string text: return "x:" + text;
                case bool flag: return "v:" + (flag ? "1" : "0");
                case int number: return "v:" + number.ToString(CultureInfo.InvariantCulture);
                case float number: return "v:" + number.ToString("R", CultureInfo.InvariantCulture);
                case Enum e: return "e:" + e;
            }
            var title = AccessTools.Property(value.GetType(), "TitleId")?.GetValue(value, null) as string;
            return title != null ? "t:" + title : "~";
        }

        private static object Decode(string token, Type type, Hero sender)
        {
            if (token == "~") return null;
            if (token == "@clan") return sender.Clan;
            if (token == "@hero") return sender;
            var body = token.Length > 2 ? token.Substring(2) : "";
            string Id() => body.Split('|')[0];
            string Name() => body.IndexOf('|') >= 0 ? body.Substring(body.IndexOf('|') + 1) : "";
            switch (token[0])
            {
                case 'c': return Clan.All.FirstOrDefault(c => c?.StringId == Id()) ?? Clan.All.FirstOrDefault(c => Name().Length > 0 && c.Leader?.Name?.ToString() == Name());
                case 'h': return Hero.FindFirst(h => h.StringId == Id()) ?? Hero.AllAliveHeroes.FirstOrDefault(h => Name().Length > 0 && h.Name?.ToString() == Name());
                case 'k': return Kingdom.All.FirstOrDefault(k => k?.StringId == Id()) ?? Kingdom.All.FirstOrDefault(k => Name().Length > 0 && k.Name?.ToString() == Name());
                case 's': return Settlement.Find(body);
                case 'x': return body;
                case 'v':
                    if (type == typeof(bool)) return body == "1";
                    if (type == typeof(int)) return int.Parse(body, CultureInfo.InvariantCulture);
                    if (type == typeof(float)) return float.Parse(body, CultureInfo.InvariantCulture);
                    return null;
                case 'e': return Enum.Parse(type, body);
                case 't':
                    var titles = Behaviour("BellumCivile.Behaviors.FeudalTitleBehavior");
                    return titles == null ? null : AccessTools.Method(titles.GetType(), "GetTitle", new[] { typeof(string) })?.Invoke(titles, new object[] { body });
            }
            return null;
        }

        private static object Behaviour(string typeName)
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null || Campaign.Current == null) return null;
            try
            {
                return typeof(Campaign).GetMethod(nameof(Campaign.GetCampaignBehavior))?.MakeGenericMethod(type).Invoke(Campaign.Current, null)
                       ?? AccessTools.Property(type, "Instance")?.GetValue(null, null);
            }
            catch { return null; }
        }

        // ---- server: the same call, as that player ----

        private static void OnCall(Hero sender, string[] args)
        {
            if (args.Length < 1 || !ByKey.TryGetValue(args[0], out var method))
                return;
            var parameters = method.GetParameters();
            if (args.Length != parameters.Length + 1)
            {
                SubModule.Log("Bellum actions: " + args[0] + " from " + sender.StringId + " came with the wrong arguments; ignored.");
                return;
            }
            var values = new object[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].IsOut) continue;
                try { values[i] = Decode(args[i + 1], parameters[i].ParameterType, sender); }
                catch { values[i] = null; }
                if (values[i] == null && args[i + 1] != "~")
                {
                    Agency.ToPlayer(sender, "notice", "That is no longer there.");
                    SubModule.Log("Bellum actions: " + args[0] + " from " + sender.StringId + ": " + args[i + 1] + " is no longer there; ignored.");
                    return;
                }
            }
            var instance = method.IsStatic ? null : Behaviour(method.DeclaringType.FullName);
            if (!method.IsStatic && instance == null)
                return;
            object result = null;
            var ok = Prompts.RunAsking(sender, "Bellum's " + args[0], () => result = method.Invoke(instance, values));
            var reason = string.Join(" ", parameters.Select((p, i) => p.IsOut ? values[i] : null).OfType<object>()
                .Where(v => v is string || v is TextObject).Select(v => v.ToString()).Where(s => s.Length > 0));
            var outcome = !ok ? "not carried out" : result is bool b ? (b ? "done" : "refused") : "done";
            if (ok) _done++;
            lock (Carried) { Carried.Add(args[0] + " " + outcome); if (Carried.Count > 50) Carried.RemoveAt(0); }
            SubModule.Log("Carried out " + args[0] + " for " + sender.StringId + ": " + outcome + (reason.Length > 0 ? " (" + reason + ")" : "") + ".");
            if (outcome == "refused" && reason.Length > 0)
                Agency.ToPlayer(sender, "notice", reason);
            StateMirror.SendTo(sender);
        }
    }
}
