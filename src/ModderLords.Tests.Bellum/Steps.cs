using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ModderLords.Testing;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Election;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace ModderLords.Tests.Bellum;

/// <summary>
/// Steps for Bellum Civile and its Coop compat module (coop-compat/BellumCivileCoopCompat). Both are read by reflection,
/// so this DLL needs neither to build. Kingdoms are named by StringId, which is the same on the server and every
/// player's game.
/// </summary>
public static class Steps
{
    internal static readonly TimeSpan Sync = TimeSpan.FromSeconds(30);
    internal const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    public static void Register()
    {
        InGameTests.Register("bellum.compat", StepSide.Any, Compat);
        InGameTests.Register("bellum.rebellion", StepSide.Server, Rebellion);
        InGameTests.Register("bellum.war", StepSide.Any, War);
        InGameTests.Register("bellum.endInternalWar", StepSide.Server, EndInternalWar);
        InGameTests.Register("bellum.makeRuler", StepSide.Server, MakeRuler);
        InGameTests.Register("bellum.declareWar", StepSide.Server, DeclareWar);
        InGameTests.Register("bellum.addInfluence", StepSide.Server, AddInfluence);
        InGameTests.Register("bellum.proposePeace", StepSide.Client, ProposePeace);
        InGameTests.Register("bellum.voteForPeace", StepSide.Client, VoteForPeace);
        CoopSteps.Register();
        PlayersSteps.Register();
        AgencySteps.Register();
        CallsSteps.Register();
        FeudSteps.Register();
        ActionsSteps.Register();
        TalkSteps.Register();
        ArmySteps.Register();
        HostageSteps.Register();
        SubinfeudationSteps.Register();
    }

    // ---- the compat module ---------------------------------------------------------------------------------------

    // What the module logs when a Bellum method it needs is gone or has changed.
    private static readonly string[] Trouble =
        { "not found", "changed shape", "not installed", "no longer has", "Could not", "failed", "Not found in this Bellum version", "some other way" };

    /// <summary>
    /// The compat module's own log this run: fails on any line that says a part is missing or broken, and on any
    /// <c>expect</c> text (separated by |) that no line contains (waited for up to <c>waitSeconds</c> when given). Reports its summary.
    /// </summary>
    private static IEnumerable<StepWait?> Compat(StepContext ctx)
    {
        var module = FindType("BellumCivileCoopCompat.SubModule") ?? throw new StepFailedException("the Bellum Civile Coop Compat module is not loaded");
        var logged = module.GetField("Logged", Any)?.GetValue(null) as List<string>
            ?? throw new StepFailedException("this compat module keeps no log for tests (older than 1.1.0)");
        var wanted = ctx.Arg("expect", "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
        if (ctx.HasArg("waitSeconds") && wanted.Length > 0)
        {
            bool Seen() { lock (logged) return wanted.All(w => logged.Any(l => l.Contains(w))); }
            yield return ctx.Until(Seen, "the compat module to log " + string.Join(", ", wanted), TimeSpan.FromSeconds(ctx.ArgInt("waitSeconds", 60)));
        }
        List<string> lines;
        lock (logged) lines = logged.ToList();
        ctx.Dump("bellum-compat-" + ctx.Side.Replace(':', '-'), string.Join(Environment.NewLine, lines));
        foreach (var line in lines) ctx.Log(line);

        var trouble = lines.Where(l => Trouble.Any(t => l.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        ctx.Assert(trouble.Count == 0, "the compat module reports: " + string.Join(" / ", trouble));
        foreach (var want in ctx.Arg("expect", "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            ctx.Assert(lines.Any(l => l.Contains(want)), $"the compat module never logged '{want}'");

        var summary = FindType("BellumCivileCoopCompat.Hooks")?.GetMethod("Summary", Any)?.Invoke(null, null) as string;
        ctx.Report("value", lines.Count);
        ctx.Report("summary", summary ?? "");
        yield break;
    }

    // ---- civil war -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Server: starts a rebellion the way Bellum's own cheat does: a court faction of a few vassal clans in a lords'
    /// kingdom (CheatCommands.CreateFactionFromArgs), then its ultimatum. A ruler who gives in starts no war, so it tries
    /// kingdoms in turn until one rebels. Reports the rebel kingdom (value) and the kingdom it rose against (parent).
    /// </summary>
    private static IEnumerable<StepWait?> Rebellion(StepContext ctx)
    {
        var cheats = FindType("BellumCivile.CheatCommands") ?? throw new StepFailedException("Bellum Civile is not loaded");
        var create = cheats.GetMethod("CreateFactionFromArgs", Any) ?? throw new StepFailedException("Bellum has no CreateFactionFromArgs");
        var type = ctx.Arg("type", "Independence");
        var candidates = Kingdom.All
            .Where(k => k != null && !k.IsEliminated && k.RulingClan != null && !k.StringId.Contains("_rebels") && !HasPlayers(k))
            .OrderByDescending(k => k.Clans.Count(c => c != k.RulingClan && !c.IsUnderMercenaryService && !c.IsMinorFaction))
            .Take(ctx.ArgInt("tries", 6)).ToList();
        ctx.Assert(candidates.Count > 0, "no lords' kingdom to rebel in");

        // Bellum bars rebellions in every realm for a year after a new campaign starts ("the realm is pacified"), and the
        // suite runs on a new world: lift that first-year grace for the realm being tried, on the server only.
        var managerType = FindType("BellumCivile.Behaviors.FactionManagerBehavior");
        var manager = managerType == null ? null
            : typeof(Campaign).GetMethod(nameof(Campaign.GetCampaignBehavior))?.MakeGenericMethod(managerType).Invoke(Campaign.Current, null);
        var grace = managerType?.GetField("_initialCivilWarGrace", Any)?.GetValue(manager) as IDictionary;
        if (grace == null) ctx.Log("Bellum's first-year rebellion grace was not found; left as it is");

        foreach (var kingdom in candidates)
        {
            if (grace != null && grace.Contains(kingdom))
            {
                grace.Remove(kingdom);
                ctx.Log($"{kingdom.StringId}: lifted Bellum's first-year rebellion grace");
            }
            var before = new HashSet<Kingdom>(Kingdom.All);
            var args = new object?[] { new List<string> { kingdom.StringId, type }, null };
            var faction = create.Invoke(null, args);
            if (faction == null)
            {
                ctx.Log($"{kingdom.StringId}: no faction ({args[1]})");
                continue;
            }
            var ultimatum = faction.GetType().GetMethod("TriggerUltimatum", Type.EmptyTypes);
            ctx.Log($"{kingdom.StringId}: faction '{faction.GetType().GetProperty("Name")?.GetValue(faction)}', ultimatum {ultimatum?.Invoke(faction, null)}");
            Kingdom? Rebels() => Kingdom.All.FirstOrDefault(r => r != null && !before.Contains(r) && !r.IsEliminated && r.IsAtWarWith(kingdom));
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (Rebels() == null && DateTime.UtcNow < until) yield return null;
            if (Rebels() is { } rebels)
            {
                ctx.Log($"{rebels.StringId} ({rebels.Name}) rose against {kingdom.StringId}");
                ctx.Report("value", rebels.StringId);
                ctx.Report("parent", kingdom.StringId);
                yield break;
            }
            ctx.Log($"{kingdom.StringId}: the ruler gave in; no war");
        }
        throw new StepFailedException($"no kingdom of {candidates.Count} rebelled");
    }

    /// <summary>
    /// Whether kingdoms <c>a</c> and <c>b</c> are at war on this side: "war", or "ended" (peace, or one of them is gone).
    /// With <c>expect</c>, waits for it (timeoutSeconds, default 60).
    /// </summary>
    private static IEnumerable<StepWait?> War(StepContext ctx)
    {
        string a = ctx.Arg("a"), b = ctx.Arg("b");
        string Read()
        {
            var first = KingdomById(a);
            var second = KingdomById(b);
            if (first == null || second == null || first.IsEliminated || second.IsEliminated) return "ended";
            return first.IsAtWarWith(second) ? "war" : "ended";
        }
        if (ctx.HasArg("expect"))
            yield return ctx.Settled(Read, $"{a} and {b}", ctx.Arg("expect"), timeout: TimeSpan.FromSeconds(ctx.ArgInt("timeoutSeconds", 60)));
        var k1 = KingdomById(a);
        var k2 = KingdomById(b);
        ctx.Report("value", Read());
        ctx.Report("detail", $"{a}: {(k1 == null ? "missing" : k1.IsEliminated ? "eliminated" : "standing")}, {b}: {(k2 == null ? "missing" : k2.IsEliminated ? "eliminated" : "standing")}");
    }

    /// <summary>
    /// Server: puts a white peace between kingdoms <c>a</c> and <c>b</c> (a civil war, rivalry or feud) in Bellum's
    /// internal settlement queue, as its council vote or an exhausted war does, and lets time run until the war is over.
    /// Bellum applies that queue from its campaign tick, which waited for a map screen: a dedicated server never had one.
    /// </summary>
    private static IEnumerable<StepWait?> EndInternalWar(StepContext ctx)
    {
        var first = KingdomById(ctx.Arg("a")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("a")}'");
        var second = KingdomById(ctx.Arg("b")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("b")}'");
        ctx.Assert(first.IsAtWarWith(second), $"{first.StringId} and {second.StringId} are not at war");
        var behavior = FindType("BellumCivile.Behaviors.InternalPeaceSettlementBehavior") ?? throw new StepFailedException("this Bellum has no internal settlement queue");
        var current = behavior.GetProperty("Current", Any)?.GetValue(null) ?? throw new StepFailedException("Bellum's internal settlement behaviour is not running");
        var queue = behavior.GetMethod("QueueWhitePeace", Any) ?? throw new StepFailedException("Bellum has no QueueWhitePeace");
        var queued = queue.Invoke(current, new object[] { first, second, false });
        ctx.Assert(queued is true, $"Bellum would not queue a white peace between {first.StringId} and {second.StringId} (not an internal war?)");
        ctx.Log("queued a white peace");

        Kingdom? Find(string id) => KingdomById(id);
        bool Over() => Find(first.StringId) is not { IsEliminated: false } k1 || Find(second.StringId) is not { IsEliminated: false } k2 || !k1.IsAtWarWith(k2);
        var start = CampaignTime.Now;
        SetTime(ctx, play: true);
        try
        {
            yield return ctx.Until(Over, $"the war between {first.StringId} and {second.StringId} to end", TimeSpan.FromSeconds(ctx.ArgInt("timeoutSeconds", 90)));
        }
        finally
        {
            SetTime(ctx, play: false);
        }
        ctx.Report("value", $"ended after {(CampaignTime.Now - start).ToHours:0.#} game hours");
    }

    // ---- a player's realm ------------------------------------------------------------------------------------------

    /// <summary>Server: makes a hero's clan the founder and ruler of a new kingdom (KingdomManager.CreateKingdom). Reports its id.</summary>
    private static IEnumerable<StepWait?> MakeRuler(StepContext ctx)
    {
        var clan = HeroNames.Single(ctx.Arg("heroName")).Clan ?? throw new StepFailedException("the hero has no clan");
        if (clan.Kingdom == null || clan.Kingdom.RulingClan != clan)
        {
            var manager = Campaign.Current.KingdomManager;
            var create = manager.GetType().GetMethods().FirstOrDefault(m => m.Name == "CreateKingdom")
                ?? throw new StepFailedException("this game has no KingdomManager.CreateKingdom");
            var name = new TextObject(ctx.Arg("name", "Harness Realm"));
            var args = create.GetParameters().Select(p => p.ParameterType == typeof(TextObject) ? name
                : p.ParameterType == typeof(CultureObject) ? clan.Culture
                : p.ParameterType == typeof(Clan) ? clan
                : p.HasDefaultValue ? p.DefaultValue : null).ToArray();
            create.Invoke(manager, args);
        }
        yield return ctx.Until(() => clan.Kingdom != null && clan.Kingdom.RulingClan == clan, $"{clan.Name} to rule a kingdom", Sync);
        ctx.Report("value", clan.Kingdom!.StringId);
    }

    /// <summary>
    /// Server: kingdom <c>kingdom</c> declares war on <c>target</c>, or on the largest lords' kingdom it is at peace
    /// with. Reports the enemy's id.
    /// </summary>
    private static IEnumerable<StepWait?> DeclareWar(StepContext ctx)
    {
        var kingdom = KingdomById(ctx.Arg("kingdom")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("kingdom")}'");
        var target = ctx.HasArg("target")
            ? KingdomById(ctx.Arg("target")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("target")}'")
            : Kingdom.All.Where(k => k != null && k != kingdom && !k.IsEliminated && k.RulingClan != null && !k.StringId.Contains("_rebels")
                    && !HasPlayers(k) && !kingdom.IsAtWarWith(k))
                .OrderByDescending(k => k.Clans.Count).FirstOrDefault() ?? throw new StepFailedException("no kingdom to declare war on");
        DeclareWarAction.ApplyByDefault(kingdom, target);
        yield return ctx.Until(() => kingdom.IsAtWarWith(target), $"{kingdom.StringId} to be at war with {target.StringId}", Sync);
        ctx.Report("value", target.StringId);
    }

    /// <summary>Server: gives a hero's clan <c>amount</c> influence (default 500).</summary>
    private static IEnumerable<StepWait?> AddInfluence(StepContext ctx)
    {
        var clan = HeroNames.Single(ctx.Arg("heroName")).Clan ?? throw new StepFailedException("the hero has no clan");
        var before = clan.Influence;
        ChangeClanInfluenceAction.Apply(clan, ctx.ArgInt("amount", 500));
        yield return ctx.Until(() => clan.Influence > before, $"{clan.Name}'s influence to rise", Sync);
        ctx.Report("value", clan.Influence);
    }

    /// <summary>
    /// Player: proposes peace with kingdom <c>enemy</c> in the kingdom screen's way: a vanilla peace decision added to the
    /// player's kingdom, which Coop sends to the server as a request. Reports whether the game allows it here.
    /// </summary>
    private static IEnumerable<StepWait?> ProposePeace(StepContext ctx)
    {
        var clan = Clan.PlayerClan ?? throw new StepFailedException("no player clan");
        var kingdom = clan.Kingdom ?? throw new StepFailedException("the player is in no kingdom");
        var enemy = KingdomById(ctx.Arg("enemy")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("enemy")}'");
        ctx.Assert(kingdom.IsAtWarWith(enemy), $"{kingdom.StringId} is not at war with {enemy.StringId} here");
        var ctor = typeof(MakePeaceKingdomDecision).GetConstructors()
            .FirstOrDefault(c => c.GetParameters() is { Length: >= 2 } p && p[0].ParameterType == typeof(Clan) && p[1].ParameterType == typeof(IFaction))
            ?? throw new StepFailedException("MakePeaceKingdomDecision has no (Clan, IFaction, ...) constructor");
        var decision = (MakePeaceKingdomDecision)ctor.Invoke(ctor.GetParameters()
            .Select((p, i) => i == 0 ? clan : i == 1 ? enemy : p.HasDefaultValue ? p.DefaultValue : (p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)).ToArray());
        var allowed = decision.IsAllowed();
        ctx.Log($"proposing peace with {enemy.StringId}: allowed {allowed}, cost {decision.GetProposalInfluenceCost()}, influence {clan.Influence:0}");
        kingdom.AddDecision(decision, false);
        ctx.Report("value", allowed);
        yield break;
    }

    /// <summary>
    /// Player (the realm's ruler): votes for peace with <c>enemy</c> as the kingdom screen's decision page does: an
    /// election for the open peace decision, the peace outcome chosen, then the choice applied. In Coop that sends the
    /// ruler's final vote to the server's voting round; without it the round runs out and the AI decides for the ruler.
    /// </summary>
    private static IEnumerable<StepWait?> VoteForPeace(StepContext ctx)
    {
        var kingdom = Clan.PlayerClan?.Kingdom ?? throw new StepFailedException("the player is in no kingdom");
        var enemy = KingdomById(ctx.Arg("enemy")) ?? throw new StepFailedException($"no kingdom '{ctx.Arg("enemy")}'");
        MakePeaceKingdomDecision? Open() => kingdom.UnresolvedDecisions.OfType<MakePeaceKingdomDecision>().FirstOrDefault(d => d.FactionToMakePeaceWith == enemy);
        yield return ctx.Until(() => Open() != null, $"a peace decision with {enemy.StringId} to reach this game", Sync);
        var decision = Open()!;

        var election = new KingdomElection(decision);
        typeof(KingdomElection).GetMethod("StartElection", Any, null, Type.EmptyTypes, null)?.Invoke(election, null);
        var peace = election.PossibleOutcomes.OfType<MakePeaceKingdomDecision.MakePeaceDecisionOutcome>().FirstOrDefault(o => o.ShouldPeaceBeDeclared)
            ?? throw new StepFailedException("the election has no peace outcome");
        var support = typeof(KingdomElection).GetMethod("OnPlayerSupport", Any) ?? throw new StepFailedException("KingdomElection has no OnPlayerSupport");
        support.Invoke(election, new object[] { peace, Supporter.SupportWeights.FullyPush });
        var apply = typeof(KingdomElection).GetMethod("ApplySelection", Any, null, Type.EmptyTypes, null) ?? throw new StepFailedException("KingdomElection has no ApplySelection");
        apply.Invoke(election, null);
        ctx.Log($"voted for peace with {enemy.StringId} in {kingdom.StringId}");
        ctx.Report("value", "peace");
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether Bellum would let <paramref name="clan"/> fabricate a claim on the title of <paramref name="fief"/> (its own
    /// FeudalClaimFabricationBehavior.TryGetFabricationTrack: a neighbouring, liege or subordinate title). Logs why not.
    /// </summary>
    internal static object? FabricationTarget(Clan clan, TaleWorlds.CampaignSystem.Settlements.Settlement fief, Action<string>? log = null)
    {
        var titlesType = FindType("BellumCivile.Behaviors.FeudalTitleBehavior");
        var fabType = FindType("BellumCivile.Behaviors.FeudalClaimFabricationBehavior");
        if (titlesType == null || fabType == null) return null;
        var get = typeof(Campaign).GetMethod(nameof(Campaign.GetCampaignBehavior))!;
        var titles = get.MakeGenericMethod(titlesType).Invoke(Campaign.Current, null);
        var title = FindType("BellumCivile.CheatCommands")?.GetMethod("ResolveFeudalTitleForDebug", Any)?.Invoke(null, new object?[] { titles, fief.StringId });
        var track = fabType.GetMethod("TryGetFabricationTrack", Any);
        if (title == null || track == null) return null;
        var args = new object?[track.GetParameters().Length];
        args[0] = titles; args[1] = clan; args[2] = title;
        var ok = track.Invoke(null, args) is true;
        if (!ok) log?.Invoke($"{clan.StringId} cannot claim {fief.StringId}: {args[4]}");
        return ok ? title : null;
    }

    internal static Kingdom? KingdomById(string id) => Kingdom.All.FirstOrDefault(k => k != null && k.StringId == id);

    // This game's own player clan's realm. Enough for the suite: it runs on a fresh world, where players join no kingdom
    // until bellum.makeRuler, and that realm is named to every step that needs it.
    internal static bool HasPlayers(Kingdom kingdom) => Clan.PlayerClan != null && kingdom.Clans.Contains(Clan.PlayerClan);

    internal static Type? FindType(string name)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (assembly.GetType(name, false) is { } type) return type;
            }
            catch (Exception)
            {
                // a dynamic or broken assembly
            }
        }
        return null;
    }

    // Campaign time on the server; in Coop through Coop's own time command (setting the mode here does not reach Coop's clock).
    internal static void SetTime(StepContext ctx, bool play, bool fast = false)
    {
        if (CommandLineFunctionality.HasFunctionForCommand("coop.debug.set_time_mode"))
            ctx.Log(CommandLineFunctionality.CallFunction("coop.debug.set_time_mode", new List<string> { play ? (fast ? "Play_2x" : "Play_1x") : "Pause" }, out _));
        else
            Campaign.Current.TimeControlMode = play ? CampaignTimeControlMode.UnstoppableFastForward : CampaignTimeControlMode.Stop;
    }
}
