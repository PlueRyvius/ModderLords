
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

    public static void ValidateSaveName(string saveName)
    {
        if (string.IsNullOrWhiteSpace(saveName)) throw new ArgumentException("Save name is empty.");
        if (saveName.EndsWith(".sav", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Save name must not include the .sav extension.");
        if (saveName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("Save name contains characters that are not allowed in file names.");
        if (Path.GetFileName(saveName) != saveName) throw new ArgumentException("Save name must be a plain name, not a path.");
    }
}
