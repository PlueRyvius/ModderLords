using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModderLords.Core.Compat.Authority;

public enum AuthorityVerdict
{
    /// <summary>Simulation or load-time code that changes the world: must run only on the server.</summary>
    ServerOnly,
    /// <summary>Server-only, and it writes mod state that player-facing code reads, so clients also need that state.</summary>
    NeedsStateSync,
    /// <summary>A player action that changes state Coop blocks or never replicates from a client: it silently does nothing.</summary>
    NeedsRelay,
    /// <summary>A postfix/finalizer on a method Coop skips on clients: Harmony still runs it there.</summary>
    LeakingPostfix,
    /// <summary>Already server-only: the mod checks authority itself, or Coop gates what runs it.</summary>
    AlreadyHandled,
    /// <summary>Runs on every peer and must agree: needs identical settings and data, not gating.</summary>
    Both,
    /// <summary>Local to each peer: display, setup, or no world change found.</summary>
    Local,
    /// <summary>The code alone does not decide it; a person should look.</summary>
    Review,
}

/// <summary>A verdict for one entry point. Evidence is the call path (at most 5 methods) ending in the deciding effect.</summary>
public sealed record RootVerdict(AuthorityRoot Root, AuthorityVerdict Verdict, string Reason, IReadOnlyList<string> Evidence, bool Opaque);

public sealed class AuthorityReport
{
    public string ModuleId { get; init; } = "";
    public bool NotAnalysable { get; init; }
    public List<RootVerdict> Roots { get; init; } = new();
    public List<string> Notes { get; init; } = new();

    /// <summary>Verdicts that call for a gate, a relay, state sync, or a look.</summary>
    [JsonIgnore]
    public IEnumerable<RootVerdict> ActionNeeded => Roots.Where(r => r.Verdict is AuthorityVerdict.ServerOnly or AuthorityVerdict.NeedsStateSync
        or AuthorityVerdict.NeedsRelay or AuthorityVerdict.LeakingPostfix or AuthorityVerdict.Review);

    public int Count(AuthorityVerdict verdict) => Roots.Count(r => r.Verdict == verdict);

    [JsonIgnore]
    public string Summary => NotAnalysable
        ? "not analysable: " + string.Join("; ", Notes.Take(2))
        : Roots.Count == 0
            ? "no entry points found"
            : string.Join(", ", Enum.GetValues<AuthorityVerdict>().Select(v => (v, n: Count(v))).Where(x => x.n > 0).Select(x => $"{x.v} {x.n}"));

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>
/// Step 3 of the authority classifier: walks each entry point of a <see cref="ModCodeModel"/> to the effects it can
/// reach and decides, against Coop's own <see cref="CoopSinkCatalogue"/>, where that code has to run.
/// </summary>
public static class AuthorityScan
{
    private const int MaxVisited = 5000;

    private static readonly string[] UiNamespaces =
    [
        "TaleWorlds.GauntletUI", "TaleWorlds.Engine.GauntletUI", "TaleWorlds.MountAndBlade.GauntletUI", "TaleWorlds.ScreenSystem",
        "TaleWorlds.MountAndBlade.View", "SandBox.View", "SandBox.GauntletUI", "TaleWorlds.TwoDimension", "TaleWorlds.InputSystem",
    ];
    private static readonly HashSet<string> PresentationTypes = new(StringComparer.Ordinal)
    {
        "TaleWorlds.Library.InformationManager", "TaleWorlds.Core.MBInformationManager", "TaleWorlds.Engine.SoundEvent",
    };
    private static readonly string[] WorldNamespaces = ["TaleWorlds.CampaignSystem", "TaleWorlds.Core", "TaleWorlds.MountAndBlade", "SandBox", "StoryMode"];
    private static readonly HashSet<string> AuthorityGetters = new(StringComparer.Ordinal)
    {
        "get_IsAuthority", "get_IsServer", "get_IsClient", "get_IsHost", "get_ShouldDeferToHost", "get_IsCoopClient", "get_IsCoopServer",
    };

    private enum Sink { Blocked, SyncedWrite, WorldWrite, Random, Presentation }

    /// <param name="plumbingWriters">
    /// A mod field written by more entry points than this is plumbing (a logger's fault flag, a shared cache), not state
    /// players see. Default: max(5, roots / 20).
    /// </param>
    public static AuthorityReport Classify(ModCodeModel model, CoopSinkCatalogue coop, int? plumbingWriters = null)
    {
        var report = new AuthorityReport { ModuleId = model.ModuleId, NotAnalysable = model.NotAnalysable, Notes = model.Notes.ToList() };
        if (model.NotAnalysable) return report;

        var walker = new Walker(model, coop);
        var analysed = model.Roots.Select(r => (root: r, fx: walker.Walk(r.Method), trigger: EffectiveTrigger(r))).ToList();

        var writers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var a in analysed)
            foreach (var w in a.fx.ModWrites) writers[w] = writers.GetValueOrDefault(w) + 1;
        var limit = plumbingWriters ?? Math.Max(5, model.Roots.Count / 20);

        static bool PlayerFacing(RootTrigger? t) => t is RootTrigger.PlayerInput or RootTrigger.Presentation or RootTrigger.Query;
        var playerFacingWrites = new HashSet<string>(analysed.Where(a => PlayerFacing(a.trigger)).SelectMany(a => a.fx.ModWrites), StringComparer.Ordinal);
        // Shared state = read by player-facing code, written only elsewhere, and by few enough entry points not to be plumbing.
        var sharedState = new HashSet<string>(
            analysed.Where(a => PlayerFacing(a.trigger)).SelectMany(a => a.fx.ModReads)
                .Where(f => writers.GetValueOrDefault(f) <= limit && !playerFacingWrites.Contains(f)),
            StringComparer.Ordinal);

        foreach (var a in analysed) report.Roots.Add(Decide(coop, a.root, a.fx, a.trigger, sharedState));
        return report;
    }

    /// <summary>A patch runs where its target runs; the target's name and type say where that usually is. Null = unknown.</summary>
    private static RootTrigger? EffectiveTrigger(AuthorityRoot root)
    {
        if (root.Patch is not { } p) return root.Trigger;
        var simple = Simple(p.TargetType);
        if (p.TargetMethod.Contains("_on_consequence", StringComparison.Ordinal)) return RootTrigger.PlayerInput;
        if (p.TargetMethod.Contains("_on_condition", StringComparison.Ordinal)) return RootTrigger.Query;
        if (p.TargetMethod.Contains("_on_init", StringComparison.Ordinal) || p.TargetMethod.Contains("_on_tick", StringComparison.Ordinal)) return RootTrigger.Presentation;
        // View models before game models: "ViewModel" also ends in "Model". A ViewModel's commands are the player's clicks.
        if (simple.EndsWith("ViewModel", StringComparison.Ordinal) || (simple.EndsWith("VM", StringComparison.Ordinal) && p.TargetMethod.StartsWith("Execute", StringComparison.Ordinal)))
            return RootTrigger.PlayerInput;
        if (IsPresentationType(p.TargetType) || simple.EndsWith("VM", StringComparison.Ordinal) || simple.EndsWith("View", StringComparison.Ordinal)
            || simple.Contains("Gauntlet", StringComparison.Ordinal) || simple.Contains("Screen", StringComparison.Ordinal)
            || simple.Contains("Tableau", StringComparison.Ordinal) || simple.Contains("Widget", StringComparison.Ordinal))
            return RootTrigger.Presentation;
        if (simple.EndsWith("Model", StringComparison.Ordinal)) return RootTrigger.Query;
        if (simple.StartsWith("Mission", StringComparison.Ordinal) || simple is "Agent" or "Formation" || simple.EndsWith("Logic", StringComparison.Ordinal))
            return RootTrigger.Mission;
        return null;
    }

    private static RootVerdict Decide(CoopSinkCatalogue coop, AuthorityRoot root, Effects fx, RootTrigger? trigger, HashSet<string> playerFacingReads)
    {
        RootVerdict V(AuthorityVerdict verdict, string reason, Sink? sink = null)
        {
            var evidence = sink is { } s && fx.Sinks.TryGetValue(s, out var hit) ? Path(fx, hit.at).Append(hit.text).ToList() : new List<string>();
            return new RootVerdict(root, verdict, reason, evidence, fx.Opaque);
        }

        Sink? change = fx.Has(Sink.Blocked) ? Sink.Blocked : fx.Has(Sink.SyncedWrite) ? Sink.SyncedWrite : fx.Has(Sink.WorldWrite) ? Sink.WorldWrite : null;

        if (root.Patch is { } p)
        {
            if (p.TargetType == "?") return V(AuthorityVerdict.Review, "patches methods chosen at runtime (TargetMethods)");
            if (coop.IsBehaviourGated(p.TargetType))
                return V(AuthorityVerdict.AlreadyHandled, $"{Simple(p.TargetType)} only runs on the server under Coop");
            if (coop.IsBlocked(p.TargetType, p.TargetMethod))
            {
                var target = $"{Simple(p.TargetType)}.{p.TargetMethod}";
                return p.Kind switch
                {
                    PatchKind.Postfix or PatchKind.Finalizer when fx.AuthorityCheck => V(AuthorityVerdict.AlreadyHandled, "checks authority itself"),
                    PatchKind.Postfix or PatchKind.Finalizer => V(AuthorityVerdict.LeakingPostfix,
                        $"Coop skips {target} on clients, but Harmony still runs this {p.Kind.ToString().ToLowerInvariant()} there", change),
                    PatchKind.Transpiler => V(AuthorityVerdict.AlreadyHandled, $"rewrites {target}, which Coop already gates on clients"),
                    _ => V(AuthorityVerdict.Review, $"a prefix beside Coop's own client gate on {target}; patch order decides which runs"),
                };
            }
        }

        if (fx.AuthorityCheck && trigger is not RootTrigger.Presentation) return V(AuthorityVerdict.AlreadyHandled, "checks authority itself");

        var shared = fx.ModWrites.Where(playerFacingReads.Contains).Select(Short).OrderBy(x => x, StringComparer.Ordinal).Take(3).ToList();
        switch (trigger)
        {
            case RootTrigger.Simulation or RootTrigger.Session:
            {
                var deciding = change ?? (fx.Has(Sink.Random) ? Sink.Random : null);
                var who = trigger == RootTrigger.Simulation ? "simulation " : "load-time code ";
                if (deciding is { } d)
                {
                    var why = who + fx.Sinks[d].text;
                    return shared.Count > 0
                        ? V(AuthorityVerdict.NeedsStateSync, why + "; also writes " + string.Join(", ", shared) + ", which player-facing code reads", d)
                        : V(AuthorityVerdict.ServerOnly, why, d);
                }
                if (trigger == RootTrigger.Simulation && shared.Count > 0)
                    return V(AuthorityVerdict.NeedsStateSync, who + "writes " + string.Join(", ", shared) + ", which player-facing code reads");
                if (fx.Has(Sink.Presentation)) return V(AuthorityVerdict.Local, "display only", Sink.Presentation);
                return V(AuthorityVerdict.Local, trigger == RootTrigger.Session ? "setup only; no world change found" : "no world change found");
            }
            case RootTrigger.PlayerInput:
                if (fx.Has(Sink.Blocked))
                    return V(AuthorityVerdict.NeedsRelay, "player action " + fx.Sinks[Sink.Blocked].text + "; on a client it silently does nothing", Sink.Blocked);
                if (change is { } pc)
                    return V(AuthorityVerdict.NeedsRelay, "player action " + fx.Sinks[pc].text + "; done on a client, the server never hears of it", pc);
                return V(AuthorityVerdict.Local, "no world change found");
            case RootTrigger.Query:
                if (change is { } qc) return V(AuthorityVerdict.Review, "a query that " + fx.Sinks[qc].text, qc);
                if (fx.Has(Sink.Random)) return V(AuthorityVerdict.Review, "a query that uses MBRandom, so peers can disagree", Sink.Random);
                return V(AuthorityVerdict.Both, "every peer asks this; settings and data must match");
            case RootTrigger.Mission:
                return change is { } mc
                    ? V(AuthorityVerdict.Review, "mission code that " + fx.Sinks[mc].text, mc)
                    : V(AuthorityVerdict.Both, "runs in each peer's own mission");
            case RootTrigger.Presentation:
                return V(AuthorityVerdict.Local, "display only");
            case RootTrigger.Lifecycle:
                return V(AuthorityVerdict.Local, "module lifecycle");
            default:
                if (change is { } uc)
                    return V(AuthorityVerdict.Review, "patches an ungated vanilla method and " + fx.Sinks[uc].text + "; who calls the target decides", uc);
                if (fx.Has(Sink.Random)) return V(AuthorityVerdict.Review, "patches an ungated vanilla method and uses MBRandom", Sink.Random);
                return fx.Has(Sink.Presentation)
                    ? V(AuthorityVerdict.Local, "display only", Sink.Presentation)
                    : V(AuthorityVerdict.Both, "no world change found; runs wherever its target runs");
        }
    }

    private sealed class Effects
    {
        public readonly Dictionary<string, string?> Parent = new(StringComparer.Ordinal);
        public readonly Dictionary<Sink, (string text, string at)> Sinks = new();
        public readonly HashSet<string> ModWrites = new(StringComparer.Ordinal);
        public readonly HashSet<string> ModReads = new(StringComparer.Ordinal);
        public bool AuthorityCheck;
        public bool Opaque;
        public bool Truncated;

        public void Hit(Sink sink, string text, string at) => Sinks.TryAdd(sink, (text, at));
        public bool Has(Sink sink) => Sinks.ContainsKey(sink);
    }

    private sealed class Walker
    {
        private readonly ModCodeModel _model;
        private readonly HashSet<string> _blocked;
        private readonly HashSet<string> _gatedActionTypes;
        private readonly HashSet<string> _synced;
        private readonly HashSet<string> _rootMethods;

        public Walker(ModCodeModel model, CoopSinkCatalogue coop)
        {
            _model = model;
            _blocked = new HashSet<string>(coop.Gates.Select(g => g.TargetType + "::" + g.TargetMethod), StringComparer.Ordinal);
            _gatedActionTypes = new HashSet<string>(coop.Gates.Select(g => g.TargetType).Where(t => t.EndsWith("Action", StringComparison.Ordinal)), StringComparer.Ordinal);
            _synced = new HashSet<string>(coop.SyncedMembers.Concat(coop.InterceptedMembers), StringComparer.Ordinal);
            _rootMethods = new HashSet<string>(model.Roots.Select(r => r.Method), StringComparer.Ordinal);
        }

        // Coop gates action classes at ApplyInternal; their public Apply* entry points all funnel into it.
        private bool IsBlocked(string type, string method) =>
            _blocked.Contains(type + "::" + method) || (method.StartsWith("Apply", StringComparison.Ordinal) && _gatedActionTypes.Contains(type));

        private bool IsModState(string type) => _model.BaseTypes.ContainsKey(type) && !type.Contains('<');

        public Effects Walk(string rootId)
        {
            var fx = new Effects();
            fx.Parent[rootId] = null;
            var queue = new Queue<(string id, int depth)>();
            queue.Enqueue((rootId, 0));
            while (queue.Count > 0)
            {
                var (id, depth) = queue.Dequeue();
                if (!_model.Methods.TryGetValue(id, out var node)) continue;
                if (node.Opaque) fx.Opaque = true;

                foreach (var w in node.FieldWrites)
                {
                    var type = TypeOf(w);
                    if (_synced.Contains(w)) fx.Hit(Sink.SyncedWrite, "changes " + Short(w) + ", which Coop syncs", id);
                    else if (IsWorldType(type)) fx.Hit(Sink.WorldWrite, "changes " + Short(w), id);
                    else if (IsModState(type)) fx.ModWrites.Add(w);
                }
                foreach (var r in node.FieldReads)
                    if (IsModState(TypeOf(r))) fx.ModReads.Add(r);

                // Record every effect of this method's own calls before following any of them.
                foreach (var callee in node.Calls)
                {
                    var sep = callee.IndexOf("::", StringComparison.Ordinal);
                    if (sep <= 0) continue;
                    var type = callee[..sep];
                    var name = callee[(sep + 2)..];
                    if (depth <= 1 && AuthorityGetters.Contains(name)) fx.AuthorityCheck = true;
                    if (IsBlocked(type, name)) fx.Hit(Sink.Blocked, "calls " + Short(callee) + ", which Coop blocks on clients", id);
                    else if (name.StartsWith("set_", StringComparison.Ordinal) && _synced.Contains(type + "." + name[4..]))
                        fx.Hit(Sink.SyncedWrite, "sets " + Short(type + "." + name[4..]) + ", which Coop syncs", id);
                    else if (name.StartsWith("set_", StringComparison.Ordinal) && IsWorldType(type))
                        fx.Hit(Sink.WorldWrite, "sets " + Short(type + "." + name[4..]), id);
                    else if (type.EndsWith(".MBRandom", StringComparison.Ordinal)) fx.Hit(Sink.Random, "uses MBRandom", id);
                    else if (IsPresentationType(type)) fx.Hit(Sink.Presentation, "shows " + Short(callee), id);
                }
                foreach (var callee in node.Calls) Enqueue(fx, queue, callee, id, depth, node.VirtualCalls.Contains(callee));
                // Delegates run later, from wherever they are invoked; ones that are entry points get their own verdict.
                foreach (var target in node.Delegates)
                    if (!_rootMethods.Contains(target)) Enqueue(fx, queue, target, id, depth, dispatch: false);
            }
            return fx;
        }

        /// <summary>
        /// Queues the callee, plus its mod overrides when the call is virtual; past the visit cap nothing more is queued
        /// (the walk drains and is marked Truncated).
        /// </summary>
        private void Enqueue(Effects fx, Queue<(string, int)> queue, string callee, string from, int depth, bool dispatch)
        {
            if (fx.Truncated) return;
            foreach (var target in dispatch ? _model.Dispatch(callee) : [callee])
            {
                if (!_model.Methods.ContainsKey(target) || fx.Parent.ContainsKey(target)) continue;
                if (fx.Parent.Count >= MaxVisited) { fx.Truncated = true; return; }
                fx.Parent[target] = from;
                queue.Enqueue((target, depth + 1));
            }
        }
    }

    private static IEnumerable<string> Path(Effects fx, string at)
    {
        var chain = new List<string>();
        for (string? cur = at; cur is not null && chain.Count < 64; cur = fx.Parent.GetValueOrDefault(cur)) chain.Add(Short(cur));
        chain.Reverse();
        return chain.Count <= 5 ? chain : chain.Take(2).Append("…").Concat(chain.Skip(chain.Count - 2));
    }

    // Game-menu objects (MenuCallbackArgs, GameMenu navigation) are how a menu displays itself; Coop's real menu gates are
    // in the blocked list, which is checked before this.
    private static bool IsPresentationType(string type) =>
        PresentationTypes.Contains(type) || type.Contains(".ViewModelCollection", StringComparison.Ordinal)
        || type.StartsWith("TaleWorlds.CampaignSystem.GameMenus.", StringComparison.Ordinal)
        || UiNamespaces.Any(ns => type.StartsWith(ns, StringComparison.Ordinal));

    private static bool IsWorldType(string type) =>
        WorldNamespaces.Any(ns => type.StartsWith(ns + ".", StringComparison.Ordinal)) && !IsPresentationType(type);

    private static string TypeOf(string member)
    {
        var i = member.LastIndexOf('.');
        return i > 0 ? member[..i] : member;
    }

    private static string Simple(string type)
    {
        var i = type.LastIndexOf('.');
        return i >= 0 ? type[(i + 1)..] : type;
    }

    /// <summary>"Ns.Type::Method" → "Type.Method"; "Ns.Type.field" → "Type.field".</summary>
    private static string Short(string id)
    {
        var sep = id.IndexOf("::", StringComparison.Ordinal);
        if (sep > 0) return Simple(id[..sep]) + "." + id[(sep + 2)..];
        var i = id.LastIndexOf('.');
        return i > 0 ? Simple(id[..i]) + id[i..] : id;
    }
}
