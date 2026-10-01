using System.Text;
using System.Text.Json;

namespace ModderLords.Coop.Admin;

/// <summary>What the server says a character file would bring across (a check) or brought across (an import).</summary>
public sealed record ImportReport(
    string SourceName, int SourceLevel, string ExportedAt, int Skills, int Perks,
    IReadOnlyList<string> MissingModules, IReadOnlyList<string> Notes, IReadOnlyList<string> Applied, HeroDetail Hero)
{
    public static ImportReport From(AdminReply reply)
    {
        var r = reply.Root;
        var source = r.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
        return new ImportReport(
            Str(source, "name"), Int(source, "level"), Str(source, "exportedAt"), Int(source, "skills"), Int(source, "perks"),
            Strings(source, "missingModules"), Strings(r, "notes"), Strings(r, "applied"), HeroDetail.From(reply));
    }

    private static string Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static IReadOnlyList<string> Strings(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];
}

/// <summary>
/// Moves character files between the host's disk and the server. Export comes back in the reply; an import is dropped
/// as a file in the server's user folder (<c>ModderLords\imports\&lt;token&gt;.json</c>), because the console splits its
/// arguments on spaces and a character file is full of them. The server reads it, sanitizes it against the world it
/// is running, and deletes it once applied.
/// </summary>
public sealed class CharacterTransferClient
{
    public const string FileExtension = ".mlchar";
    public const string FileKind = "ModderLords character";
    public const int SupportedSchema = 1;
    public static readonly string[] Parts = ["stats", "gold", "look", "gear", "name"];
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(30);

    private readonly AdminClient _admin;
    private readonly string _importsDir;

    public CharacterTransferClient(AdminClient admin, string serverDataDir)
    {
        _admin = admin;
        _importsDir = Path.Combine(serverDataDir, "ModderLords", "imports");
    }

    /// <summary>The player's character as file text (indented JSON), ready to save.</summary>
    public async Task<string> ExportAsync(string steamId)
    {
        var reply = await _admin.RequestAsync("export", [steamId], ReplyTimeout).ConfigureAwait(false);
        if (!reply.Ok) throw new InvalidOperationException(reply.Error);
        if (!reply.Root.TryGetProperty("character", out var character) || character.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The server's reply had no character in it.");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            character.WriteTo(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Checks the file is a character file this version reads and hands it to the server's folder. Returns the token
    /// that names it there. The server checks it properly; this only stops an obviously wrong file early.
    /// </summary>
    public string Stage(string fileText)
    {
        try
        {
            using var doc = JsonDocument.Parse(fileText);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kind) || kind.GetString() != FileKind)
                throw new InvalidOperationException("This is not a ModderLords character file.");
            if (!root.TryGetProperty("schema", out var schema) || !schema.TryGetInt32(out var s) || s < 1 || s > SupportedSchema)
                throw new InvalidOperationException($"This character file is a format this version of ModderLords does not read (it reads up to {SupportedSchema}). Update ModderLords.");
        }
        catch (JsonException ex) { throw new InvalidOperationException("This is not a character file: " + ex.Message); }

        Directory.CreateDirectory(_importsDir);
        Sweep();
        var token = Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(_importsDir, token + ".json"), fileText, new UTF8Encoding(false));
        return token;
    }

    /// <summary>What importing the staged file onto this player would do, without doing it.</summary>
    public Task<ImportReport> CheckAsync(string steamId, string token) => RunAsync(steamId, token, "check");

    /// <summary>Imports the chosen parts of the staged file onto this player's hero.</summary>
    public Task<ImportReport> ApplyAsync(string steamId, string token, IEnumerable<string> parts)
    {
        var chosen = parts.Distinct().ToList();
        if (chosen.Count == 0) throw new ArgumentException("Pick at least one part to import.");
        if (chosen.FirstOrDefault(p => !Parts.Contains(p)) is { } unknown) throw new ArgumentException($"Unknown part \"{unknown}\".");
        return RunAsync(steamId, token, string.Join(",", chosen));
    }

    /// <summary>Removes a staged file the host decided not to import.</summary>
    public void Discard(string token)
    {
        try { File.Delete(Path.Combine(_importsDir, token + ".json")); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<ImportReport> RunAsync(string steamId, string token, string mode)
    {
        var reply = await _admin.RequestAsync("import", [steamId, token, mode], ReplyTimeout).ConfigureAwait(false);
        if (!reply.Ok) throw new InvalidOperationException(reply.Error);
        return ImportReport.From(reply);
    }

    /// <summary>Staged files left by an import that was never finished (the app closed, the server stopped). A day is plenty.</summary>
    private void Sweep()
    {
        foreach (var f in Directory.EnumerateFiles(_importsDir, "*.json"))
            try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1)) File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
