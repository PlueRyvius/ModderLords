using ModderLords.Core.Modules;
using ModderLords.Coop.Launch;

namespace ModderLords.Coop.Saves;

/// <summary>
/// Points the server's settlement distance cache at the one a map mod ships, instead of SandBox's vanilla copy.
///
/// Measured on 2026-09-08. A TAOM server ran for 2h15m and never got past 81 seconds of progress: the loading step
/// froze at tick 1619 (LoadVisualsThirdState) and Main_map was re-loaded 159,872 times. TAOM's crash log gives the
/// reason exactly once per iteration:
///
///     System.NullReferenceException
///       at NavigationCacheElement`1.get_StringId()
///       at NavigationCacheElement`1.GetHashCode()
///       at Dictionary`2.TryInsert(...)
///       at NavigationCache`1.Deserialize(String path)
///       at A.G.Load()                                    &lt;- DedicatedServer.Core
///       at Campaign.LoadMapScene()
///
/// The server logs the file it reads, and it is hardcoded to SandBox's:
/// <c>..\..\Modules\SandBox/ModuleData\DistanceCaches\settlements_distance_cache_Default.bin</c> — 2.6 MB, built for
/// the vanilla map. TAOM_Map replaces the campaign map and ships its own rebuilt cache (10.2 MB, 988 settlements,
/// 221 fortifications) which is installed on the server and never read. Deserialising vanilla pairs against TAOM's
/// settlements yields elements whose settlement is null, and hashing one throws.
///
/// There is no seam for this: the path is baked into the official host, so the only way to change which file it reads
/// is to change that file. That runs against this project's rule of not writing inside the DedicatedServer package,
/// which is why it is <b>off by default</b>, takes a keep-forever backup of the original before touching anything,
/// and is undone automatically the moment it is switched off.
/// </summary>
public static class DistanceCacheOverride
{
    public const string CacheFileName = "settlements_distance_cache_Default.bin";

    /// <summary>Relative to a module folder: ModuleData\DistanceCaches\settlements_distance_cache_Default.bin.</summary>
    public static string RelativePath => Path.Combine("ModuleData", "DistanceCaches", CacheFileName);

    /// <summary>The vanilla file the engine actually opens. Folder name, not module id: the engine's path is literal.</summary>
    public static string TargetPath(ServerPaths paths) => Path.Combine(paths.ModulesRoot, "SandBox", RelativePath);

    /// <summary>Kept beside the original and never overwritten, so the vanilla cache is always recoverable.</summary>
    public static string BackupPath(ServerPaths paths) => TargetPath(paths) + ".modderlords-original";

    public sealed record Result(bool Applied, string? ProviderId, IReadOnlyList<string> Messages);

    /// <summary>Selected modules that ship a distance cache of their own, in load order.</summary>
    public static IReadOnlyList<DiscoveredModule> Providers(IEnumerable<DiscoveredModule> modules) =>
        modules.Where(m => File.Exists(Path.Combine(m.FolderPath, RelativePath))).ToList();

    /// <summary>
    /// The crash this class exists for, caught before launch instead of minutes into loading: a selected mod ships its
    /// own settlement distance cache (it replaces the campaign map) while the override is off, so the server is certain
    /// to read SandBox's vanilla cache against that map. Measured 2026-09-14: TAOM world creation died with 0xE0434352
    /// right after the server logged reading SandBox's cache. Null when there is nothing to stop.
    /// </summary>
    public static string? PreflightProblem(IEnumerable<DiscoveredModule> selected, bool enabled)
    {
        if (enabled) return null;
        var providers = Providers(selected.Where(m => !m.IsStock && !m.FolderName.Equals("SandBox", StringComparison.OrdinalIgnoreCase)));
        if (providers.Count == 0) return null;
        var one = providers.Count == 1;
        return $"{string.Join(", ", providers.Select(p => p.Id))} {(one ? "ships its own" : "ship their own")} settlement distance cache, so "
             + $"{(one ? "it replaces" : "they replace")} the campaign map, but this profile has \"Use a map mod's distance cache\" off. "
             + "The server would read SandBox's vanilla cache against that map and crash while loading it. "
             + "Tick \"Use a map mod's distance cache\" on the Server tab (CLI: --mod-distance-cache) and launch again.";
    }

    /// <summary>
    /// Applies or removes the override to match <paramref name="enabled"/>. Safe to call on every launch: it compares
    /// content before copying, so an unchanged setup does no I/O and the file is not touched needlessly.
    /// </summary>
    public static Result Sync(ServerPaths paths, IReadOnlyList<DiscoveredModule> selected, bool enabled)
    {
        var messages = new List<string>();
        var target = TargetPath(paths);
        var backup = BackupPath(paths);

        if (!enabled)
        {
            // Switching it off has to put the vanilla cache back, or a profile that no longer uses the map mod would
            // silently keep running against the map mod's cache.
            if (File.Exists(backup))
            {
                if (TryWithRetry(() => { File.Copy(backup, target, overwrite: true); File.Delete(backup); }, out var err))
                    messages.Add("restored SandBox's original settlement distance cache");
                else
                    // Not a warning. Measured: a previous server process still held the file open, the restore failed,
                    // and the next launch of the KNOWN-GOOD stack ran against the map mod's cache and died with
                    // 0xE0434352. Leaving the wrong file in place is a broken server, so refuse rather than warn.
                    throw new InvalidOperationException(
                        $"SandBox's original settlement distance cache could not be restored ({err}). The server would "
                        + "start with a map mod's cache and crash. A previous server process is usually still holding "
                        + $"the file — close it, or copy '{Path.GetFileName(backup)}' back over "
                        + $"'{Path.GetFileName(target)}' in {Path.GetDirectoryName(target)}.");
            }
            return new Result(false, null, messages);
        }

        if (!File.Exists(target))
        {
            messages.Add($"WARNING distance-cache override is on but {target} does not exist; leaving it alone");
            return new Result(false, null, messages);
        }

        var providers = Providers(selected);
        if (providers.Count == 0)
        {
            messages.Add("distance-cache override is on but no selected mod ships a settlement distance cache; using SandBox's");
            return new Result(false, null, messages);
        }
        // Last in load order wins, on the same "later module overrides earlier" rule the engine uses for data.
        var provider = providers[^1];
        if (providers.Count > 1)
            messages.Add($"distance cache: {providers.Count} mods ship one ({string.Join(", ", providers.Select(p => p.Id))}); using {provider.Id}, the last in load order");

        var source = Path.Combine(provider.FolderPath, RelativePath);
        try
        {
            if (!File.Exists(backup))
            {
                File.Copy(target, backup);
                messages.Add($"kept SandBox's original distance cache as {Path.GetFileName(backup)}");
            }
            if (SameContent(source, target)) return new Result(true, provider.Id, messages);

            var tmp = target + ".modderlords-tmp";
            if (!TryWithRetry(() => { File.Copy(source, tmp, overwrite: true); File.Move(tmp, target, overwrite: true); }, out var copyErr))
                throw new InvalidOperationException($"could not install {provider.Id}'s settlement distance cache ({copyErr}); "
                    + "a previous server process is usually still holding the file.");
            messages.Add($"distance cache: using {provider.Id}'s ({new FileInfo(source).Length / (1024 * 1024)} MB) instead of SandBox's; " +
                "the engine's path is hardcoded, so the file is replaced rather than redirected");
            return new Result(true, provider.Id, messages);
        }
        catch (InvalidOperationException)
        {
            // Deliberate hard failure from above: the file is in an unknown state and starting the server anyway is
            // how the known-good stack got broken. Let it out rather than demoting it to a warning nobody reads.
            throw;
        }
        catch (Exception ex)
        {
            messages.Add("WARNING could not apply the distance-cache override: " + ex.Message);
            return new Result(false, provider.Id, messages);
        }
    }

    /// <summary>
    /// The engine holds this file open and does not always let go the instant the process is told to stop, so a
    /// restore that fails on the first attempt is usually a lock that is about to clear rather than a real failure.
    /// </summary>
    private static bool TryWithRetry(Action action, out string? error)
    {
        error = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { action(); return true; }
            catch (IOException ex) { error = ex.Message; Thread.Sleep(200 * (attempt + 1)); }
            catch (UnauthorizedAccessException ex) { error = ex.Message; Thread.Sleep(200 * (attempt + 1)); }
            catch (Exception ex) { error = ex.Message; return false; }
        }
        return false;
    }

    /// <summary>Length first, then bytes: these files are megabytes, and most launches will not have changed one.</summary>
    private static bool SameContent(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
        using var sa = File.OpenRead(a);
        using var sb = File.OpenRead(b);
        var ba = new byte[81920];
        var bb = new byte[81920];
        int n;
        while ((n = sa.Read(ba, 0, ba.Length)) > 0)
        {
            var m = 0;
            while (m < n) { var k = sb.Read(bb, m, n - m); if (k <= 0) return false; m += k; }
            if (!ba.AsSpan(0, n).SequenceEqual(bb.AsSpan(0, n))) return false;
        }
        return true;
    }
}
