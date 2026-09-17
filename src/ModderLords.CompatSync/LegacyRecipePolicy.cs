using System;
using System.Collections.Generic;

namespace ModderLords.CompatSync;

/// <summary>Validate the complete legacy message before installing any gate, including messages from older builds.</summary>
public static class LegacyRecipePolicy
{
    public static bool Accept(string json, out string reason)
    {
        reason = "Recipe refused: only schema-v1 legacy gates are accepted; generated transformations require validated operation contracts.";
        if (json == null || json.Length > 1024 * 1024) return false;
        try
        {
            var root = MiniJson.ParseObject(json);
            if (root.TryGetValue("SchemaVersion", out var schema) && !(schema is long version && version == 1)) return false;
            if (!root.TryGetValue("Mods", out var value) || value is not List<object?> mods) return false;
            foreach (var item in mods)
            {
                if (item is not Dictionary<string, object?> mod) return false;
                foreach (var key in new[] { "Handlers", "Unpatch", "PlayerComparisons", "Relays" })
                    if (mod.TryGetValue(key, out var actions) && actions != null && !(actions is List<object?> list && list.Count == 0)) return false;
            }
            reason = ""; return true;
        }
        catch (Exception) { return false; }
    }
}
