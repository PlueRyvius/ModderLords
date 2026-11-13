using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace ModderLords.Core.Testing;

/// <summary>What a release zip holds, and what is wrong with it (<see cref="PackageCheck"/>).</summary>
public sealed record PackageReport
{
    public required string Zip { get; init; }
    public required long Bytes { get; init; }
    public required string Sha256 { get; init; }
    /// <summary>The mod folder at the zip's root; null when there is none to read.</summary>
    public string? ModId { get; init; }
    public string? Version { get; init; }
    /// <summary>Files under the mod folder (folders not counted).</summary>
    public int Files { get; init; }
    /// <summary>For each path a test module asks about: the files under it, and the fewest it must hold.</summary>
    public IReadOnlyList<(string Path, int Files, int AtLeast)> Counts { get; init; } = [];
    public IReadOnlyList<string> Problems { get; init; } = [];
    public bool Ok => Problems.Count == 0;

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"package {Path.GetFileName(Zip)}: {(Ok ? "OK" : "NOT OK")}");
        sb.AppendLine($"  {Bytes:N0} bytes, SHA-256 {Sha256}");
        if (ModId is not null) sb.AppendLine($"  {ModId} {Version ?? "(no version)"}, {Files} files");
        foreach (var (path, files, atLeast) in Counts) sb.AppendLine($"  {(path.Length == 0 ? "(all)" : path)}: {files} files (at least {atLeast})");
        foreach (var p in Problems) sb.AppendLine("  problem: " + p);
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Checks a mod's release zip before it is published, then unpacks it for a test run (docs/INGAME-TEST-HARNESS.md,
/// decision 27). Modular Smithing 2's first v0.9.50 zip was a tar archive named .zip, and only its size gave it away;
/// an earlier hand install left out changed data files. So: it must be a real zip, hold the mod's folder (and nothing
/// beside it) with its SubModule.xml naming the id and version, and hold at least the files its test module expects.
/// </summary>
public static class PackageCheck
{
    /// <param name="modId">The mod the zip must hold; null takes the one folder at its root.</param>
    /// <param name="version">The version SubModule.xml must say ("v0.9.50", or "0.9.50"); null for any.</param>
    /// <param name="atLeast">Fewest files under each path of the mod folder ("" for all of it).</param>
    public static PackageReport Inspect(string zip, string? modId, string? version, IReadOnlyDictionary<string, int>? atLeast = null)
    {
        var info = new FileInfo(zip);
        if (!info.Exists) throw new FileNotFoundException("no such file", zip);
        string sha;
        using (var stream = File.OpenRead(zip)) sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        PackageReport Bad(string problem) => new() { Zip = zip, Bytes = info.Length, Sha256 = sha, Problems = [problem] };

        var kind = Kind(zip);
        if (kind is not null) return Bad($"not a zip archive: {kind}");

        ZipArchive archive;
        try { archive = ZipFile.OpenRead(zip); }
        catch (InvalidDataException ex) { return Bad("not a readable zip archive: " + ex.Message); }
        using (archive)
        {
            var problems = new List<string>();
            var files = archive.Entries.Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\')).ToList();
            static string Root(ZipArchiveEntry e) => e.FullName.Replace('\\', '/').Split('/')[0];
            var roots = files.Select(Root).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (modId is null)
            {
                if (roots.Count != 1) return Bad($"the zip should hold one mod folder at its root; it holds {string.Join(", ", roots)}");
                modId = roots[0];
            }
            var others = roots.Where(r => !r.Equals(modId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (others.Count > 0) problems.Add($"beside {modId}/ the zip also holds {string.Join(", ", others.Take(5))} at its root; players unpack all of it into Modules");

            var mod = files.Where(e => Root(e).Equals(modId, StringComparison.OrdinalIgnoreCase)).ToList();
            string Relative(ZipArchiveEntry e) => e.FullName.Replace('\\', '/').Substring(modId.Length + 1);
            var manifest = mod.FirstOrDefault(e => Relative(e).Equals("SubModule.xml", StringComparison.OrdinalIgnoreCase));
            string? foundId = null, foundVersion = null;
            if (manifest is null) problems.Add($"{modId}/SubModule.xml is missing");
            else
            {
                try
                {
                    var doc = new XmlDocument();
                    using (var s = manifest.Open()) doc.Load(s);
                    foundId = (doc.SelectSingleNode("/Module/Id") as XmlElement)?.GetAttribute("value");
                    foundVersion = (doc.SelectSingleNode("/Module/Version") as XmlElement)?.GetAttribute("value");
                }
                catch (XmlException ex) { problems.Add($"{modId}/SubModule.xml is not readable XML: {ex.Message}"); }
                if (foundId is not null && !foundId.Equals(modId, StringComparison.Ordinal))
                    problems.Add($"{modId}/SubModule.xml says Id {foundId}, not {modId}");
                if (foundId is null && problems.Count == 0) problems.Add($"{modId}/SubModule.xml has no Id");
                if (version is not null && !SameVersion(foundVersion, version))
                    problems.Add($"{modId}/SubModule.xml says version {foundVersion ?? "(none)"}, not {version}");
            }

            var counts = new List<(string, int, int)>();
            foreach (var (path, min) in (atLeast ?? new Dictionary<string, int>()).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var prefix = path.Replace('\\', '/').Trim('/');
                var n = prefix.Length == 0 ? mod.Count : mod.Count(e => Relative(e).StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
                counts.Add((prefix, n, min));
                if (n < min) problems.Add($"{(prefix.Length == 0 ? modId + "/" : modId + "/" + prefix + "/")} holds {n} files; a release has at least {min}");
            }
            return new PackageReport
            {
                Zip = zip, Bytes = info.Length, Sha256 = sha, ModId = modId, Version = foundVersion,
                Files = mod.Count, Counts = counts, Problems = problems,
            };
        }
    }

    /// <summary>
    /// Unpacks the mod folder of a checked zip into <paramref name="parent"/>\&lt;ModId&gt;, replacing what was there,
    /// and returns that folder. Never writes into the game's Modules folder: a run pins this copy instead.
    /// </summary>
    public static string Unpack(string zip, string modId, string parent)
    {
        var staging = parent + ".staging";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        ZipFile.ExtractToDirectory(zip, staging);   // refuses entries that would land outside staging
        var unpacked = Path.Combine(staging, modId);
        if (!Directory.Exists(unpacked)) throw new InvalidDataException($"{Path.GetFileName(zip)} has no {modId} folder");
        var target = Path.Combine(parent, modId);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.CreateDirectory(parent);
        Directory.Move(unpacked, target);
        Directory.Delete(staging, true);
        return target;
    }

    /// <summary>The one folder at a zip's root, which names the mod; null when it is not a zip or holds more than one.</summary>
    public static string? ModIdOf(string zip)
    {
        if (Kind(zip) is not null) return null;
        try
        {
            using var archive = ZipFile.OpenRead(zip);
            var roots = archive.Entries.Where(e => !e.FullName.EndsWith('/'))
                .Select(e => e.FullName.Replace('\\', '/').Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return roots.Count == 1 ? roots[0] : null;
        }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>"v0.9.50", "0.9.50" and "v0.9.50.0" are the same version.</summary>
    public static bool SameVersion(string? a, string? b)
    {
        static string Norm(string? v) => System.Text.RegularExpressions.Regex.Replace((v ?? "").Trim().TrimStart('v', 'V'), @"(\.0)+$", "");
        return a is not null && b is not null && Norm(a).Equals(Norm(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What the file is when it is not a zip ("a tar archive", "a gzip file", ...); null for a zip.</summary>
    private static string? Kind(string path)
    {
        var head = new byte[262];
        int n;
        using (var s = File.OpenRead(path)) n = s.Read(head, 0, head.Length);
        if (n >= 4 && head[0] == 'P' && head[1] == 'K' && (head[2] == 3 || head[2] == 5 || head[2] == 7)) return null;
        if (n >= 262 && Encoding.ASCII.GetString(head, 257, 5) == "ustar") return "a tar archive (GNU tar ignores -a and writes tar whatever the name says)";
        if (n >= 2 && head[0] == 0x1F && head[1] == 0x8B) return "a gzip file";
        if (n >= 6 && head[0] == '7' && head[1] == 'z') return "a 7-Zip archive";
        if (n >= 4 && Encoding.ASCII.GetString(head, 0, 4) == "Rar!") return "a RAR archive";
        return n == 0 ? "an empty file" : $"unknown content (starts {Convert.ToHexString(head, 0, Math.Min(n, 4))})";
    }
}
