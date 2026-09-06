namespace ModderLords.Core.Perf;

/// <summary>
/// Learns what one metric normally does on THIS server, this session, and reports sustained departures from it.
///
/// Deliberately not absolute thresholds: nobody knows what a good tick rate is for a given machine, mod set and
/// player count, so the meter measures the server against its own recent history instead of against a number
/// somebody guessed. Median and MAD rather than mean and standard deviation, because a save write or a player
/// joining is a spike, and a spike must not widen the band until real problems fit inside it.
/// </summary>
public sealed class BaselineTracker
{
    /// <summary>Samples kept: an hour at the 10-second report cadence. Bounded by construction, like every other buffer here.</summary>
    public const int Capacity = 360;
    /// <summary>Samples ignored after the server starts serving, so world load and the first connections do not become "normal".</summary>
    public const int WarmUpSamples = 6;
    /// <summary>Half-width of the normal range, in scaled MADs.</summary>
    public const double BandK = 3.0;
    /// <summary>Consecutive out-of-band samples, same direction, before we call it sustained rather than a blip.</summary>
    public const int SustainedSamples = 3;
    /// <summary>Turns a MAD into a standard-deviation-like number for a normal distribution.</summary>
    private const double MadToSigma = 1.4826;

    private readonly double[] _ring = new double[Capacity];
    private int _next, _count, _seen;
    private int _outsideRun;
    private int _outsideDirection;   // -1 below the band, +1 above, 0 none

    public string Name { get; }
    /// <summary>True when lower values are the bad ones (tick rate); false when higher is worse (CPU, memory, frame time).</summary>
    public bool LowIsBad { get; }
    public double? Latest { get; private set; }
    public int SampleCount => _count;
    public bool WarmedUp => _seen > WarmUpSamples;

    public BaselineTracker(string name, bool lowIsBad)
    {
        Name = name;
        LowIsBad = lowIsBad;
    }

    public void Add(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return;
        Latest = value;
        _seen++;
        if (_seen <= WarmUpSamples) return;   // still loading; not evidence of normal

        _ring[_next] = value;
        _next = (_next + 1) % Capacity;
        if (_count < Capacity) _count++;

        var band = Band();
        if (band is null) return;
        var (low, high) = band.Value;
        var direction = value < low ? -1 : value > high ? 1 : 0;
        if (direction == 0 || direction != _outsideDirection) _outsideRun = direction == 0 ? 0 : 1;
        else _outsideRun++;
        _outsideDirection = direction;
    }

    /// <summary>Everything recorded since warm-up, oldest first.</summary>
    public IReadOnlyList<double> Samples()
    {
        var result = new double[_count];
        var start = _count < Capacity ? 0 : _next;
        for (var i = 0; i < _count; i++) result[i] = _ring[(start + i) % Capacity];
        return result;
    }

    public double? Median() => _count == 0 ? null : MedianOf(Samples());

    /// <summary>The normal range: median ± BandK scaled MADs. Null until there is enough to be worth stating.</summary>
    public (double Low, double High)? Band()
    {
        if (_count < WarmUpSamples) return null;
        var values = Samples();
        var median = MedianOf(values);
        var mad = MedianOf(values.Select(v => Math.Abs(v - median)).ToArray());
        // An utterly steady metric has MAD 0, which would make every wobble a departure. Give it a small floor.
        var spread = Math.Max(mad * MadToSigma, Math.Abs(median) * 0.02);
        return (median - BandK * spread, median + BandK * spread);
    }

    /// <summary>True when the metric has sat outside its normal range, the bad way, long enough to mean something.</summary>
    public bool IsDeparted =>
        _outsideRun >= SustainedSamples && _outsideDirection == (LowIsBad ? -1 : 1);

    public sealed record Summary(string Name, int SampleCount, double? Median, double? Mad, double? Min, double? Max);

    public Summary Describe()
    {
        if (_count == 0) return new Summary(Name, 0, null, null, null, null);
        var values = Samples();
        var median = MedianOf(values);
        var mad = MedianOf(values.Select(v => Math.Abs(v - median)).ToArray());
        return new Summary(Name, _count, median, mad, values.Min(), values.Max());
    }

    /// <summary>One sentence, always relative to this session — never "this is bad", because we do not know that.</summary>
    public string Status(Func<double, string> format)
    {
        if (Latest is null) return "No data yet.";
        if (!WarmedUp) return $"Still learning what is normal — {_seen} of {WarmUpSamples + 1} warm-up samples.";
        var band = Band();
        if (band is null) return $"Now {format(Latest.Value)}. Not enough history yet to say what is normal.";

        var (low, high) = band.Value;
        var normal = $"usually {format(Math.Max(low, 0))}–{format(high)}";
        if (IsDeparted)
            return $"{(LowIsBad ? "Below" : "Above")} its normal range ({normal}) for {_outsideRun * 10} seconds — now {format(Latest.Value)}.";
        return $"Now {format(Latest.Value)}, {normal}.";
    }

    private static double MedianOf(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
