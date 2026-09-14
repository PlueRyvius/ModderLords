using System.Text.Json;
using System.Text.Json.Serialization;

using ModderLords.Core.Compat;
using ModderLords.Core.Compat.Authority;
using ModderLords.Core.Config;
using ModderLords.Core.Export;
using ModderLords.Core.Launch;
using ModderLords.Core.Logs;
using ModderLords.Core.Modules;
using ModderLords.Core.Overlay;
using ModderLords.Core.Perf;
using ModderLords.Core.Profiles;
using ModderLords.Core.Saves;
using ModderLords.Coop.Compat;
using ModderLords.Coop.Config;
using ModderLords.Coop.Launch;
using ModderLords.Coop.Live;
using ModderLords.Coop.Saves;

namespace ModderLords.Coop.Compat;

/// <summary>
/// Layer 1 recipe: which of a mod's code runs on the server only. The launcher writes one recipes.json into
/// the bundled ModderLords.Compat module (the server's copy); the server pushes it to joining clients, and the
/// client's copy of the module applies the gates locally. Coop's own object sync carries the server's effects to the
/// clients.
/// </summary>
public sealed class ModRecipe
{
    public string Id { get; set; } = "";
    /// <summary>Full type names of CampaignBehaviorBase subclasses whose RegisterEvents is skipped on clients (whole-behaviour gating).</summary>
    public List<string> CampaignBehaviors { get; set; } = new();
    /// <summary>Full type names of MissionLogic/MissionBehavior subclasses that clients do not add to missions.</summary>
    public List<string> MissionBehaviors { get; set; } = new();
    /// <summary>v2, generated from the code: "Type::Method" event handlers whose body is skipped on clients.</summary>
    public List<string> Handlers { get; set; } = new();
    /// <summary>v2, generated from the code: postfixes/finalizers removed from their target on clients, where Coop skips the target.</summary>
    public List<ModRecipePatch> Unpatch { get; set; } = new();
    public string? Notes { get; set; }
    /// <summary>Hints for the module's static-settings discovery (full type names or trailing-* globs); null = none.</summary>
    public ModRecipeSettings? Settings { get; set; }
}

/// <summary>A patch method ("Type::Method") attached to a target ("Type::Method").</summary>
public sealed class ModRecipePatch
{
    public string Target { get; set; } = "";
    public string Patch { get; set; } = "";
}

public sealed class ModRecipeSettings
{
    public List<string> Include { get; set; } = new();
    public List<string> Exclude { get; set; } = new();
}

public sealed class RecipeSet
{
    public const string FileName = "recipes.json";
    public const int CurrentSchema = 2;
    public int SchemaVersion { get; set; } = CurrentSchema;
    public string GeneratedBy { get; set; } = "";
    public List<ModRecipe> Mods { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static RecipeSet FromJson(string json) => JsonSerializer.Deserialize<RecipeSet>(json, Json) ?? new RecipeSet();

    /// <summary>
    /// Builds the recipe for every mod flagged server-authoritative. A mod with an analysable authority report gets
    /// generated per-handler gates and postfix removals; otherwise its whole behaviours are gated from the assembly scan.
    /// <paramref name="keepClientSide"/> names behaviour types that stay client-side either way.
    /// </summary>
    public static RecipeSet Build(IEnumerable<(string id, ScanResult scan, IReadOnlyCollection<string> keepClientSide)> serverAuthoritative, string generatedBy,
        IEnumerable<(string id, IReadOnlyList<string> include, IReadOnlyList<string> exclude)>? settingsHints = null,
        IReadOnlyDictionary<string, AuthorityReport>? authority = null)
    {
        var set = new RecipeSet { GeneratedBy = generatedBy };
        foreach (var (id, scan, keep) in serverAuthoritative)
        {
            ModRecipe recipe;
            if (authority is not null && authority.TryGetValue(id, out var report) && !report.NotAnalysable)
                recipe = FromReport(id, report, keep);
            else
            {
                recipe = new ModRecipe
                {
                    Id = id,
                    CampaignBehaviors = scan.CampaignBehaviors.Where(b => !keep.Contains(b)).ToList(),
                    MissionBehaviors = scan.MissionBehaviors.Where(b => !keep.Contains(b)).ToList(),
                };
                var notes = new List<string>();
                if (authority is not null) notes.Add("code not analysable; whole behaviours gated");
                if (keep.Count > 0) notes.Add("kept client-side: " + string.Join(", ", keep));
                recipe.Notes = notes.Count > 0 ? string.Join("; ", notes) : null;
            }
            if (recipe.CampaignBehaviors.Count + recipe.MissionBehaviors.Count + recipe.Handlers.Count + recipe.Unpatch.Count == 0) continue;
            set.Mods.Add(recipe);
        }
        // Settings hints ride along for any mod that has them, gated or not (the module reads Mods[].Settings only).
        foreach (var (id, include, exclude) in settingsHints ?? [])
        {
            if (include.Count + exclude.Count == 0) continue;
            var recipe = set.Mods.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (recipe is null) { recipe = new ModRecipe { Id = id }; set.Mods.Add(recipe); }
            recipe.Settings = new ModRecipeSettings { Include = include.ToList(), Exclude = exclude.ToList() };
        }
        return set;
    }

    private static ModRecipe FromReport(string id, AuthorityReport report, IReadOnlyCollection<string> keep)
    {
        static string DeclaringType(string method) => method[..Math.Max(0, method.IndexOf("::", StringComparison.Ordinal))];
        // A lambda handler lives in a nested closure type; the exclusion list names the behaviour that owns it.
        bool Kept(string method)
        {
            var type = DeclaringType(method);
            return keep.Any(k => type == k || type.StartsWith(k + "+", StringComparison.Ordinal));
        }

        var handlers = report.Roots
            .Where(r => r.Verdict is AuthorityVerdict.ServerOnly or AuthorityVerdict.NeedsStateSync
                && r.Root.Patch is null && r.Root.Trigger is RootTrigger.Simulation or RootTrigger.Session)
            .Select(r => r.Root.Method).Where(m => !Kept(m)).Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal).ToList();
        var unpatch = report.Roots
            .Where(r => r.Verdict == AuthorityVerdict.LeakingPostfix && r.Root.Patch is not null && !Kept(r.Root.Method))
            .Select(r => new ModRecipePatch { Target = r.Root.Patch!.TargetType + "::" + r.Root.Patch.TargetMethod, Patch = r.Root.Method })
            .ToList();

        var notes = new List<string> { $"generated from code: {handlers.Count} handler(s), {unpatch.Count} postfix(es)" };
        var relay = report.Count(AuthorityVerdict.NeedsRelay);
        var state = report.Count(AuthorityVerdict.NeedsStateSync);
        var review = report.Count(AuthorityVerdict.Review);
        if (relay > 0) notes.Add($"{relay} player action(s) need a server relay");
        if (state > 0) notes.Add($"{state} handler(s) also write mod state players see");
        if (review > 0) notes.Add($"{review} to review");
        if (keep.Count > 0) notes.Add("kept client-side: " + string.Join(", ", keep));
        return new ModRecipe { Id = id, Handlers = handlers, Unpatch = unpatch, Notes = string.Join("; ", notes) };
    }

    /// <summary>Writes recipes.json into the module folder atomically; returns the path. An empty set still writes (clears an old one).</summary>
    public string WriteInto(string moduleFolder)
    {
        var path = Path.Combine(moduleFolder, FileName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson());
        File.Move(tmp, path, overwrite: true);
        return path;
    }
}
