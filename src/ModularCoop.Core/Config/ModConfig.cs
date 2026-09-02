using System.Text;
using System.Text.Json;
using ModularCoop.Core.Launch;

namespace ModularCoop.Core.Config;

/// <summary>
/// CoopData\mod-config.json: gameplay settings owned by the Coop mod (difficulty, fast forward, cheats, ...).
/// Edited value-by-value in place so the mod's own comments survive; seeded from the package's
/// engine\Modules\Coop\mod-config.default.json when it does not exist yet.
/// </summary>
public static class ModConfig
{
    public sealed record Setting(string Path, JsonValueKind Kind, string RawValue)
    {
        public string Display => Kind == JsonValueKind.String ? JsonSerializer.Deserialize<string>(RawValue) ?? "" : RawValue;
    }

    /// <summary>Known enum-valued keys and their choices (from the shipped comments).</summary>
    public static readonly Dictionary<string, string[]> Choices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["difficulty.playerReceivedDamage"] = ["VeryEasy", "Easy", "Realistic"],
        ["difficulty.playerTroopsReceivedDamage"] = ["VeryEasy", "Easy", "Realistic"],
        ["difficulty.combatAIDifficulty"] = ["VeryEasy", "Easy", "Realistic"],
        ["difficulty.recruitmentDifficulty"] = ["VeryEasy", "Easy", "Realistic"],
        ["difficulty.playerMapMovementSpeed"] = ["VeryEasy", "Easy", "Realistic"],
        ["difficulty.stealthAndDisguiseDifficulty"] = ["VeryEasy", "Easy", "Realistic"],
        ["difficulty.persuasionSuccessChance"] = ["VeryEasy", "Easy", "Realistic"],
        ["difficulty.clanMemberDeathChance"] = ["VeryEasy", "Easy", "Realistic"],
        ["difficulty.battleDeath"] = ["VeryEasy", "Easy", "Realistic"],
        ["modOptions.goldFoodInfluenceChangeInBattles"] = ["Disabled", "OneDayMax", "Enabled"],
        ["modOptions.lordDefectionRetries"] = ["Vanilla", "Disabled", "Reduced"],
    };

    public static string? TemplatePath(ServerPaths paths)
    {
        var p = Path.Combine(paths.ModulesRoot, "Coop", "mod-config.default.json");
        if (File.Exists(p)) return p;
        p = Path.Combine(paths.DedicatedServerRoot, "server-data", "mod-config.json");
        return File.Exists(p) ? p : null;
    }

    public static string EnsureExists(ServerPaths paths)
    {
        if (File.Exists(paths.ModConfigPath)) return paths.ModConfigPath;
        var template = TemplatePath(paths) ?? throw new FileNotFoundException("No mod-config template found in the server package.");
        Directory.CreateDirectory(paths.CoopDataDir);
        File.Copy(template, paths.ModConfigPath);
        return paths.ModConfigPath;
    }

    public static IReadOnlyList<Setting> Read(ServerPaths paths)
    {
        var text = File.ReadAllText(EnsureExists(paths));
        return CommentedJson.Leaves(text).Select(l => new Setting(l.Path, l.Kind, l.RawValue)).ToList();
    }

    /// <summary>Applies the given path -> raw JSON value changes in place, with a backup beside the server's config backups.</summary>
    public static int Apply(ServerPaths paths, IEnumerable<(string path, string rawValue)> changes)
    {
        var file = EnsureExists(paths);
        var text = File.ReadAllText(file);
        var n = 0;
        foreach (var (path, raw) in changes)
        {
            var updated = CommentedJson.SetScalar(text, path, raw);
            if (updated != text) { text = updated; n++; }
        }
        if (n == 0) return 0;
        var backupDir = Path.Combine(paths.DataDir, "config-backups", DateTime.Now.ToString("yyyyMMdd-HHmmssfff"));
        Directory.CreateDirectory(backupDir);
        File.Copy(file, Path.Combine(backupDir, "mod-config.json"), overwrite: true);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, file, overwrite: true);
        return n;
    }

    /// <summary>Encodes a user-typed value for the leaf's kind.</summary>
    public static string Encode(JsonValueKind kind, string input)
    {
        input = input.Trim();
        return kind switch
        {
            JsonValueKind.True or JsonValueKind.False => bool.TryParse(input, out var b) ? (b ? "true" : "false") : throw new FormatException("expected true or false"),
            JsonValueKind.Number => double.TryParse(input, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _) ? input : throw new FormatException("expected a number"),
            _ => JsonSerializer.Serialize(input),
        };
    }
}
