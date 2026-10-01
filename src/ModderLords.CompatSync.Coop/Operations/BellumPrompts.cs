using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameInterface;
using GameInterface.Services.Players;
using HarmonyLib;
using ModderLords.CompatSync;
using ModderLords.CompatSync.Coop.Fourberie;
using ModderLords.CompatSync.Coop.Taom;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace ModderLords.CompatSync.Coop.Operations;

/// <summary>
/// One place Bellum decides something for "the player": the Bellum method that raises the prompt, who it is for, and
/// what Bellum's own AI does instead when that player does not answer.
/// </summary>
internal sealed class BellumPromptSite
{
    /// <summary>"Type::Method", pinned in the bellum-civile.commands contract.</summary>
    public string Method = "";

    /// <summary>The players this call is for, first one first; empty leaves the call to Bellum's AI path.</summary>
    public Func<object?, object?[], IReadOnlyList<Hero>> Players = (_, _) => Array.Empty<Hero>();

    /// <summary>
    /// What happens when the player does not answer in time or is offline: Bellum decides as it would for a lord.
    /// Runs on the game thread outside any player scope; false falls back to <see cref="BellumPrompts.DefaultAnswer"/>.
    /// </summary>
    public Func<BellumPrompts.Pending, bool>? Ai;
}

/// <summary>
/// Bellum validation mode, both sides: Bellum's decisions for "the player" reach the real player they are about.
/// <para>
/// On a dedicated server Bellum's simulation runs with the server's placeholder as "the player", so it never asked a
/// real player anything: their clan was treated like a lord's. A <see cref="BellumPromptSite"/> runs its Bellum method
/// as the player it concerns (ServerRelay.PlayerScope), so Bellum takes its player branch with that player's names; the
/// inquiry it then opens goes to that player's game instead of the server's headless guard. The answer comes back on
/// the shared action channel and Bellum's own callback runs on the server as that player. A player who does not answer
/// within ten minutes, or is offline, gets Bellum's AI decision (the site's <see cref="BellumPromptSite.Ai"/>): the world
/// keeps moving around an absent player instead of waiting for them.
/// </para>
/// <para>Wire form is the Fourberie layer's FbPromptWire, under its own feature.</para>
/// </summary>
internal static class BellumPrompts
{
    internal const string Feature = "bellum-ui";
    private static readonly TimeSpan AnswerWithin = TimeSpan.FromMinutes(10);
    private static readonly Harmony Harmony = new Harmony("ModderLords.Compat.BellumCivile.Prompts");
    /// <summary>Above the headless guard, which answers every inquiry itself; below the Fourberie layer's own capture.</summary>
    private const int CapturePriority = Priority.First + 100;

    internal sealed class Pending
    {
        public int Id;
        public Hero Hero = null!;
        public BellumPromptSite Site = null!;
        public object? Instance;
        public object?[] Args = Array.Empty<object?>();
        public InquiryData? Inquiry;
        public MultiSelectionInquiryData? Multi;
        public DateTime Expires;
        public string Title => Inquiry?.TitleText ?? Multi?.TitleText ?? "";
    }

    private sealed class Frame
    {
        public BellumPromptSite Site = null!;
        public Hero Hero = null!;
        public object? Instance;
        public object?[] Args = Array.Empty<object?>();
        public IReadOnlyList<Hero> Others = Array.Empty<Hero>();
        public IDisposable? Scope;
        public Frame? Previous;
    }

    private static readonly Dictionary<MethodBase, BellumPromptSite> Sites = new Dictionary<MethodBase, BellumPromptSite>();
    private static readonly Dictionary<int, Pending> Waiting = new Dictionary<int, Pending>();
    [ThreadStatic] private static Frame? _frame;
    [ThreadStatic] private static Hero? _replayAs;
    private static bool _server, _installed;
    private static int _nextId;
    private static long _sent, _answered, _decided;
    private static DateTime _nextExpiryCheck = DateTime.MinValue;

    // ---- install ---------------------------------------------------------------------------------------------------

    /// <summary>Server: wraps every site and captures inquiries raised inside one. Client: shows what the server sends.</summary>
    internal static string Install(bool server, IEnumerable<BellumPromptSite> sites, IEnumerable<string> anyPlayerChecks)
    {
        if (_installed) return "already installed";
        _installed = true;
        _server = server;
        if (!server)
        {
            TaomActions.RegisterClientApply(Feature, ClientApply);
            return "this player's game shows the Bellum prompts the server raises for them";
        }

        var siteList = sites.ToList();
        foreach (var site in siteList)
        {
            var method = Resolve(site.Method) ?? throw new MissingMethodException("Bellum prompt site not found: " + site.Method);
            Sites[method] = site;
            Harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(BellumPrompts), nameof(SitePrefix)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(BellumPrompts), nameof(SiteFinalizer)));
        }
        Harmony.Patch(AccessTools.Method(typeof(InformationManager), nameof(InformationManager.ShowInquiry)),
            prefix: new HarmonyMethod(typeof(BellumPrompts), nameof(InquiryPrefix)) { priority = CapturePriority });
        Harmony.Patch(AccessTools.Method(typeof(MBInformationManager), nameof(MBInformationManager.ShowMultiSelectionInquiry)),
            prefix: new HarmonyMethod(typeof(BellumPrompts), nameof(MultiPrefix)) { priority = CapturePriority });
        TaomActions.Register(Feature, Answer);

        // "Is this the player's?" where Bellum means "players choose for themselves": every player, not the placeholder.
        var t = typeof(ServerPlayerChecks);
        PlayerComparisonRewriter.SetHelpers(t.GetMethod(nameof(ServerPlayerChecks.IsAnyPlayerHero))!, t.GetMethod(nameof(ServerPlayerChecks.IsAnyPlayerClan))!,
            t.GetMethod(nameof(ServerPlayerChecks.IsAnyPlayerParty))!, t.GetMethod(nameof(ServerPlayerChecks.IsAnyPlayerPartyBase))!);
        var checks = anyPlayerChecks.ToList();
        var (methods, comparisons, missing) = PlayerComparisonRewriter.Apply(Harmony, checks, m => Log.Warn("Bellum prompts: " + m));
        if (missing > 0) throw new MissingMethodException("Bellum prompts: " + missing + " player check method(s) not found");

        var notices = NoticeComponent.EnsureInstalled();
        return $"{siteList.Count} Bellum prompt site(s) go to the player they concern; {methods} method(s) ask about any player ({comparisons} comparison(s)); {notices}";
    }

    // ---- server: running a site as its player ----------------------------------------------------------------------

    public static void SitePrefix(MethodBase __originalMethod, object? __instance, object?[] __args, out object? __state)
    {
        __state = null;
        if (!_server || !Sites.TryGetValue(__originalMethod, out var site)) return;
        var hero = _replayAs;
        _replayAs = null;
        IReadOnlyList<Hero> others = Array.Empty<Hero>();
        if (hero == null)
        {
            IReadOnlyList<Hero> players;
            try { players = site.Players(__instance, __args); }
            catch (Exception ex) { Log.Warn($"Bellum prompts: could not tell who {site.Method} is for: {ex.GetBaseException().Message}"); return; }
            if (players.Count == 0) return;
            hero = players[0];
            others = players.Skip(1).ToList();
        }
        var frame = new Frame { Site = site, Hero = hero, Instance = __instance, Args = __args, Others = others, Previous = _frame };
        frame.Scope = new ServerRelay.PlayerScope(hero, hero.PartyBelongedTo);
        _frame = frame;
        __state = frame;
    }

    public static Exception? SiteFinalizer(MethodBase __originalMethod, object? __instance, object?[] __args, object? __state, Exception? __exception)
    {
        if (__state is not Frame frame) return __exception;
        _frame = frame.Previous;
        frame.Scope?.Dispose();
        // A site that asks several players (a call to arms) asks each one in turn, as that player.
        foreach (var other in frame.Others)
        {
            _replayAs = other;
            try { __originalMethod.Invoke(__instance, __args); }
            catch (Exception ex) { Log.Warn($"Bellum prompts: {frame.Site.Method} for {other.Name} failed: {ex.GetBaseException().Message}"); }
            finally { _replayAs = null; }
        }
        return __exception;
    }

    // ---- server: capture ------------------------------------------------------------------------------------------

    public static bool InquiryPrefix(InquiryData __0)
    {
        if (_frame is not { } f || __0 == null) return true;
        Hold(f, new Pending { Inquiry = __0 });
        return false;
    }

    public static bool MultiPrefix(MultiSelectionInquiryData __0)
    {
        if (_frame is not { } f || __0 == null) return true;
        Hold(f, new Pending { Multi = __0 });
        return false;
    }

    private static void Hold(Frame f, Pending p)
    {
        p.Id = ++_nextId;
        p.Hero = f.Hero;
        p.Site = f.Site;
        p.Instance = f.Instance;
        p.Args = (object?[])f.Args.Clone();
        var connected = IsConnected(f.Hero);
        // An offline player times out at once: there is nobody to wait for, and Bellum's AI decides for them.
        p.Expires = connected ? DateTime.UtcNow + AnswerWithin : DateTime.UtcNow;
        Waiting[p.Id] = p;
        if (connected && TaomActions.Push != null)
        {
            TaomActions.Push(f.Hero, Feature, Pack(p));
            _sent++;
            Log.Info($"Bellum prompts: '{p.Title}' sent to {f.Hero.Name} ({f.Site.Method})");
        }
        else Log.Info($"Bellum prompts: '{p.Title}' is for {f.Hero.Name}, who is offline; Bellum's AI decides");
    }

    private static List<string> Pack(Pending p)
    {
        if (p.Inquiry is { } q)
        {
            var (affirmativeEnabled, affirmativeHint) = Evaluate(q.GetIsAffirmativeOptionEnabled);
            var (negativeEnabled, negativeHint) = Evaluate(q.GetIsNegativeOptionEnabled);
            return FbPromptWire.Pack(new FbInquiry
            {
                Id = p.Id, Title = q.TitleText ?? "", Text = q.Text ?? "", AffirmativeText = q.AffirmativeText ?? "", NegativeText = q.NegativeText ?? "",
                AffirmativeShown = q.IsAffirmativeOptionShown, NegativeShown = q.IsNegativeOptionShown,
                AffirmativeEnabled = affirmativeEnabled, AffirmativeHint = affirmativeHint, NegativeEnabled = negativeEnabled, NegativeHint = negativeHint,
            });
        }
        var m = p.Multi!;
        var wire = new FbMultiInquiry
        {
            Id = p.Id, Title = m.TitleText ?? "", Description = m.DescriptionText ?? "", AffirmativeText = m.AffirmativeText ?? "",
            NegativeText = m.NegativeText ?? "", ExitShown = m.IsExitShown, Min = m.MinSelectableOptionCount, Max = m.MaxSelectableOptionCount,
        };
        foreach (var e in m.InquiryElements ?? new List<InquiryElement>()) wire.Elements.Add((e.Title ?? "", e.Hint ?? "", e.IsEnabled));
        return FbPromptWire.Pack(wire);
    }

    private static (bool, string) Evaluate(Func<(bool, string)>? check)
    {
        if (check == null) return (true, "");
        try { var (ok, hint) = check(); return (ok, hint ?? ""); }
        catch { return (true, ""); }
    }

    // ---- server: answers and timeouts -----------------------------------------------------------------------------

    /// <summary>Server, game thread, inside TaomActions.Run (as the answering player): a player's answer to their prompt.</summary>
    private static TaomActionOutcome Answer(Hero hero, TaleWorlds.CampaignSystem.Party.MobileParty? party, string op, IList<string> args)
    {
        if (op != "answer" || args.Count == 0 || !int.TryParse(args[0], out var id) || !Waiting.TryGetValue(id, out var p) || !ReferenceEquals(p.Hero, hero))
            return TaomActionOutcome.Fail("");
        var multi = p.Multi;
        if (!FbPromptWire.TryReadAnswer(args, out _, out var yes, out var picked,
                multi?.InquiryElements?.Count ?? 0, multi?.MinSelectableOptionCount ?? 0, multi?.MaxSelectableOptionCount ?? 0))
            return TaomActionOutcome.Fail("");
        Waiting.Remove(id);
        _answered++;
        RunAsPlayer(p, () =>
        {
            if (p.Inquiry is { } q)
            {
                var (affirmativeEnabled, _) = Evaluate(q.GetIsAffirmativeOptionEnabled);
                if (yes && q.IsAffirmativeOptionShown && affirmativeEnabled) q.AffirmativeAction?.Invoke();
                else if (!yes) q.NegativeAction?.Invoke();
            }
            else if (multi != null)
            {
                var elements = multi.InquiryElements ?? new List<InquiryElement>();
                if (picked != null) multi.AffirmativeAction?.Invoke(picked.Select(i => elements[i]).Where(e => e.IsEnabled).ToList());
                else multi.NegativeAction?.Invoke(new List<InquiryElement>());
            }
        });
        Log.Info($"Bellum prompts: {hero.Name} answered '{p.Title}'");
        return new TaomActionOutcome(true, "");
    }

    /// <summary>Server tick: prompts whose player did not answer in time, or went offline, are decided by Bellum's AI.</summary>
    internal static void ServerTick()
    {
        if (!_server || Waiting.Count == 0 || DateTime.UtcNow < _nextExpiryCheck) return;
        _nextExpiryCheck = DateTime.UtcNow.AddSeconds(2);
        var now = DateTime.UtcNow;
        foreach (var p in Waiting.Values.Where(p => p.Expires <= now || !IsConnected(p.Hero)).ToList())
        {
            Waiting.Remove(p.Id);
            _decided++;
            var byAi = false;
            try { byAi = p.Site.Ai?.Invoke(p) == true; }
            catch (Exception ex) { Log.Warn($"Bellum prompts: AI decision for '{p.Title}' failed: {ex.GetBaseException().Message}"); }
            if (!byAi) RunAsPlayer(p, () => DefaultAnswer(p));
            Log.Info($"Bellum prompts: no answer from {p.Hero.Name} to '{p.Title}'; {(byAi ? "Bellum's AI decided" : "the default was taken")}");
            if (IsConnected(p.Hero))
                RunAsPlayer(p, () => InformationManager.DisplayMessage(new InformationMessage(
                    $"No answer in time to \"{p.Title}\": it was decided for you.", Colors.Yellow)));
        }
    }

    /// <summary>Used only when a site has no AI decision or it failed: the first enabled choice that is not a refusal.</summary>
    internal static void DefaultAnswer(Pending p)
    {
        if (p.Inquiry is { } q)
        {
            if (q.IsAffirmativeOptionShown && Evaluate(q.GetIsAffirmativeOptionEnabled).Item1) q.AffirmativeAction?.Invoke();
            else q.NegativeAction?.Invoke();
        }
        else if (p.Multi is { } m)
        {
            var chosen = (m.InquiryElements ?? new List<InquiryElement>()).Where(e => e.IsEnabled).Take(Math.Max(1, m.MinSelectableOptionCount)).ToList();
            if (chosen.Count > 0) m.AffirmativeAction?.Invoke(chosen);
            else m.NegativeAction?.Invoke(new List<InquiryElement>());
        }
    }

    /// <summary>Runs Bellum code as the prompt's player, so its "the player" is them and their follow-up prompts reach them.</summary>
    internal static void RunAsPlayer(Pending p, Action action)
    {
        var running = TaomActions.Running;
        TaomActions.Running = false;   // let the notice forwarder send what the callback shows
        var frame = new Frame { Site = p.Site, Hero = p.Hero, Instance = p.Instance, Args = p.Args, Previous = _frame };
        frame.Scope = new ServerRelay.PlayerScope(p.Hero, p.Hero.PartyBelongedTo);
        _frame = frame;
        try { action(); }
        catch (Exception ex) { Log.Warn($"Bellum prompts: callback for '{p.Title}' failed for {p.Hero.Name}: {ex.GetBaseException().Message}"); }
        finally
        {
            _frame = frame.Previous;
            frame.Scope.Dispose();
            TaomActions.Running = running;
        }
    }

    internal static string Summary() => $"Bellum prompts sent {_sent}, answered {_answered}, decided for absent players {_decided}, waiting {Waiting.Count}";

    // ---- who is who ------------------------------------------------------------------------------------------------

    /// <summary>The living hero of a Coop player whose clan this is (online or not), or null for a lord's clan.</summary>
    internal static Hero? PlayerOf(Clan? clan)
    {
        if (clan == null || !ContainerProvider.TryResolve<IPlayerManager>(out var players)) return null;
        foreach (var player in players.Players)
            if (PlayerHeroes.HeroFor(player.HeroId) is { IsAlive: true } hero && ReferenceEquals(hero.Clan, clan)) return hero;
        return null;
    }

    private static bool IsConnected(Hero hero) =>
        ContainerProvider.TryResolve<IPlayerManager>(out var players) && PlayerHeroes.PlayerFor(players, hero) is { } player
        && players.TryGetPeer(player.ControllerId, out var peer) && peer != null;

    private static MethodBase? Resolve(string identity)
    {
        var split = identity.Split(new[] { "::" }, 2, StringSplitOptions.None);
        var type = split.Length == 2 ? AccessTools.TypeByName(split[0]) : null;
        var matches = type == null ? new List<MethodInfo>() : AccessTools.GetDeclaredMethods(type).Where(m => m.Name == split[1]).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    // ---- client ----------------------------------------------------------------------------------------------------

    private static void ClientApply(IList<string> data)
    {
        switch (FbPromptWire.Unpack(data))
        {
            case FbInquiry q:
                InformationManager.ShowInquiry(new InquiryData(q.Title, q.Text, q.AffirmativeShown, q.NegativeShown, q.AffirmativeText, q.NegativeText,
                    () => Reply(FbPromptWire.Answer(q.Id, true)), () => Reply(FbPromptWire.Answer(q.Id, false)),
                    isAffirmativeOptionEnabled: () => (q.AffirmativeEnabled, q.AffirmativeHint),
                    isNegativeOptionEnabled: () => (q.NegativeEnabled, q.NegativeHint)));
                break;
            case FbMultiInquiry m:
                var elements = m.Elements.Select((e, i) => new InquiryElement(i, e.Title, null, e.Enabled, e.Hint)).ToList();
                MBInformationManager.ShowMultiSelectionInquiry(new MultiSelectionInquiryData(m.Title, m.Description, elements, m.ExitShown, m.Min, m.Max,
                    m.AffirmativeText, m.NegativeText,
                    chosen => Reply(FbPromptWire.AnswerPicked(m.Id, chosen.Select(c => (int)c.Identifier))),
                    _ => Reply(FbPromptWire.Answer(m.Id, false))));
                break;
            default:
                Log.Warn("Bellum prompts: a server prompt could not be read");
                break;
        }
    }

    private static void Reply(List<string> answer) => TaomActions.Send?.Invoke(Feature, "answer", answer);
}
