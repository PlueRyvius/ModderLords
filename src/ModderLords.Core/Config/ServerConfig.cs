using System.Text;
using System.Text.Json;
using ModderLords.Core.Launch;
using ModderLords.Core.Profiles;

namespace ModderLords.Core.Config;

/// <summary>
/// server-config.json as the pristine server core reads it. The official host documents the file as "created on first
/// run, recreated if deleted", so we render it whole from a commented template instead of editing it in place.
/// Unknown keys are preserved verbatim as a trailing block so third-party additions survive a round trip.
/// </summary>
public static class ServerConfig
{
    public static readonly string[] KnownKeys = ["saveName", "port", "password", "autosaveMinutes", "logFile", "steam", "traceTick", "tracePublish", "traceBandits"];

    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public sealed record Snapshot(string SaveName, ServerSettings Settings, IReadOnlyDictionary<string, JsonElement> Unknown);

    public static Snapshot? Read(string path)
    {
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path), ReadOptions);
        var root = doc.RootElement;
        var s = new ServerSettings();
        var save = "";
        var unknown = new Dictionary<string, JsonElement>();
        foreach (var prop in root.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "saveName": save = prop.Value.GetString() ?? ""; break;
                case "port": if (prop.Value.TryGetInt32(out var port)) s.JoinPort = port; break;
                case "password": s.Password = prop.Value.GetString() ?? ""; break;
                case "autosaveMinutes": if (prop.Value.TryGetInt32(out var a)) s.AutosaveMinutes = a; break;
                case "logFile": s.LogFile = prop.Value.ValueKind == JsonValueKind.True; break;
                case "steam": s.Steam = prop.Value.ValueKind == JsonValueKind.True; break;
                case "traceTick": s.TraceTick = prop.Value.ValueKind == JsonValueKind.True; break;
                case "tracePublish": s.TracePublish = prop.Value.ValueKind == JsonValueKind.True; break;
                case "traceBandits": s.TraceBandits = prop.Value.ValueKind == JsonValueKind.True; break;
                default: unknown[prop.Name] = prop.Value.Clone(); break;
            }
        }
        return new Snapshot(save, s, unknown);
    }

    public static string Render(string saveName, ServerSettings s, IReadOnlyDictionary<string, JsonElement>? unknown = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  // Bannerlord Coop dedicated server configuration, rendered by ModderLords.");
        sb.AppendLine("  // The server re-reads this file on every start. Comments and trailing commas are allowed.");
        sb.AppendLine();
        sb.AppendLine("  // Save to host, from \"Game Saves\" in this folder (no .sav extension).");
        sb.AppendLine($"  \"saveName\": {J(saveName)},");
        sb.AppendLine("  // UDP port Coop clients connect to; forward it on the router for direct joins.");
        sb.AppendLine($"  \"port\": {s.JoinPort},");
        sb.AppendLine("  // Connection password (up to 128 characters). Empty = open server.");
        sb.AppendLine($"  \"password\": {J(s.Password)},");
        sb.AppendLine("  // Minutes between world autosaves; 0 disables autosaving.");
        sb.AppendLine($"  \"autosaveMinutes\": {s.AutosaveMinutes},");
        sb.AppendLine("  // Write everything the server prints to logs\\coop-server-*.log in this folder.");
        sb.AppendLine($"  \"logFile\": {B(s.LogFile)},");
        sb.AppendLine("  // Advertise on Steam when a logged-in Steam client runs on this machine (no port forwarding needed for Steam joins).");
        sb.AppendLine($"  \"steam\": {B(s.Steam)},");
        sb.AppendLine("  // Diagnostics. Only enable when asked to capture logs for a bug report.");
        sb.AppendLine($"  \"traceTick\": {B(s.TraceTick)},");
        sb.AppendLine($"  \"tracePublish\": {B(s.TracePublish)},");
        sb.AppendLine($"  \"traceBandits\": {B(s.TraceBandits)},");
        if (unknown is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("  // Keys this tool does not manage (kept from the previous file; the pristine server ignores unknown keys).");
            foreach (var kv in unknown) sb.AppendLine($"  {J(kv.Key)}: {kv.Value.GetRawText()},");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>Backs up the current file (config-backups\&lt;timestamp&gt;) and writes the rendered one atomically.</summary>
    public static string Write(ServerPaths paths, string saveName, ServerSettings settings, bool keepUnknownKeys = true)
    {
        var path = paths.ServerConfigPath;
        IReadOnlyDictionary<string, JsonElement>? unknown = null;
        if (File.Exists(path))
        {
            try { if (keepUnknownKeys) unknown = Read(path)?.Unknown; } catch { /* corrupt file: replace it, but keep a backup */ }
            var backupDir = Path.Combine(paths.DataDir, "config-backups", DateTime.Now.ToString("yyyyMMdd-HHmmssfff"));
            Directory.CreateDirectory(backupDir);
            File.Copy(path, Path.Combine(backupDir, "server-config.json"), overwrite: true);
        }
        Directory.CreateDirectory(paths.DataDir);
        var text = Render(saveName, settings, unknown);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
        return text;
    }

    private static string J(string s) => JsonSerializer.Serialize(s);
    private static string B(bool b) => b ? "true" : "false";
}
