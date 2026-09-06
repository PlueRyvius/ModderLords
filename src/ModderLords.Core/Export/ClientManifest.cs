using System.Text;
using System.Text.Json;
using System.Xml;
using ModderLords.Core.Modules;

namespace ModderLords.Core.Export;

/// <summary>
/// What players need to match: Coop's ModuleValidator requires every community module (id + version) on the server to be
/// enabled on the client and nothing extra; official non-DLC modules are exempt; DLC must be off.
/// </summary>
public static class ClientManifest
{
    public sealed record Entry(string Id, string Version, string? Source);

    /// <summary>TaleWorlds modules the validator ignores: always present on a client, never part of a server's mod list.</summary>
    public static readonly IReadOnlySet<string> OfficialModuleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Native", "SandBoxCore", "Sandbox", "SandBox", "StoryMode", "CustomBattle", "BirthAndDeath", "Multiplayer", "FastMode" };

    /// <summary>
    /// Both ids the Coop client module ships under, depending on the build the player installed. Matched exactly and
    /// never by prefix: CoopModPatch is an ordinary community mod that the server may or may not be running.
    /// </summary>
    public static readonly IReadOnlySet<string> CoopClientModuleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Coop", "CoopNightly" };

    /// <summary>Server-side-only modules (the launcher's own compat module, DedicatedServer.Windows): exempt from the validator, never installed on a client.</summary>
    public static bool IsServerOnly(string id) => id.StartsWith("DedicatedServer.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What a player has to match. Server-only modules (the launcher's own DedicatedServer.* guards) are left out:
    /// they are exempt from the validator, cannot be installed on a client, and telling a player to enable one sends
    /// them looking for a mod that does not exist.
    /// </summary>
    public static IReadOnlyList<Entry> From(ModuleSelectionResult p) =>
        p.Selections.Where(s => !IsServerOnly(s.Module.Id))
                    .Select(s => new Entry(s.Module.Id, s.Module.Version, WorkshopUrl(s.Module.FolderPath))).ToList();

    public static string ToText(IReadOnlyList<Entry> entries, string coopId, string coopVersion)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Enable exactly these mods in the Bannerlord launcher (same versions), plus Coop. Disable every other community mod and any DLC.");
        sb.AppendLine();
        sb.AppendLine($"  {coopId,-30} {coopVersion}");
        foreach (var e in entries) sb.AppendLine($"  {e.Id,-30} {e.Version}" + (e.Source is null ? "" : $"   {e.Source}"));
        return sb.ToString();
    }

    public static string ToJson(IReadOnlyList<Entry> entries, string coopId, string coopVersion) =>
        JsonSerializer.Serialize(new { coop = new { id = coopId, version = coopVersion }, modules = entries }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>A Steam workshop item id used as the folder name means the mod came from the workshop.</summary>
    public static string? WorkshopUrl(string folderPath)
    {
        var name = Path.GetFileName(folderPath.TrimEnd('\\', '/'));
        return name.Length >= 9 && name.All(char.IsDigit) ? $"https://steamcommunity.com/sharedfiles/filedetails/?id={name}" : null;
    }

    public sealed record ClientCheck(string Id, string? ServerVersion, string? ClientVersion, bool ClientEnabled, string Verdict);

    /// <summary>
    /// Reads the client's LauncherData.xml (Documents\Mount and Blade II Bannerlord\Configs) and reports what the validator
    /// would say. Official modules are ignored; DLC (NavalDLC) enabled is a rejection.
    /// </summary>
    public static IReadOnlyList<ClientCheck> CompareWithLauncherData(IReadOnlyList<Entry> server, string launcherDataPath)
    {
        var result = new List<ClientCheck>();
        var client = new Dictionary<string, (string version, bool selected)>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(launcherDataPath))
        {
            var doc = new XmlDocument();
            doc.Load(launcherDataPath);
            foreach (XmlElement m in doc.SelectNodes("//SingleplayerData/ModDatas/UserModData")!)
            {
                var id = m.SelectSingleNode("Id")?.InnerText ?? "";
                var ver = m.SelectSingleNode("LastKnownVersion")?.InnerText ?? "";
                var sel = string.Equals(m.SelectSingleNode("IsSelected")?.InnerText, "true", StringComparison.OrdinalIgnoreCase);
                if (id.Length > 0) client[id] = (ver, sel);
            }
        }
        foreach (var e in server)
        {
            if (IsServerOnly(e.Id)) continue;   // never installed on a client; reporting it missing is a false alarm
            if (!client.TryGetValue(e.Id, out var c)) result.Add(new ClientCheck(e.Id, e.Version, null, false, "missing on client"));
            else if (!c.selected) result.Add(new ClientCheck(e.Id, e.Version, c.version, false, "installed but not enabled"));
            else if (!Saves.SaveHeaderReader.VersionsEqual(e.Version, c.version)) result.Add(new ClientCheck(e.Id, e.Version, c.version, true, "version differs"));
            else result.Add(new ClientCheck(e.Id, e.Version, c.version, true, "ok"));
        }
        foreach (var kv in client)
        {
            if (!kv.Value.selected || OfficialModuleIds.Contains(kv.Key)) continue;
            if (CoopClientModuleIds.Contains(kv.Key)) continue;   // the Coop module itself; NOT a prefix test, or CoopModPatch would be exempt too
            if (server.Any(s => s.Id.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(new ClientCheck(kv.Key, null, kv.Value.version, true, kv.Key.Equals("NavalDLC", StringComparison.OrdinalIgnoreCase) ? "DLC must be disabled" : "enabled on client but not on server"));
        }
        return result;
    }

    public static string DefaultLauncherDataPath()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(docs, "Mount and Blade II Bannerlord", "Configs", "LauncherData.xml");
    }
}
