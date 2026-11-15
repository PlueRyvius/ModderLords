using System.Text.Json;
using ModderLords.Core.Profiles;

namespace ModderLords.Coop.Launch;

/// <summary>
/// How far a world creation pass has got, for the Host tab's progress bar.
/// <para>
/// The pass reports fixed phases in a fixed order (<see cref="Phases"/>, from the compat module's
/// <c>worldcreate: phase=</c> lines). Each phase is a mark on the bar, placed by how long each step took the last time
/// this profile made a world (TAOM's own timings before any). Between phases the bar fills at that pace but stops short
/// of the next mark, so a slower run waits rather than claiming progress it has not made; when the phase line comes the
/// bar moves to its mark. Successful TAOM creations took 53 s to 139 s (2026-10/11), so the pace is a guide, the marks
/// are the facts.
/// </para>
/// </summary>
public sealed class CreationProgress
{
    /// <summary>The phases a creation pass reports, in order.</summary>
    public static readonly IReadOnlyList<string> Phases = ["armed", "starting", "campaign-created", "map-ready", "saving", "saved"];

    /// <summary>
    /// Seconds from the previous phase (the launch, for the first) to each phase: a TAOM creation on 2026-11-15
    /// (armed 6, starting 41, campaign-created 41, map-ready 37, saving under 1, saved 14).
    /// </summary>
    public static readonly IReadOnlyList<double> DefaultSeconds = [6, 41, 41, 37, 1, 14];

    /// <summary>How far into a step the bar may go before that step's phase line arrives.</summary>
    public const double Cap = 0.95;

    private readonly double[] _expected;
    private readonly double[] _marks;
    private readonly DateTimeOffset?[] _reachedAt = new DateTimeOffset?[6];
    private DateTimeOffset _startedAt;
    private int _last = -1;

    /// <param name="learned">Seconds per step from this profile's last creation; null or unusable uses the defaults.</param>
    public CreationProgress(IReadOnlyList<double>? learned = null)
    {
        var source = learned is { Count: 6 } && learned.All(s => s >= 0 && double.IsFinite(s)) && learned.Sum() > 0 ? learned : DefaultSeconds;
        // A step that once took no time still gets a sliver of the bar, so its mark is not on top of the last one.
        _expected = source.Select(s => Math.Max(s, 0.5)).ToArray();
        var total = _expected.Sum();
        var sum = 0.0;
        _marks = _expected.Select(s => (sum += s) / total).ToArray();
    }

    /// <summary>The bar's mark for each phase, 0..1.</summary>
    public IReadOnlyList<double> Marks => _marks;

    /// <summary>The last phase reached, or null before the first.</summary>
    public string? Phase => _last < 0 ? null : Phases[_last];

    public void Start(DateTimeOffset at)
    {
        _startedAt = at;
        _last = -1;
        Array.Clear(_reachedAt);
    }

    /// <summary>A phase line arrived. Unknown phases, and a phase earlier than one already reached, change nothing.</summary>
    public void Reached(string phase, DateTimeOffset at)
    {
        var index = IndexOf(phase);
        if (index <= _last) return;
        for (var i = _last + 1; i <= index; i++) _reachedAt[i] ??= at;
        _last = index;
    }

    /// <summary>The bar's position at <paramref name="now"/>, 0..1.</summary>
    public double Fraction(DateTimeOffset now)
    {
        if (_last == Phases.Count - 1) return 1;
        var from = _last < 0 ? 0 : _marks[_last];
        var to = _marks[_last + 1];
        var since = _last < 0 ? _startedAt : _reachedAt[_last]!.Value;
        var into = Math.Clamp((now - since).TotalSeconds / _expected[_last + 1], 0, Cap);
        return from + (to - from) * into;
    }

    /// <summary>Seconds each step took, once every phase was reached; null otherwise. What the next run is paced by.</summary>
    public IReadOnlyList<double>? Durations()
    {
        if (_reachedAt.Any(t => t is null)) return null;
        var previous = _startedAt;
        return _reachedAt.Select(t => { var s = (t!.Value - previous).TotalSeconds; previous = t.Value; return Math.Round(s, 1); }).ToList();
    }

    private static int IndexOf(string phase)
    {
        for (var i = 0; i < Phases.Count; i++)
            if (Phases[i].Equals(phase, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}

/// <summary>
/// The step timings of each profile's last successful world creation, in <c>creation-times.json</c> beside the
/// profiles. Only paces the progress bar, so a missing or broken file simply means TAOM's defaults.
/// </summary>
public static class CreationTimes
{
    public static string FilePath => Path.Combine(ProfileStore.RootDir, "creation-times.json");

    public static IReadOnlyList<double>? Load(string profile, string? file = null)
    {
        try
        {
            var path = file ?? FilePath;
            if (!File.Exists(path)) return null;
            var all = JsonSerializer.Deserialize<Dictionary<string, double[]>>(File.ReadAllText(path));
            return all != null && all.TryGetValue(profile, out var seconds) ? seconds : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static void Save(string profile, IReadOnlyList<double> seconds, string? file = null)
    {
        try
        {
            var path = file ?? FilePath;
            Dictionary<string, double[]> all;
            try { all = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, double[]>>(File.ReadAllText(path)) ?? new() : new(); }
            catch (JsonException) { all = new(); }
            all[profile] = seconds.ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
