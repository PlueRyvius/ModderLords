using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModderLords.Core.Compat;

/// <summary>
/// Layer 1 recipe: which of a mod's behaviours run on the server only. The launcher writes one recipes.json into
/// the bundled ModderLords.Compat module (the server's copy); the server pushes it to joining clients, and the
/// client's copy of the module stops those behaviours from registering locally. Coop's own object sync carries the
/// server's effects to the clients.
/// </summary>
public sealed class ModRecipe
{
    public string Id { get; set; } = "";
    /// <summary>Full type names of CampaignBehaviorBase subclasses whose RegisterEvents is skipped on clients.</summary>
    public List<string> CampaignBehaviors { get; set; } = new();
    /// <summary>Full type names of MissionLogic/MissionBehavior subclasses that clients do not add to missions.</summary>
    public List<string> MissionBehaviors { get; set; } = new();
    public string? Notes { get; set; }
    /// <summary>Hints for the module's static-settings discovery (full type names or trailing-* globs); null = none.</summary>
    public ModRecipeSettings? Settings { get; set; }
}

public sealed class ModRecipeSettings
{
    public List<string> Include { get; set; } = new();
    public List<string> Exclude { get; set; } = new();
}

public sealed class RecipeSet
{
    public const string FileName = "recipes.json";
    public int SchemaVersion { get; set; } = 1;
    public string GeneratedBy { get; set; } = "";
    public List<ModRecipe> Mods { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static RecipeSet FromJson(string json) => JsonSerializer.Deserialize<RecipeSet>(json, Json) ?? new RecipeSet();

    /// <summary>Builds the recipe for every mod flagged server-authoritative from its assembly scan.</summary>
    public static RecipeSet Build(IEnumerable<(string id, ScanResult scan, IReadOnlyCollection<string> keepClientSide)> serverAuthoritative, string generatedBy,
        IEnumerable<(string id, IReadOnlyList<string> include, IReadOnlyList<string> exclude)>? settingsHints = null)
    {
        var set = new RecipeSet { GeneratedBy = generatedBy };
        foreach (var (id, scan, keep) in serverAuthoritative)
        {
            var campaign = scan.CampaignBehaviors.Where(b => !keep.Contains(b)).ToList();
            var mission = scan.MissionBehaviors.Where(b => !keep.Contains(b)).ToList();
            if (campaign.Count == 0 && mission.Count == 0) continue;
            set.Mods.Add(new ModRecipe { Id = id, CampaignBehaviors = campaign, MissionBehaviors = mission,
                Notes = keep.Count > 0 ? "kept client-side: " + string.Join(", ", keep) : null });
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
