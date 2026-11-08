using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ModderLords.Testing;

/// <summary>
/// What a mod folder holds, as short hashes (docs/INGAME-TEST-HARNESS.md, decisions 23 and 24): its data
/// (<c>ModuleData\</c>), its code for one side (<c>bin\Win64_Shipping_Client\</c> or <c>_Server\</c>) and its
/// <c>SubModule.xml</c>, each by content; everything else by size only, since art folders can be gigabytes. The launcher
/// and the games compute it the same way (this file is linked into both), so a copy the game loaded can be checked
/// against the copy ModderLords picked, and the server against the player's game. Results compare on these bytes, not on
/// the version string, which a test build and a release can share.
/// </summary>
public sealed class ModuleFingerprint
{
    public const string ClientBin = "Win64_Shipping_Client";
    public const string ServerBin = "Win64_Shipping_Server";

    private ModuleFingerprint(string data, string code, string manifest, string other, int files)
    {
        Data = data; Code = code; Manifest = manifest; Other = other; Files = files;
    }

    public string Data { get; }
    public string Code { get; }
    public string Manifest { get; }
    /// <summary>Every other file, by relative path and size.</summary>
    public string Other { get; }
    public int Files { get; }

    public override string ToString() => $"data #{Data} code #{Code} manifest #{Manifest} other #{Other} ({Files} files)";

    /// <summary>
    /// The fingerprint of <paramref name="folder"/>, reading code from <c>bin\<paramref name="bin"/></c>. Folders in
    /// <paramref name="excluded"/> (relative, e.g. a mod's "server only" folders a client launch leaves out) are skipped.
    /// </summary>
    public static ModuleFingerprint Of(string folder, string bin, IEnumerable<string>? excluded = null)
    {
        var root = Path.GetFullPath(folder).TrimEnd('\\', '/');
        var skip = (excluded ?? Enumerable.Empty<string>()).Select(Normalize).Where(e => e.Length > 0).ToList();
        var data = new List<string>();
        var code = new List<string>();
        var other = new List<string>();
        var manifest = "";
        var files = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Normalize(path.Substring(root.Length));
            if (skip.Any(e => relative == e || relative.StartsWith(e + "/", StringComparison.Ordinal))) continue;
            files++;
            // A file listed but not readable (a link with nothing behind it in a launch's private view, measured
            // 2026-11-08 under Coop's bundled server engine) counts as such instead of failing the whole mod.
            try
            {
                if (relative == "submodule.xml") manifest = Hash(File.ReadAllBytes(path));
                else if (relative.StartsWith("moduledata/", StringComparison.Ordinal)) data.Add(relative + ":" + Hash(File.ReadAllBytes(path)));
                else if (relative.StartsWith("bin/" + bin.ToLowerInvariant() + "/", StringComparison.Ordinal)) code.Add(relative + ":" + Hash(File.ReadAllBytes(path)));
                else if (!relative.StartsWith("bin/", StringComparison.Ordinal)) other.Add(relative + ":" + new FileInfo(path).Length);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                (relative.StartsWith("moduledata/", StringComparison.Ordinal) ? data : relative.StartsWith("bin/", StringComparison.Ordinal) ? code : other)
                    .Add(relative + ":unreadable");
            }
        }
        return new ModuleFingerprint(Combine(data), Combine(code), manifest.Length > 0 ? manifest : Combine(new List<string>()), Combine(other), files);
    }

    /// <summary>One short hash over a set of named parts, independent of their order.</summary>
    public static string Combine(IEnumerable<string> parts)
    {
        var sorted = parts.OrderBy(p => p, StringComparer.Ordinal).ToList();
        return Hash(Encoding.UTF8.GetBytes(string.Join("\n", sorted)));
    }

    private static string Normalize(string relative) => relative.Replace('\\', '/').Trim('/').ToLowerInvariant();

    private static string Hash(byte[] bytes)
    {
        using (var sha = SHA256.Create())
        {
            var digest = sha.ComputeHash(bytes);
            var sb = new StringBuilder();
            for (int i = 0; i < 6; i++) sb.Append(digest[i].ToString("x2"));
            return sb.ToString();
        }
    }

    // ---- the expectations the launcher hands a game ------------------------------------------------------------

    /// <summary>The file in the run folder with the copies ModderLords picked for <paramref name="side"/>.</summary>
    public static string ExpectedFile(string side) => "expected-fingerprints-" + side.Replace(':', '-') + ".txt";

    /// <summary>
    /// One line per mod: <c>id|data|code|folder</c>, code empty where it is not compared (the server, whose launch can
    /// leave a mod's DLLs out by its role). Plain text, so the game reads it without a JSON library.
    /// </summary>
    public static string WriteExpected(IEnumerable<(string Id, string Folder, ModuleFingerprint Print, bool CompareCode)> mods) =>
        string.Join("\n", mods.Select(m => $"{m.Id}|{m.Print.Data}|{(m.CompareCode ? m.Print.Code : "")}|{m.Folder}")) + "\n";

    public static IEnumerable<(string Id, string Data, string Code, string Folder)> ReadExpected(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().Split('|');
            if (parts.Length >= 4) yield return (parts[0], parts[1], parts[2], string.Join("|", parts.Skip(3)));
        }
    }
}
