
using ModderLords.Core.Compat;
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

namespace ModderLords.Coop.Saves;

/// <summary>
/// The pristine engine core only loads a save that already exists; bootstrapping a fresh world from
/// default_new_game.sav is the official console host's job (its "[launcher] save ... not present; a fresh
/// sandbox will be created" line). When we launch the engine directly we have to do that step ourselves.
/// </summary>
public static class SavePreparer
{
    public const string TemplateFileName = SaveHeaderReader.TemplateSaveName + ".sav";

    public sealed record Result(string SavePath, bool CreatedFromTemplate, string? TemplateUsed);

    /// <summary>Returns the template path in the data dir, falling back to the one shipped in the package.</summary>
    public static string? FindTemplate(ServerPaths paths)
    {
        var inData = Path.Combine(paths.SavesDir, TemplateFileName);
        if (File.Exists(inData)) return inData;
        var inPackage = Path.Combine(paths.DedicatedServerRoot, "server-data", "Game Saves", TemplateFileName);
        return File.Exists(inPackage) ? inPackage : null;
    }

    public static string SavePath(ServerPaths paths, string saveName) => Path.Combine(paths.SavesDir, saveName + ".sav");

    public static bool Exists(ServerPaths paths, string saveName) => File.Exists(SavePath(paths, saveName));

    /// <summary>Creates &lt;saveName&gt;.sav from the template when it does not exist yet. Never overwrites.</summary>
    public static Result EnsureExists(ServerPaths paths, string saveName)
    {
        ValidateSaveName(saveName);
        var target = SavePath(paths, saveName);
        if (File.Exists(target)) return new Result(target, false, null);

        var template = FindTemplate(paths) ?? throw new FileNotFoundException(
            $"No {TemplateFileName} found in '{paths.SavesDir}' or the server package; cannot bootstrap a new world.");
        Directory.CreateDirectory(paths.SavesDir);
        var tmp = target + ".modderlords-tmp";
        File.Copy(template, tmp, overwrite: true);
        File.Move(tmp, target);
        return new Result(target, true, template);
    }

    /// <param name="SessionCopied">Coop's session file was beside the save and came across with it.</param>
    /// <param name="SessionPlayers">How many player characters that file lists, or null when it could not be read.</param>
    public sealed record ImportResult(string SavePath, string SourcePath, bool Overwrote, IReadOnlyList<string> Warnings,
        bool SessionCopied = false, int? SessionPlayers = null);

    /// <summary>
    /// Coop keeps what the save itself cannot hold in &lt;name&gt;.json beside &lt;name&gt;.sav: which player owns which
    /// hero, party and clan, and each player's workshops, caravans and crafting. A save loaded without it has every
    /// character still in the world and no player attached to any of them, so everyone is sent to make a new one.
    /// </summary>
    public static string SessionPath(string savePath) => Path.ChangeExtension(savePath, ".json");

    /// <summary>
    /// Copies a save made by the real game into the server's Game Saves.
    ///
    /// The server can only ever load a world that already exists, and the only world it can make for itself is a copy
    /// of the pre-baked vanilla default_new_game.sav — which lists Native;SandBoxCore;Sandbox;Coop and nothing else.
    /// A modded server therefore has no way to obtain a world built with its own modules. The client does: launching
    /// the real Bannerlord with the same load order and starting a campaign produces a save whose header lists every
    /// community module. This carries that file across the gap.
    ///
    /// Unlike <see cref="EnsureExists"/> this is an explicit act, so it <b>does</b> replace an existing save when told
    /// to — but never silently: <paramref name="overwrite"/> has to be asked for, and the existing file is kept as a
    /// .replaced-&lt;stamp&gt; sibling rather than destroyed.
    ///
    /// Coop's session file (<see cref="SessionPath"/>) travels with the save. Reported 2026-10-11 by a host who moved
    /// a world from the in-game Coop host to the dedicated server: only the .sav was copied, so the players' characters
    /// stood in the world unowned until the "Players" list was pasted back by hand.
    /// </summary>
    public static ImportResult ImportFrom(string clientSavePath, ServerPaths paths, string saveName, bool overwrite = false)
    {
        ValidateSaveName(saveName);
        if (!File.Exists(clientSavePath)) throw new FileNotFoundException($"No save at '{clientSavePath}'.", clientSavePath);

        var warnings = new List<string>();
        var header = SaveHeaderReader.TryRead(clientSavePath, out var readError);
        if (header is null) warnings.Add($"the file's header could not be read ({readError}); it may not be a Bannerlord save");
        else
        {
            // A client save legitimately lacks the server-side modules, so their absence is not a problem. The Coop
            // module is: without it the world was not built for coop and the handshake has nothing to agree on.
            if (!header.ModuleIds.Any(id => ClientManifest.CoopClientModuleIds.Contains(id)))
                warnings.Add("the save does not list a Coop module, so it was made by a client that was not running Bannerlord Coop; " +
                    "the world may not be coop-compatible");
            warnings.AddRange(header.ModuleIds.Where(ClientManifest.IsServerOnly)
                .Select(id => $"the save lists the server-only module {id}, which is unexpected for a client save"));
        }

        var target = SavePath(paths, saveName);
        var overwrote = File.Exists(target);
        if (overwrote && !overwrite)
            throw new InvalidOperationException($"Save '{saveName}' already exists at '{target}'. Pass overwrite to replace it.");

        Directory.CreateDirectory(paths.SavesDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        if (overwrote)
        {
            var kept = target + ".replaced-" + stamp;
            File.Move(target, kept);
            warnings.Add("the save that was there has been kept as " + Path.GetFileName(kept));
        }
        // A session file already under this name belongs to the world being replaced, or to one deleted earlier.
        // Left in place it would hand the new world's players the old world's characters.
        var targetSession = SessionPath(target);
        if (File.Exists(targetSession)) File.Move(targetSession, targetSession + ".replaced-" + stamp);

        // Same write-then-move as EnsureExists: a save half-copied over is worse than one that is missing.
        var tmp = target + ".modderlords-tmp";
        File.Copy(clientSavePath, tmp, overwrite: true);
        File.Move(tmp, target);

        int? players = null;
        var sourceSession = SessionPath(clientSavePath);
        var copied = File.Exists(sourceSession);
        if (copied)
        {
            var tmpSession = targetSession + ".modderlords-tmp";
            File.Copy(sourceSession, tmpSession, overwrite: true);
            File.Move(tmpSession, targetSession);
            players = CountPlayers(targetSession);
            if (players is null)
                warnings.Add($"the co-op session file {Path.GetFileName(sourceSession)} was copied but could not be read; the players' characters may not be restored");
        }
        return new ImportResult(target, clientSavePath, overwrote, warnings, copied, players);
    }

    /// <summary>The number of entries in the session file's "Players" list, or null when the file is not readable JSON.</summary>
    private static int? CountPlayers(string sessionPath)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(sessionPath));
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            return doc.RootElement.TryGetProperty("Players", out var list) && list.ValueKind == System.Text.Json.JsonValueKind.Array
                ? list.GetArrayLength() : 0;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The saves the real game has, newest first, for an import to choose from.</summary>
    public static IReadOnlyList<SaveHeader> ClientSaves() => SaveHeaderReader.ReadAll(ServerPaths.ClientSavesDir()).ToList();

    public static void ValidateSaveName(string saveName)
    {
        if (string.IsNullOrWhiteSpace(saveName)) throw new ArgumentException("Save name is empty.");
        if (saveName.EndsWith(".sav", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Save name must not include the .sav extension.");
        if (saveName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("Save name contains characters that are not allowed in file names.");
        if (Path.GetFileName(saveName) != saveName) throw new ArgumentException("Save name must be a plain name, not a path.");
    }
}
