using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;

namespace BellumCivileCoopCompat
{
    // Player agency, prompts (docs/BELLUM-COOP-COVERAGE.md, stage 3, A1): what Bellum would ask "the player", the server
    // asks the player concerned.
    //
    // A prompt site is one of Bellum's methods that, for the player, opens a question (an ultimatum to the player's realm,
    // say). On the server, when a site concerns a connected player, the site runs as that player (AsPlayer): Bellum's own
    // test "is this the player?" is then true, and it builds its question as in single player. The question it would
    // show (InformationManager.ShowInquiry) is caught instead, sent to that player's PC, and shown there. The player's
    // answer comes back, and the server runs Bellum's own yes or no, again as that player. If nobody answers in time, the
    // site's fallback answers (for an ultimatum: refuse). Nothing is ever shown on the server, so a question nobody can
    // answer never blocks Bellum there.
    //
    // A site that concerns a player who is not connected is not asked: the stage 2 guard (PlayersNotAi) keeps it from
    // happening, as before.
    public static class Prompts
    {
        /// <summary>How long a player has to answer before the site's fallback answers. Tests shorten it.</summary>
        public static double AnswerSeconds = 600;

        public sealed class Site
        {
            public Site(string type, string method, Func<object, object[], Hero> player, bool fallbackYes, string what)
            {
                Type = type; Method = method; Player = player; FallbackYes = fallbackYes; What = what;
            }

            public string Type { get; }
            public string Method { get; }
            /// <summary>The connected player this call concerns, or null (then the site runs as Bellum's AI would).</summary>
            public Func<object, object[], Hero> Player { get; }
            /// <summary>The answer when the player does not answer in time.</summary>
            public bool FallbackYes { get; }
            public string What { get; }
        }

        private sealed class Pending
        {
            public string Id;
            public Hero Hero;
            public Site Site;
            public InquiryData Data;
            public DateTime Expires;
        }

        public static readonly List<Site> Sites = new List<Site>
        {
            // A court faction's ultimatum to a realm a player rules: accept the demands, or refuse and face the rebellion.
            new Site("BellumCivile.FactionObject", "TriggerUltimatumDetailed", (instance, args) => RulerOf(Get(instance, "ParentKingdom") as Kingdom),
                fallbackYes: false, what: "an ultimatum to the player's realm"),
        };

        private static readonly Dictionary<MethodBase, Site> ByMethod = new Dictionary<MethodBase, Site>();
        private static readonly Dictionary<string, Pending> Open = new Dictionary<string, Pending>();
        [ThreadStatic] private static Frame _frame;
        private static int _next, _asked, _answered, _fallback;

        private sealed class Frame
        {
            public Hero Hero;
            public Site Site;
            public IDisposable Scope;
            public Frame Outer;
        }

        public static string Summary() => _asked > 0 ? "; questions put to players " + _asked + " (answered " + _answered + ", timed out " + _fallback + ")" : "";

        /// <summary>The prompts still waiting for an answer, for tests: "id kind hero".</summary>
        public static List<string> Waiting
        {
            get { lock (Open) return Open.Values.Select(p => p.Id + " " + p.Site.Method + " " + p.Hero.StringId).ToList(); }
        }

        public static void Install(Harmony harmony)
        {
            var missing = new List<string>();
            foreach (var site in Sites)
            {
                var type = AccessTools.TypeByName(site.Type);
                var methods = type?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name == site.Method && !m.IsAbstract).ToList() ?? new List<MethodInfo>();
                if (methods.Count == 0) { missing.Add(site.Type.Substring(site.Type.LastIndexOf('.') + 1) + "." + site.Method); continue; }
                foreach (var method in methods)
                {
                    ByMethod[method] = site;
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(Prompts), nameof(SitePrefix)) { priority = Priority.First },
                        finalizer: new HarmonyMethod(typeof(Prompts), nameof(SiteFinalizer)));
                }
            }
            var show = AccessTools.Method(typeof(InformationManager), nameof(InformationManager.ShowInquiry), new[] { typeof(InquiryData), typeof(bool), typeof(bool) });
            if (show == null) missing.Add("InformationManager.ShowInquiry");
            else harmony.Patch(show, prefix: new HarmonyMethod(typeof(Prompts), nameof(InquiryPrefix)) { priority = Priority.First });

            var notifications = AccessTools.TypeByName("BellumCivile.BellumCivileNotifications");
            foreach (var personal in notifications?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Where(m => m.Name == "ShowPersonal") ?? Enumerable.Empty<MethodInfo>())
                harmony.Patch(personal, prefix: new HarmonyMethod(typeof(Prompts), nameof(NoticePrefix)) { priority = Priority.First });

            Agency.Handle("answer", OnAnswer);
            Agency.Listen("prompt", ShowOnPlayerPc);
            SubModule.Log("Prompts: on the server, Bellum's questions to a connected player go to that player (" + Sites.Count + " sites).");
            if (missing.Count > 0)
                SubModule.Log("Prompts: not found in this Bellum version: " + string.Join(", ", missing));
        }

        /// <summary>
        /// Server: runs <paramref name="action"/> as <paramref name="hero"/> (a player's request), with every question
        /// Bellum asks on the way put to that player and every line it tells "the player" sent to them as a notice.
        /// </summary>
        public static bool RunAsking(Hero hero, string what, Action action)
        {
            if (action == null || !CanAsk(hero))
                return false;
            var scope = AsPlayer.Enter(hero);
            if (scope == null)
                return false;
            var site = new Site("", "", (i, a) => hero, fallbackYes: false, what: what);
            _frame = new Frame { Hero = hero, Site = site, Scope = scope, Outer = _frame };
            try { action(); }
            catch (Exception ex) { SubModule.Log("Bellum failed carrying out " + what + " for " + hero.StringId + ": " + ex.GetBaseException().Message); }
            finally
            {
                var frame = _frame;
                _frame = frame?.Outer;
                frame?.Scope.Dispose();
            }
            return true;
        }

        // While the server acts for a player, what Bellum tells "the player" goes to that player.
        private static bool NoticePrefix(object[] __args)
        {
            var frame = _frame;
            if (!Coop.OnServer || frame == null || __args == null || __args.Length == 0 || __args[0] == null)
                return true;
            Agency.ToPlayer(frame.Hero, "notice", __args[0].ToString());
            return false;
        }

        /// <summary>Whether a site would be put to its player now (connected, and the server can reach them).</summary>
        public static bool CanAsk(Hero hero) => hero != null && Coop.OnServer && Agency.Channel != null && AsPlayer.Available && Coop.IsConnectedPlayer(hero);

        private static Hero RulerOf(Kingdom kingdom)
        {
            var leader = kingdom?.RulingClan?.Leader;
            return leader != null && Coop.IsPlayerHero(leader) ? leader : null;
        }

        private static object Get(object instance, string member)
        {
            if (instance == null) return null;
            var type = instance.GetType();
            var property = AccessTools.Property(type, member);
            return property != null ? property.GetValue(instance, null) : AccessTools.Field(type, member)?.GetValue(instance);
        }

        // ---- server: a site runs as its player, and its question is caught ----

        private static void SitePrefix(MethodBase __originalMethod, object __instance, object[] __args)
        {
            if (!Coop.OnServer || !ByMethod.TryGetValue(__originalMethod, out var site) || AsPlayer.Current != null)
                return;
            Hero hero;
            try { hero = site.Player(__instance, __args); }
            catch { return; }
            if (!CanAsk(hero))
                return;
            var scope = AsPlayer.Enter(hero);
            if (scope == null)
                return;
            _frame = new Frame { Hero = hero, Site = site, Scope = scope, Outer = _frame };
        }

        private static Exception SiteFinalizer(MethodBase __originalMethod, Exception __exception)
        {
            var frame = _frame;
            if (frame != null && ByMethod.TryGetValue(__originalMethod, out var site) && ReferenceEquals(frame.Site, site))
            {
                _frame = frame.Outer;
                frame.Scope.Dispose();
            }
            return __exception;
        }

        private static bool InquiryPrefix(InquiryData data)
        {
            var frame = _frame;
            if (!Coop.OnServer || frame == null || data == null)
                return true;
            var pending = new Pending
            {
                Id = "p" + (++_next), Hero = frame.Hero, Site = frame.Site, Data = data,
                Expires = DateTime.UtcNow.AddSeconds(AnswerSeconds),
            };
            lock (Open) Open[pending.Id] = pending;
            _asked++;
            var yes = data.IsAffirmativeOptionShown && Enabled(data.GetIsAffirmativeOptionEnabled);
            var no = data.IsNegativeOptionShown && Enabled(data.GetIsNegativeOptionEnabled);
            var sent = Agency.ToPlayer(pending.Hero, "prompt", pending.Id, data.TitleText ?? "", data.Text ?? "",
                yes ? data.AffirmativeText ?? "" : "", no ? data.NegativeText ?? "" : "", ((int)AnswerSeconds).ToString());
            SubModule.Log("Asked " + pending.Hero.StringId + " (" + frame.Site.What + ", " + pending.Id + ")" + (sent ? "." : "; could not reach them, the fallback answers."));
            if (!sent) pending.Expires = DateTime.MinValue;
            return false;   // the server shows nothing
        }

        private static bool Enabled(Func<ValueTuple<bool, string>> check)
        {
            if (check == null) return true;
            try { return check().Item1; }
            catch { return true; }
        }

        // ---- server: the player's answer, or the fallback ----

        private static void OnAnswer(Hero sender, string[] args)
        {
            if (args.Length < 2) return;
            Pending pending;
            lock (Open)
            {
                if (!Open.TryGetValue(args[0], out pending) || !ReferenceEquals(pending.Hero, sender))
                {
                    SubModule.Log("An answer to " + args[0] + " from " + sender?.StringId + " was ignored (not theirs, or already answered).");
                    return;
                }
                Open.Remove(pending.Id);
            }
            _answered++;
            Apply(pending, args[1] == "yes", "answered");
        }

        /// <summary>Server, every few seconds: prompts nobody answered get the site's fallback.</summary>
        public static void Tick()
        {
            if (!Coop.OnServer) return;
            List<Pending> due;
            lock (Open)
            {
                due = Open.Values.Where(p => p.Expires <= DateTime.UtcNow || !Coop.IsConnectedPlayer(p.Hero)).ToList();
                foreach (var p in due) Open.Remove(p.Id);
            }
            foreach (var p in due)
            {
                _fallback++;
                Apply(p, p.Site.FallbackYes, "not answered");
            }
        }

        private static void Apply(Pending pending, bool yes, string how)
        {
            var data = pending.Data;
            if (yes && !(data.IsAffirmativeOptionShown && Enabled(data.GetIsAffirmativeOptionEnabled))) yes = false;
            var action = yes ? data.AffirmativeAction : data.NegativeAction;
            SubModule.Log(pending.Hero.StringId + " " + how + " " + (yes ? "yes" : "no") + " to " + pending.Site.What + " (" + pending.Id + ").");
            if (action == null) return;
            var ran = AsPlayer.Run(pending.Hero, () =>
            {
                try { action(); }
                catch (Exception ex) { SubModule.Log("Bellum failed applying " + pending.Id + ": " + ex.GetBaseException().Message); }
            });
            if (!ran)
            {
                try { action(); }   // the scope could not open: Bellum's own answer still runs, as for a lord
                catch (Exception ex) { SubModule.Log("Bellum failed applying " + pending.Id + ": " + ex.GetBaseException().Message); }
            }
        }

        // ---- player's PC: show the question, send the answer ----

        /// <summary>The last question shown on this PC (id), for tests.</summary>
        public static string LastShown { get; private set; } = "";

        private static void ShowOnPlayerPc(string[] args)
        {
            if (args.Length < 6) return;
            var id = args[0];
            LastShown = id;
            var yesText = args[3];
            var noText = args[4];
            var data = new InquiryData(args[1], args[2], yesText.Length > 0, noText.Length > 0, yesText, noText,
                () => Agency.ToServer("answer", id, "yes"), () => Agency.ToServer("answer", id, "no"));
            InformationManager.ShowInquiry(data, true, true);
        }

        /// <summary>Player's PC: answers a question as the buttons do (for tests).</summary>
        public static bool Answer(string id, bool yes) => Agency.ToServer("answer", id, yes ? "yes" : "no");
    }
}
