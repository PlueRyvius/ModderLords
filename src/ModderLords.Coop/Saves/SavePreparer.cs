
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

    public sealed record ImportResult(string SavePath, string SourcePath, bool Overwrote, IReadOnlyList<string> Warnings);

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
        if (overwrote)
        {
            var kept = target + ".replaced-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Move(target, kept);
            warnings.Add("the save that was there has been kept as " + Path.GetFileName(kept));
        }
        // Same write-then-move as EnsureExists: a save half-copied over is worse than one that is missing.
        var tmp = target + ".modderlords-tmp";
        File.Copy(clientSavePath, tmp, overwrite: true);
        File.Move(tmp, target);
        return new ImportResult(target, clientSavePath, overwrote, warnings);
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
