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
    /// <summary>A player action that changes mod state the server's own simulation reads: done on a client, the server never sees it.</summary>
    PlayerStateUnsynced,
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

/// <summary>
/// A verdict for one entry point. Evidence is the call path (at most 5 methods) ending in the deciding effect.
/// Flags are extra findings ("RegistersUI: …", "HostIsOnlyPlayer: …"). GateInstead is set for a server-only handler that
/// also registers UI: the methods to skip on clients in its place, so the UI still registers there.
/// </summary>
public sealed record RootVerdict(AuthorityRoot Root, AuthorityVerdict Verdict, string Reason, IReadOnlyList<string> Evidence, bool Opaque,
    IReadOnlyList<string>? Flags = null, IReadOnlyList<string>? GateInstead = null);

public sealed class AuthorityReport
{
    public string ModuleId { get; init; } = "";
    public bool NotAnalysable { get; init; }
    public List<RootVerdict> Roots { get; init; } = new();
    public List<string> Notes { get; init; } = new();

    /// <summary>Verdicts that call for a gate, a relay, state sync, or a look.</summary>
    [JsonIgnore]
    public IEnumerable<RootVerdict> ActionNeeded => Roots.Where(r => r.Verdict is AuthorityVerdict.ServerOnly or AuthorityVerdict.NeedsStateSync
        or AuthorityVerdict.NeedsRelay or AuthorityVerdict.PlayerStateUnsynced or AuthorityVerdict.LeakingPostfix or AuthorityVerdict.Review);

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
    /// <summary>"SimpleType::Method" calls that add menus, dialog lines or screens: skipping the caller on a client hides them there.</summary>
    private static readonly HashSet<string> UiRegistrations = new(StringComparer.Ordinal)
    {
        "CampaignGameStarter::AddGameMenu", "CampaignGameStarter::AddGameMenuOption", "CampaignGameStarter::AddWaitGameMenu",
        "CampaignGameStarter::AddPlayerLine", "CampaignGameStarter::AddDialogLine", "CampaignGameStarter::AddRepeatablePlayerLine",
        "ScreenManager::PushScreen", "ScreenManager::AddGlobalLayer", "GauntletLayer::.ctor",
    };
    /// <summary>Getters for "the" player; on a Coop server they answer for the host.</summary>
    private static readonly HashSet<string> HostGetters = new(StringComparer.Ordinal)
    {
        "Hero::get_MainHero", "Clan::get_PlayerClan", "MobileParty::get_MainParty", "Campaign::get_MainParty", "PartyBase::get_MainParty",
    };
    /// <summary>Opening a screen or switching menus: navigation on the player's own client, not a world change.</summary>
    private static readonly HashSet<string> Navigation = new(StringComparer.Ordinal)
    {
        "GameStateManager::PushState", "GameMenu::ActivateGameMenu", "GameMenu::SwitchToMenu", "GameMenu::ExitToLast",
        "PlayerEncounter::set_LeaveEncounter", "PlayerEncounter::Finish",
    };

    private enum Sink { Blocked, SyncedWrite, WorldWrite, Random, Presentation, RegistersUI, ShowsPopup, HostPlayer, HiddenByCoop }

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

        // Mod state the server's own simulation reads: a player-facing write to it stays on that client.
        var serverReads = new HashSet<string>(
            analysed.Where(a => a.trigger is RootTrigger.Simulation or RootTrigger.Session).SelectMany(a => a.fx.ModReads)
                .Where(f => writers.GetValueOrDefault(f) <= limit && !IsUiState(model, TypeOf(f))),
            StringComparer.Ordinal);
        var playerReached = new HashSet<string>(analysed.Where(a => PlayerFacing(a.trigger)).SelectMany(a => a.fx.Parent.Keys), StringComparer.Ordinal);
        var split = new HandlerSplit(model, walker, playerReached);

        foreach (var a in analysed)
        {
            var v = Decide(coop, a.root, a.fx, a.trigger, sharedState, serverReads);
            if (v.Verdict is AuthorityVerdict.ServerOnly or AuthorityVerdict.NeedsStateSync && a.root.Patch is null
                && a.trigger is RootTrigger.Simulation or RootTrigger.Session)
                v = Refine(v, a.fx, split);
            report.Roots.Add(v);
        }
        return report;
    }

    /// <summary>
    /// For a handler that is about to be gated on clients: flags reads of the host's own hero/party, and when it also
    /// registers UI, gates only its server work (or sends it to Review) so that UI still registers on clients.
    /// </summary>
    private static RootVerdict Refine(RootVerdict v, Effects fx, HandlerSplit split)
    {
        var flags = new List<string>();
        if (fx.Sinks.TryGetValue(Sink.ShowsPopup, out var popup))
            flags.Add($"PopupLost: {popup.text} in {Short(popup.at)}; this runs only on the server, so no player sees it");
        if (fx.Sinks.TryGetValue(Sink.HostPlayer, out var host))
            flags.Add($"HostIsOnlyPlayer: {host.text} in {Short(host.at)}; on a Coop server that is the host, not each player");
        if (fx.Sinks.TryGetValue(Sink.RegistersUI, out var ui))
        {
            flags.Add($"RegistersUI: {ui.text} in {Short(ui.at)}");
            var gates = new List<string>();
            var why = split.Split(v.Root.Method, gates);
            v = why is null && gates.Count > 0
                ? v with
                {
                    Reason = v.Reason + "; it also " + ui.text + ", so on clients only " + string.Join(", ", gates.Select(Short)) + " is skipped and the UI stays",
                    GateInstead = gates,
                }
                : v with
                {
                    Verdict = AuthorityVerdict.Review,
                    Reason = v.Reason + "; but it also " + ui.text + " and " + (why ?? "its server work could not be separated") + ", so it keeps running on clients to keep that UI",
                };
        }
        return flags.Count > 0 ? v with { Flags = flags } : v;
    }

    /// <summary>Finds the callees of a UI-registering handler that do its server work and can be skipped on their own.</summary>
    private sealed class HandlerSplit(ModCodeModel model, Walker walker, HashSet<string> playerReached)
    {
        private readonly HashSet<string> _roots = new(model.Roots.Select(r => r.Method), StringComparer.Ordinal);

        /// <summary>Null when all server work under the method sits in skippable callees (added to gates); otherwise why not.</summary>
        public string? Split(string id, List<string> gates) => Split(id, gates, new HashSet<string>(StringComparer.Ordinal));

        private string? Split(string id, List<string> gates, HashSet<string> seen)
        {
            if (!seen.Add(id) || !model.Methods.TryGetValue(id, out var node)) return null;
            if (ServerWork(walker.Walk(id, followCalls: false)) is { } own) return $"{Short(id)} itself {own}";
            var callees = node.Calls.SelectMany(c => node.VirtualCalls.Contains(c) ? model.Dispatch(c) : [c])
                .Concat(node.Delegates.Where(d => !_roots.Contains(d)))
                .Where(model.Methods.ContainsKey).Distinct(StringComparer.Ordinal).ToList();
            foreach (var c in callees)
            {
                var fx = walker.Walk(c);
                if (ServerWork(fx) is null) continue;
                if (fx.Has(Sink.RegistersUI))
                {
                    if (Split(c, gates, seen) is { } why) return why;
                    continue;
                }
                var name = c[(c.IndexOf("::", StringComparison.Ordinal) + 2)..];
                if (_roots.Contains(c)) return $"{Short(c)} is also an entry point";
                if (playerReached.Contains(c)) return $"{Short(c)} is also used by player-facing code";
                if (name.StartsWith('.') || name.StartsWith("get_", StringComparison.Ordinal) || name.StartsWith("set_", StringComparison.Ordinal))
                    return $"{Short(c)} is a constructor or property";
                if (!model.Methods[c].IsVoid) return $"{Short(c)} returns a value";
                if (!gates.Contains(c)) gates.Add(c);
            }
            return null;
        }

        private static string? ServerWork(Effects fx) =>
            fx.Sinks.TryGetValue(Sink.Blocked, out var b) ? b.text
            : fx.Sinks.TryGetValue(Sink.SyncedWrite, out var s) ? s.text
            : fx.Sinks.TryGetValue(Sink.WorldWrite, out var w) ? w.text
            : fx.Sinks.TryGetValue(Sink.Random, out var r) ? r.text
            : null;
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

    private static RootVerdict Decide(CoopSinkCatalogue coop, AuthorityRoot root, Effects fx, RootTrigger? trigger, HashSet<string> playerFacingReads,
        HashSet<string> serverReads)
    {
        RootVerdict V(AuthorityVerdict verdict, string reason, Sink? sink = null, (string text, string at)? hit = null)
        {
            if (hit is null && sink is { } s && fx.Sinks.TryGetValue(s, out var found)) hit = found;
            var evidence = hit is { } h ? Path(fx, h.at).Append(h.text).ToList() : new List<string>();
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
            {
                // Coop's own request (Publishes) and its deliberate refusal (ClientDeny) are not gaps a relay should fill.
                foreach (var (kind, hit) in fx.Blocked)
                {
                    if (kind is CoopGateKind.Publishes or CoopGateKind.ClientDeny) continue;
                    var outcome = kind == CoopGateKind.ClientLocal ? "; the change stays on that client" : "; on a client it silently does nothing";
                    return V(AuthorityVerdict.NeedsRelay, "player action " + hit.text + outcome, hit: hit);
                }
                var unsynced = fx.ModWrites.Where(serverReads.Contains).Select(Short).OrderBy(x => x, StringComparer.Ordinal).Take(3).ToList();
                if (unsynced.Count > 0)
                    return V(AuthorityVerdict.PlayerStateUnsynced,
                        "player changes " + string.Join(", ", unsynced) + ", which server-side simulation reads; done on a client, the server never sees it");
                Sink? world = fx.Has(Sink.SyncedWrite) ? Sink.SyncedWrite : fx.Has(Sink.WorldWrite) ? Sink.WorldWrite : null;
                if (world is { } pc)
                    return V(AuthorityVerdict.NeedsRelay, "player action " + fx.Sinks[pc].text + "; done on a client, the server never hears of it", pc);
                if (fx.Sinks.TryGetValue(Sink.HiddenByCoop, out var hidden))
                    return V(AuthorityVerdict.Review, "player action " + hidden.text + ", so the player sees nothing", hit: hidden);
                if (fx.Blocked.TryGetValue(CoopGateKind.Publishes, out var published))
                    return V(AuthorityVerdict.AlreadyHandled, "player action " + published.text + "; Coop already carries it", hit: published);
                if (fx.Blocked.TryGetValue(CoopGateKind.ClientDeny, out var denied))
                    return V(AuthorityVerdict.Local, "player action " + denied.text + ", so players cannot use it (Coop's choice)", hit: denied);
                return V(AuthorityVerdict.Local, "no world change found");
            }
            case RootTrigger.Query:
                if (change is { } qc) return V(AuthorityVerdict.Review, "a query that " + fx.Sinks[qc].text, qc);
                if (fx.Has(Sink.Random)) return V(AuthorityVerdict.Review, "a query that uses MBRandom, so peers can disagree", Sink.Random);
                return V(AuthorityVerdict.Both, "every peer asks this; settings and data must match");
            case RootTrigger.Mission:
                return change is { } mc
                    ? V(AuthorityVerdict.Review, "mission code that " + fx.Sinks[mc].text, mc)
                    : V(AuthorityVerdict.Both, "runs in each peer's own mission");
            case RootTrigger.Presentation:
                return fx.Sinks.TryGetValue(Sink.HiddenByCoop, out var hiddenScreen)
                    ? V(AuthorityVerdict.Review, hiddenScreen.text + ", so the player sees nothing", hit: hiddenScreen)
                    : V(AuthorityVerdict.Local, "display only");
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
        /// <summary>First blocked call reached per Coop gate kind, in the order found.</summary>
        public readonly Dictionary<CoopGateKind, (string text, string at)> Blocked = new();
        public bool AuthorityCheck;
        public bool Opaque;
        public bool Truncated;

        public void Hit(Sink sink, string text, string at) => Sinks.TryAdd(sink, (text, at));
        public bool Has(Sink sink) => Sinks.ContainsKey(sink);
    }

    private sealed class Walker
    {
        private readonly ModCodeModel _model;
        private readonly Dictionary<string, CoopGateKind> _blocked = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CoopGateKind> _gatedActionTypes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _synced;
        private readonly HashSet<string> _rootMethods;
        private readonly Dictionary<string, Effects> _cache = new(StringComparer.Ordinal);

        public Walker(ModCodeModel model, CoopSinkCatalogue coop)
        {
            _model = model;
            foreach (var g in coop.Gates) _blocked.TryAdd(g.TargetType + "::" + g.TargetMethod, g.Kind);
            // An action class's ApplyInternal gate speaks for its Apply* entry points, so it goes first.
            foreach (var g in coop.Gates.Where(g => g.TargetType.EndsWith("Action", StringComparison.Ordinal)).OrderBy(g => g.TargetMethod == "ApplyInternal" ? 0 : 1))
                _gatedActionTypes.TryAdd(g.TargetType, g.Kind);
            _synced = new HashSet<string>(coop.SyncedMembers.Concat(coop.InterceptedMembers), StringComparer.Ordinal);
            _rootMethods = new HashSet<string>(model.Roots.Select(r => r.Method), StringComparer.Ordinal);
        }

        // Coop gates action classes at ApplyInternal; their public Apply* entry points all funnel into it.
        private CoopGateKind? BlockKind(string type, string method)
        {
            if (_blocked.TryGetValue(type + "::" + method, out var exact)) return exact;
            if (method.StartsWith("Apply", StringComparison.Ordinal) && _gatedActionTypes.TryGetValue(type, out var action)) return action;
            return null;
        }

        private static string KindText(CoopGateKind kind) => kind switch
        {
            CoopGateKind.Publishes => "which Coop turns into its own request to the server",
            CoopGateKind.ClientDeny => "which Coop refuses on clients",
            CoopGateKind.ClientLocal => "which Coop lets run on a client without telling the server",
            _ => "which Coop blocks on clients",
        };

        private bool IsModState(string type) => _model.BaseTypes.ContainsKey(type) && !type.Contains('<');

        /// <summary>Effects reachable from a method; with followCalls false, only the method's own instructions. Full walks are cached.</summary>
        public Effects Walk(string rootId, bool followCalls = true)
        {
            if (followCalls && _cache.TryGetValue(rootId, out var cached)) return cached;
            var fx = WalkUncached(rootId, followCalls);
            if (followCalls) _cache[rootId] = fx;
            return fx;
        }

        private Effects WalkUncached(string rootId, bool followCalls)
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
                // A constructor filling in its own object's fields is initialisation, not a change to existing state; counting
                // it would make every settings object with defaults look written by whoever creates one.
                var sep0 = id.IndexOf("::", StringComparison.Ordinal);
                var ownType = sep0 > 0 ? id[..sep0] : "";
                static bool IsCtor(string? m) => m is not null && (m.EndsWith("::.ctor", StringComparison.Ordinal) || m.EndsWith("::.cctor", StringComparison.Ordinal));
                // Also a property setter the constructor itself calls (`Max = 5` in a ctor goes through set_Max).
                var from = fx.Parent.GetValueOrDefault(id);
                var initializer = IsCtor(id)
                    || (sep0 > 0 && id.AsSpan(sep0 + 2).StartsWith("set_", StringComparison.Ordinal) && IsCtor(from) && from!.StartsWith(ownType + "::", StringComparison.Ordinal));

                foreach (var w in node.FieldWrites)
                {
                    var type = TypeOf(w);
                    if (_synced.Contains(w)) fx.Hit(Sink.SyncedWrite, "changes " + Short(w) + ", which Coop syncs", id);
                    else if (IsWorldType(type)) fx.Hit(Sink.WorldWrite, "changes " + Short(w), id);
                    else if (IsModState(type) && !(initializer && type == ownType)) fx.ModWrites.Add(w);
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
                    var key = Simple(type) + "::" + name;
                    if (UiRegistrations.Contains(key)) fx.Hit(Sink.RegistersUI, "registers " + Short(callee), id);
                    else if (Simple(type).EndsWith("InformationManager", StringComparison.Ordinal)
                        && name.StartsWith("Show", StringComparison.Ordinal) && name.EndsWith("Inquiry", StringComparison.Ordinal))
                        fx.Hit(Sink.ShowsPopup, "shows a popup (" + Short(callee) + ")", id);
                    if (HostGetters.Contains(key) && type.StartsWith("TaleWorlds.", StringComparison.Ordinal))
                        fx.Hit(Sink.HostPlayer, "reads " + Simple(type) + "." + name[4..], id);

                    if (Navigation.Contains(key))
                    {
                        if (node.Calls.Any(c => c.EndsWith(".QuestsState::.ctor", StringComparison.Ordinal)))
                            fx.Hit(Sink.HiddenByCoop, "opens the quest screen, which Coop never opens", id);
                        else fx.Hit(Sink.Presentation, "opens " + Short(callee), id);
                    }
                    else if (BlockKind(type, name) is { } kind)
                    {
                        var text = "calls " + Short(callee) + ", " + KindText(kind);
                        fx.Hit(Sink.Blocked, text, id);
                        fx.Blocked.TryAdd(kind, (text, id));
                    }
                    else if (name.StartsWith("set_", StringComparison.Ordinal) && _synced.Contains(type + "." + name[4..]))
                        fx.Hit(Sink.SyncedWrite, "sets " + Short(type + "." + name[4..]) + ", which Coop syncs", id);
                    else if (name.StartsWith("set_", StringComparison.Ordinal) && IsWorldType(type))
                        fx.Hit(Sink.WorldWrite, "sets " + Short(type + "." + name[4..]), id);
                    else if (type.EndsWith(".MBRandom", StringComparison.Ordinal)) fx.Hit(Sink.Random, "uses MBRandom", id);
                    else if (IsPresentationType(type)) fx.Hit(Sink.Presentation, "shows " + Short(callee), id);
                }
                if (!followCalls) break;
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

    /// <summary>Fields of view models, layers and screens: display state, not a setting the server acts on.</summary>
    private static bool IsUiState(ModCodeModel model, string type)
    {
        var plus = type.IndexOf('+');
        var outer = plus > 0 ? type[..plus] : type;
        return model.BaseChain(outer).Prepend(outer).Any(t =>
        {
            var s = Simple(t);
            return IsPresentationType(t) || s.EndsWith("VM", StringComparison.Ordinal) || s.EndsWith("ViewModel", StringComparison.Ordinal)
                || s.Contains("Gauntlet", StringComparison.Ordinal) || s.EndsWith("Screen", StringComparison.Ordinal) || s.EndsWith("Layer", StringComparison.Ordinal);
        });
    }

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

    /// <summary>"Ns.Type::Method" → "Type.Method"; "Ns.Type.field" → "Type.field"; an auto-property's backing field → "Type.Property".</summary>
    private static string Short(string id)
    {
        var sep = id.IndexOf("::", StringComparison.Ordinal);
        if (sep > 0) return Simple(id[..sep]) + "." + id[(sep + 2)..];
        var i = id.LastIndexOf('.');
        if (i <= 0) return id;
        var member = id[(i + 1)..];
        if (member.StartsWith('<') && member.EndsWith(">k__BackingField", StringComparison.Ordinal)) member = member[1..member.IndexOf('>')];
        return Simple(id[..i]) + "." + member;
    }
}
