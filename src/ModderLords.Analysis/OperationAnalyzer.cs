using System.Collections.Immutable;
using System.Text.Json;

namespace ModderLords.Analysis;

public sealed class OperationAnalyzer(string cacheDirectory)
{
    public const string RulesVersion = "operations-4";
    public AnalysisReport Analyze(AnalysisRequest request, CancellationToken cancellation = default)
    {
        var inputs = InputResolver.Resolve(request, cancellation);
        var gaps = inputs.Gaps.ToList(); var fingerprints = inputs.Fingerprints.ToList();
        var localFiles = inputs.LocalFiles.ToList();
        var loaded = new List<(AssemblyInput Input, AssemblyGraph Graph)>();
        var graphCache = new Dictionary<string, AssemblyGraph>();
        foreach (var input in inputs.Assemblies)
        {
            cancellation.ThrowIfCancellationRequested();
            Load(input);
        }
        // Resolve reference closure using the launch-side search order. Framework references are not mod roots.
        for (var i = 0; i < loaded.Count; i++)
        {
            var (input, graph) = loaded[i];
            foreach (var name in graph.References)
            {
                if (IsFramework(name) || loaded.Any(x => x.Input.Side == input.Side && x.Graph.Name == name)) continue;
                var search = input.Side == ExecutionSide.Server ? request.ServerSearchPaths : request.ClientSearchPaths;
                var candidates = search.Prepend(Path.GetDirectoryName(input.Path)!).Select(d => Path.Combine(d, name + ".dll")).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (candidates.Length == 0) { gaps.Add(new(input.Module, "unresolved-reference", $"{input.Side}: {graph.Name} → {name}")); continue; }
                if (candidates.Select(AnalysisJson.FileHash).Distinct().Skip(1).Any()) gaps.Add(new(input.Module, "ambiguous-reference", $"{input.Side}: {name} has different bytes in the search paths; activation requires a verified resolver binding."));
                var dependency = new AssemblyInput("$dependency", candidates[0], input.Side, false);
                Load(dependency);
            }
        }
        var operations = new List<OperationFinding>();
        foreach (var side in Enum.GetValues<ExecutionSide>())
        {
            var sideGraphs = loaded.Where(x => x.Input.Side == side).ToArray();
            var nodes = sideGraphs.SelectMany(x => x.Graph.Methods).GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.DistinctBy(x => x.Identity).ToArray());
            var all = nodes.Values.SelectMany(x => x).ToArray();
            var types = sideGraphs.SelectMany(x => x.Graph.Types).GroupBy(n => n.Type).ToDictionary(g => g.Key, g => g.First());
            var bindings = sideGraphs.SelectMany(x => x.Graph.Bindings).GroupBy(b => b.Service).ToDictionary(g => g.Key, g => g.Select(x => x.Implementation).Distinct().ToArray());
            var callbacks = all.SelectMany(n => n.Registrations).Select(r => r.Callback).ToHashSet();
            var byName = all.GroupBy(n => n.Name).ToDictionary(g => g.Key, g => g.ToArray());
            var dispatchCache = new Dictionary<(string, string), MethodNode[]>();
            foreach (var (input, graph) in sideGraphs.Where(x => x.Input.IsEntry)) foreach (var root in graph.Methods.Where(n => n.IsRoot || callbacks.Contains(n.Key) || n.IsVirtual && IsLifecycleType(n.Type)))
            {
                cancellation.ThrowIfCancellationRequested();
                var evidence = new Dictionary<string, EffectWitness>();
                var queue = new Queue<(MethodNode Node, ImmutableArray<string> Chain)>(); var visited = new HashSet<string>();
                queue.Enqueue((root, ImmutableArray.Create(root.Key)));
                while (queue.TryDequeue(out var current))
                {
                    if (!visited.Add(current.Node.Identity)) continue;
                    if (visited.Count > 1500) { Add("analysis-budget", current.Node, current.Chain, 0, "Operation exceeds the bounded graph traversal; remaining effects are unknown."); break; }
                    foreach (var effect in current.Node.Effects) Add(effect.Kind, current.Node, current.Chain, effect.Offset, effect.Detail + (effect.AuthorityGuarded ? " [direct mutation dominated by recognized authority branch]" : ""));
                    foreach (var edge in current.Node.Calls)
                    {
                        if (nodes.TryGetValue(edge.Target, out var targets)) foreach (var target in targets) queue.Enqueue((target, current.Chain.Add(target.Key)));
                        else if (!IsKnownExternal(edge.Target)) Add("unresolved-call", current.Node, current.Chain, edge.Offset, edge.Target);
                        if (edge.Kind == "callvirt" && types.TryGetValue(edge.ReceiverType, out _))
                        {
                            var methodName = edge.Target.Split("::").Last().Split('(')[0];
                            if (!dispatchCache.TryGetValue((edge.ReceiverType, methodName), out var implementations))
                            {
                                implementations = byName.TryGetValue(methodName, out var named) ? named.Where(n => n.Type != edge.ReceiverType && IsSubtype(n.Type, edge.ReceiverType)).ToArray() : [];
                                dispatchCache[(edge.ReceiverType, methodName)] = implementations;
                            }
                            foreach (var impl in implementations) queue.Enqueue((impl, current.Chain.Add(impl.Key)));
                            if (implementations.Length == 0 && targets == null) Add("unresolved-dispatch", current.Node, current.Chain, edge.Offset, edge.Target);
                        }
                    }
                    foreach (var resolve in current.Node.Effects.Where(e => e.Kind == "di-resolution"))
                    {
                        if (bindings.TryGetValue(resolve.Detail, out var impls))
                            foreach (var impl in impls) foreach (var n in all.Where(n => n.Type == impl && (n.Name == ".ctor" || n.IsVirtual))) queue.Enqueue((n, current.Chain.Add(n.Key)));
                        else Add("unresolved-di", current.Node, current.Chain, resolve.Offset, resolve.Detail);
                    }
                }
                var effects = evidence.Keys.Order().ToImmutableArray();
                var campaign = effects.Contains("campaign-mutation"); var mission = effects.Contains("mission-mutation");
                var ui = effects.Contains("presentation") || effects.Contains("interaction");
                var authority = (campaign || mission) && ui ? AuthorityDomain.Mixed : campaign ? AuthorityDomain.Campaign : mission ? AuthorityDomain.Mission : ui ? AuthorityDomain.LocalPresentation : AuthorityDomain.Unknown;
                // Purity is withheld for unresolved external calls, state writes, I/O and reflection.
                if (authority == AuthorityDomain.Unknown && root.IsVirtual && root.BaseType?.Contains("Model") == true && effects.Length == 0) authority = AuthorityDomain.SharedCalculation;
                operations.Add(new(input.Module, side, root.Identity, root.Key, authority, effects, evidence.Values.ToImmutableArray(),
                    new(CapabilityState.Observed, authority == AuthorityDomain.Unknown ? CapabilityState.Unknown : CapabilityState.Observed,
                        ui ? CapabilityState.Observed : CapabilityState.Unknown, effects.Contains("persistence") ? CapabilityState.Incomplete : CapabilityState.Unknown,
                        CapabilityState.Pending, CapabilityState.Pending)));

                void Add(string effect, MethodNode node, ImmutableArray<string> chain, int offset, string detail)
                { evidence.TryAdd(effect, new(effect, chain, node.Identity, offset, detail)); }
            }
            bool IsSubtype(string candidate, string target)
            {
                var seen = new HashSet<string>(); var pending = new Stack<string>(); pending.Push(candidate);
                while (pending.TryPop(out var t) && seen.Add(t))
                {
                    if (t == target) return true;
                    if (!types.TryGetValue(t, out var node)) continue;
                    if (node.BaseType != null) pending.Push(node.BaseType);
                    foreach (var iface in node.Interfaces) pending.Push(iface);
                }
                return false;
            }
            bool IsLifecycleType(string candidate)
            {
                var seen = new HashSet<string>();
                while (seen.Add(candidate) && types.TryGetValue(candidate, out var node) && node.BaseType != null)
                {
                    candidate = node.BaseType;
                    if (candidate is "TaleWorlds.CampaignSystem.CampaignBehaviorBase" or "TaleWorlds.MountAndBlade.MBSubModuleBase"
                        or "TaleWorlds.MountAndBlade.MissionBehavior" or "TaleWorlds.MountAndBlade.MissionLogic") return true;
                }
                return false;
            }
        }
        var fp = fingerprints.Distinct().ToImmutableArray(); var gapArray = gaps.Distinct().ToImmutableArray();
        var plan = CompatibilityPlanner.Build(request, fp, gapArray);
        return new(RulesVersion, plan.Digest, fp, gapArray, operations.ToImmutableArray(), plan) { LocalFiles = localFiles.Distinct().ToImmutableArray() };

        void Load(AssemblyInput input)
        {
            if (loaded.Any(x => x.Input.Path == input.Path && x.Input.Side == input.Side)) return;
            try
            {
                var hash = AnalysisJson.FileHash(input.Path);
                var metadataOnly = input.Module == "$dependency" && IsEngine(Path.GetFileNameWithoutExtension(input.Path));
                var cacheKey = hash + (metadataOnly ? ".header" : "");
                var cache = Path.Combine(cacheDirectory, RulesVersion, cacheKey + ".json");
                AssemblyGraph? graph = null;
                if (graphCache.TryGetValue(cacheKey, out var cached)) graph = cached;
                try { if (graph == null && File.Exists(cache)) graph = JsonSerializer.Deserialize<AssemblyGraph>(File.ReadAllText(cache)); } catch (JsonException) { }
                if (graph?.Hash != hash || graph.Methods == null || graph.References == null || graph.Types == null) graph = null;
                graph ??= GraphReader.Read(input.Path, hash, metadataOnly);
                graphCache[cacheKey] = graph;
                Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                if (!File.Exists(cache)) { var temp = cache + "." + Guid.NewGuid().ToString("N") + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(graph)); File.Move(temp, cache, true); }
                loaded.Add((input, graph));
                var fingerprint = new InputFingerprint(input.Module, Path.GetFileName(input.Path), hash, input.Side);
                fingerprints.Add(fingerprint); localFiles.Add(new(fingerprint, Path.GetFullPath(input.Path)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { gaps.Add(new(input.Module, "unreadable-assembly", $"{input.Side}: {Path.GetFileName(input.Path)} ({ex.GetType().Name}); no compatibility inference")); }
        }
    }
    private static bool IsFramework(string name) => name is "mscorlib" or "netstandard" or "System" or "Microsoft.CSharp" || name.StartsWith("System.", StringComparison.Ordinal);
    private static bool IsEngine(string name) => name.StartsWith("TaleWorlds.", StringComparison.Ordinal) || name.StartsWith("SandBox", StringComparison.Ordinal) || name.StartsWith("StoryMode", StringComparison.Ordinal);
    private static bool IsKnownExternal(string key) => IsFramework(key.Split('|')[0]) && !key.Contains("System.Reflection") && !key.Contains("System.IO.");
}
