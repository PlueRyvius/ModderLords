using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
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

        /// <summary>What happens when the player does not answer in time.</summary>
        public enum Fallback
        {
            /// <summary>Bellum's own "no" runs.</summary>
            No,
            /// <summary>Bellum's own "yes" runs.</summary>
            Yes,
            /// <summary>Neither runs: as if the question was never asked.</summary>
            Nothing,
        }

        public sealed class Site
        {
            public Site(string type, string method, Func<object, object[], Hero> player, Fallback fallback, string what, bool quietWhenAbsent = false)
            {
                Type = type; Method = method; Player = player; Fallback = fallback; What = what; QuietWhenAbsent = quietWhenAbsent;
            }

            public string Type { get; }
            public string Method { get; }
            /// <summary>The player this call concerns, or null (then the site runs as Bellum's AI would).</summary>
            public Func<object, object[], Hero> Player { get; }
            public Fallback Fallback { get; }
            public string What { get; }
            /// <summary>
            /// When the player can't be asked (not connected), the site doesn't run at all, rather than running as for
            /// the server's own placeholder player.
            /// </summary>
            public bool QuietWhenAbsent { get; }
            /// <summary>
            /// For a list question (pick one of several) nobody answers: what runs instead of the fallback, with the
            /// site's own instance and arguments, outside the player's scope (Bellum's AI deciding, say).
            /// </summary>
            public Action<object, object[]> Unanswered { get; set; }
        }

        private sealed class Pending
        {
            public string Id;
            public Hero Hero;
            public Site Site;
            public InquiryData Data;
            public MultiSelectionInquiryData Choice;
            public object Instance;
            public object[] Args;
            public DateTime Expires;
        }

        public static readonly List<Site> Sites = new List<Site>
        {
            // A court faction's ultimatum to a realm a player rules: accept the demands, or refuse and face the rebellion.
            new Site("BellumCivile.FactionObject", "TriggerUltimatumDetailed", (instance, args) => RulerOf(Get(instance, "ParentKingdom") as Kingdom),
                Fallback.No, "an ultimatum to the player's realm"),
            // A ruler died and a faction a player leads is strong enough to strike: issue the ultimatum now, or not yet.
            // Calls.SuccessionClan names the player as Bellum's loop reaches their faction. An absent player: not yet.
            new Site("BellumCivile.NotificationHelper", "ShowPlayerSuccessionOpportunity", (instance, args) => Calls.SuccessionPlayer,
                Fallback.Nothing, "a succession opportunity", quietWhenAbsent: true),
        };

        private static readonly Dictionary<MethodBase, Site> ByMethod = new Dictionary<MethodBase, Site>();
        private static readonly Dictionary<string, Pending> Open = new Dictionary<string, Pending>();
        private static readonly Queue<Action> Queued = new Queue<Action>();
        [ThreadStatic] private static Frame _frame;
        private static int _next, _asked, _answered, _fallback;

        private sealed class Frame
        {
            public Hero Hero;
            public Site Site;
            public IDisposable Scope;
            public Frame Outer;
            public bool FromPrefix;
            public object Instance;
            public object[] Args;
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
            var choose = AccessTools.Method(typeof(MBInformationManager), nameof(MBInformationManager.ShowMultiSelectionInquiry));
            if (choose == null) missing.Add("MBInformationManager.ShowMultiSelectionInquiry");
            else harmony.Patch(choose, prefix: new HarmonyMethod(typeof(Prompts), nameof(ChoicePrefix)) { priority = Priority.First });

            var notifications = AccessTools.TypeByName("BellumCivile.BellumCivileNotifications");
            foreach (var personal in notifications?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Where(m => m.Name == "ShowPersonal") ?? Enumerable.Empty<MethodInfo>())
                harmony.Patch(personal, prefix: new HarmonyMethod(typeof(Prompts), nameof(NoticePrefix)) { priority = Priority.First });

            Agency.Handle("answer", OnAnswer);
            Agency.Listen("prompt", ShowOnPlayerPc);
            Agency.Listen("choose", ShowChoiceOnPlayerPc);
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
            return InFrame(hero, new Site("", "", (i, a) => hero, Fallback.No, what), action);
        }

        /// <summary>
        /// Server: runs <paramref name="action"/> as <paramref name="hero"/>, with the question Bellum asks on the way
        /// put to that player under <paramref name="site"/>'s fallback. False if the server could not act as them.
        /// </summary>
        public static bool InFrame(Hero hero, Site site, Action action)
        {
            var scope = AsPlayer.Enter(hero);
            if (scope == null)
                return false;
            var frame = new Frame { Hero = hero, Site = site, Scope = scope, Outer = _frame };
            _frame = frame;
            try { action(); }
            catch (Exception ex) { SubModule.Log("Bellum failed with " + site.What + " for " + hero.StringId + ": " + ex.GetBaseException().Message); }
            finally
            {
                _frame = frame.Outer;
                frame.Scope.Dispose();
            }
            return true;
        }

        /// <summary>Server: runs <paramref name="action"/> on the next tick, outside whatever Bellum is doing now.</summary>
        public static void Later(Action action)
        {
            if (action != null)
                lock (Queued) Queued.Enqueue(action);
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

        private static bool SitePrefix(MethodBase __originalMethod, object __instance, object[] __args)
        {
            if (!Coop.OnServer || !ByMethod.TryGetValue(__originalMethod, out var site))
                return true;
            Hero hero;
            try { hero = site.Player(__instance, __args); }
            catch { return true; }
            if (hero == null)
                return true;
            if (!CanAsk(hero))
                return !site.QuietWhenAbsent;
            // Already acting as this player (their own answer led here): no new scope, but this site's question.
            var already = ReferenceEquals(AsPlayer.Current, hero);
            var scope = already ? null : AsPlayer.Enter(hero);
            if (scope == null && !already)
                return !site.QuietWhenAbsent;
            _frame = new Frame { Hero = hero, Site = site, Scope = scope, Outer = _frame, FromPrefix = true, Instance = __instance, Args = __args };
            return true;
        }

        private static Exception SiteFinalizer(MethodBase __originalMethod, Exception __exception)
        {
            var frame = _frame;
            if (frame != null && frame.FromPrefix && ByMethod.TryGetValue(__originalMethod, out var site) && ReferenceEquals(frame.Site, site))
            {
                _frame = frame.Outer;
                frame.Scope?.Dispose();
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

        // A list question (pick one of several, MBInformationManager.ShowMultiSelectionInquiry): sent as "choose", with each
        // choice as three values (enabled, title, hint).
        private static bool ChoicePrefix(MultiSelectionInquiryData data)
        {
            var frame = _frame;
            if (!Coop.OnServer || frame == null || data == null)
                return true;
            var pending = new Pending
            {
                Id = "p" + (++_next), Hero = frame.Hero, Site = frame.Site, Choice = data, Instance = frame.Instance, Args = frame.Args,
                Expires = DateTime.UtcNow.AddSeconds(AnswerSeconds),
            };
            lock (Open) Open[pending.Id] = pending;
            _asked++;
            var args = new List<string>
            {
                pending.Id, data.TitleText ?? "", data.DescriptionText ?? "", data.MinSelectableOptionCount.ToString(), data.MaxSelectableOptionCount.ToString(),
                data.AffirmativeText ?? "", data.IsExitShown ? data.NegativeText ?? "" : "", ((int)AnswerSeconds).ToString(),
            };
            foreach (var element in data.InquiryElements ?? new List<InquiryElement>())
            {
                args.Add(element.IsEnabled ? "1" : "0");
                args.Add(element.Title ?? "");
                args.Add(element.Hint ?? "");
            }
            var sent = Agency.ToPlayer(pending.Hero, "choose", args.ToArray());
            SubModule.Log("Asked " + pending.Hero.StringId + " to choose (" + frame.Site.What + ", " + pending.Id + ")" + (sent ? "." : "; could not reach them, the fallback answers."));
            if (!sent) pending.Expires = DateTime.MinValue;
            return false;
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
            if (pending.Choice != null)
                ApplyChoice(pending, args[1], "answered");
            else
                Apply(pending, args[1] == "yes", "answered");
        }

        /// <summary>Server, every few seconds: prompts nobody answered get the site's fallback.</summary>
        public static void Tick()
        {
            if (!Coop.OnServer) return;
            List<Action> queued;
            lock (Queued) { queued = Queued.ToList(); Queued.Clear(); }
            foreach (var action in queued)
            {
                try { action(); }
                catch (Exception ex) { SubModule.Log("Prompts: a queued question failed: " + ex.GetBaseException().Message); }
            }
            List<Pending> due;
            lock (Open)
            {
                due = Open.Values.Where(p => p.Expires <= DateTime.UtcNow || !Coop.IsConnectedPlayer(p.Hero)).ToList();
                foreach (var p in due) Open.Remove(p.Id);
            }
            foreach (var p in due)
            {
                _fallback++;
                if (p.Choice != null)
                    ChoiceUnanswered(p);
                else if (p.Site.Fallback == Fallback.Nothing)
                    SubModule.Log(p.Hero.StringId + " did not answer " + p.Site.What + " (" + p.Id + "); nothing was done.");
                else
                    Apply(p, p.Site.Fallback == Fallback.Yes, "not answered");
            }
        }

        private static void Apply(Pending pending, bool yes, string how)
        {
            var data = pending.Data;
            if (yes && !(data.IsAffirmativeOptionShown && Enabled(data.GetIsAffirmativeOptionEnabled))) yes = false;
            var action = yes ? data.AffirmativeAction : data.NegativeAction;
            SubModule.Log(pending.Hero.StringId + " " + how + " " + (yes ? "yes" : "no") + " to " + pending.Site.What + " (" + pending.Id + ").");
            if (action == null) return;
            // As that player, and what Bellum then asks or tells "the player" goes to them too.
            var ran = InFrame(pending.Hero, pending.Site, action);
            if (!ran)
            {
                try { action(); }   // the scope could not open: Bellum's own answer still runs, as for a lord
                catch (Exception ex) { SubModule.Log("Bellum failed applying " + pending.Id + ": " + ex.GetBaseException().Message); }
            }
        }

        private static void ChoiceUnanswered(Pending pending)
        {
            if (pending.Site.Unanswered != null)
            {
                SubModule.Log(pending.Hero.StringId + " did not choose for " + pending.Site.What + " (" + pending.Id + "); Bellum decides as for a lord.");
                try { pending.Site.Unanswered(pending.Instance, pending.Args ?? new object[0]); }
                catch (Exception ex) { SubModule.Log("Bellum failed deciding " + pending.Id + ": " + ex.GetBaseException().Message); }
                return;
            }
            switch (pending.Site.Fallback)
            {
                case Fallback.Nothing:
                    SubModule.Log(pending.Hero.StringId + " did not answer " + pending.Site.What + " (" + pending.Id + "); nothing was done.");
                    return;
                case Fallback.Yes:
                    var first = (pending.Choice.InquiryElements ?? new List<InquiryElement>()).FindIndex(e => e.IsEnabled);
                    ApplyChoice(pending, first < 0 ? "no" : "pick:" + first, "not answered");
                    return;
                default:
                    ApplyChoice(pending, "no", "not answered");
                    return;
            }
        }

        // A list answer: "pick:0,2" (the choices, by place) or "no" (the list's own cancel button).
        private static void ApplyChoice(Pending pending, string answer, string how)
        {
            var data = pending.Choice;
            var elements = data.InquiryElements ?? new List<InquiryElement>();
            var picked = new List<InquiryElement>();
            if (answer != null && answer.StartsWith("pick:"))
                foreach (var part in answer.Substring(5).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    if (int.TryParse(part, out var i) && i >= 0 && i < elements.Count && elements[i].IsEnabled && !picked.Contains(elements[i]))
                        picked.Add(elements[i]);
            var yes = picked.Count > 0 && picked.Count >= data.MinSelectableOptionCount && picked.Count <= Math.Max(1, data.MaxSelectableOptionCount);
            var action = yes ? data.AffirmativeAction : data.NegativeAction;
            SubModule.Log(pending.Hero.StringId + " " + how + " " + (yes ? string.Join(", ", picked.Select(e => e.Title)) : "no") + " to " + pending.Site.What + " (" + pending.Id + ").");
            if (action == null) return;
            var chosen = yes ? picked : new List<InquiryElement>();
            if (!InFrame(pending.Hero, pending.Site, () => action(chosen)))
            {
                try { action(chosen); }
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

        private static void ShowChoiceOnPlayerPc(string[] args)
        {
            if (args.Length < 8) return;
            var id = args[0];
            LastShown = id;
            int.TryParse(args[3], out var min);
            int.TryParse(args[4], out var max);
            var elements = new List<InquiryElement>();
            for (int i = 8, n = 0; i + 2 < args.Length; i += 3, n++)
                elements.Add(new InquiryElement(n, args[i + 1], null, args[i] == "1", args[i + 2]));
            var noText = args[6];
            var data = new MultiSelectionInquiryData(args[1], args[2], elements, noText.Length > 0, Math.Max(1, min), Math.Max(1, max), args[5], noText,
                picked => Agency.ToServer("answer", id, "pick:" + string.Join(",", (picked ?? new List<InquiryElement>()).Select(e => e.Identifier))),
                _ => Agency.ToServer("answer", id, "no"));
            MBInformationManager.ShowMultiSelectionInquiry(data, true);
        }

        /// <summary>Player's PC: answers a question as the buttons do (for tests).</summary>
        public static bool Answer(string id, bool yes) => Agency.ToServer("answer", id, yes ? "yes" : "no");

        /// <summary>Player's PC: answers "yes", "no" or, for a list, "pick:N" (for tests).</summary>
        public static bool Reply(string id, string answer) => Agency.ToServer("answer", id, answer);
    }
}
