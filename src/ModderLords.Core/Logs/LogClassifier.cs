using System.Text.RegularExpressions;

namespace ModderLords.Core.Logs;

public enum LogCategory
{
    Engine,        // routine engine diagnostics (xml loading, packages, languages)
    ModuleLoad,    // module/submodule discovery and dll loading
    Server,        // [DedicatedServer] / launcher status lines
    Coop,          // Coop mod serilog lines
    Warning,
    Error,
    Milestone,     // lines worth pinning: Command Args, SERVING, exit
    Tool,          // our own messages
    Probe,         // engine AssemblyLoader eager-probe misses ("Cannot load: X.dll"); resolved properly right after, not real errors
    Command,       // a console command the host typed, echoed back
    CommandReply,  // engine output attributed to the command just sent (see MainViewModel: a time window, not a protocol)
    Perf,          // our own periodic performance samples; data for the Performance tab, hidden from the console by default
}

public sealed record ClassifiedLine(string Text, LogCategory Category);

/// <summary>Cheap, order-dependent line classifier for the engine's console output.</summary>
public static partial class LogClassifier
{
    public static ClassifiedLine Classify(string line)
    {
        var t = line.Trim();
        if (t.Length == 0) return new(line, LogCategory.Engine);

        if (t.Contains("Command Args:", StringComparison.Ordinal) || t.Contains("SERVING", StringComparison.Ordinal))
            return new(line, LogCategory.Milestone);

        // Before the general [ModderLords] branch below, which would otherwise swallow these into Tool.
        if (t.StartsWith(PerfLineParser.Prefix, StringComparison.Ordinal)) return new(line, LogCategory.Perf);

        if (t.StartsWith("[ModderLords]", StringComparison.Ordinal) || t.StartsWith("[ModderLords.Hook]", StringComparison.Ordinal)
            || t.StartsWith("[ModderLords.Compat]", StringComparison.Ordinal)) return new(line, LogCategory.Tool);

        // The engine's AssemblyLoader eagerly tries every referenced assembly by bare file name in its own bin and
        // logs a "Messagebox [ERROR] ... Cannot load:" for each miss, then resolves it properly later. Noise, not an error.
        if (t.Contains("Cannot load:", StringComparison.Ordinal) || (t.StartsWith("ERROR:", StringComparison.Ordinal) && t.Contains("Could not load file or assembly", StringComparison.Ordinal)))
            return new(line, LogCategory.Probe);

        // A crash the mod swallowed. TAOM's handler answers its own inquiry and lets the tick return, so the run keeps
        // going with the failure invisible; without this the line's category depends on which exception type it names.
        if (t.Contains("inquiry auto-accepted", StringComparison.Ordinal))
            return new(line, LogCategory.Error);

        // Asset/animation failures the engine raises as a blocking modal, and the Coop save-vs-modules diff. All three
        // read as routine engine chatter otherwise, and all three are what a stalled load has to be diagnosed from.
        if (RglWarningRx().IsMatch(t) || t.Contains("Messagebox [Always Ignore?]", StringComparison.Ordinal)
            || t.Contains("module mismatch", StringComparison.OrdinalIgnoreCase))
            return new(line, LogCategory.Warning);

        if (ExceptionRx().IsMatch(t) || t.Contains("could not be loaded correctly", StringComparison.Ordinal)
            || t.StartsWith("Cannot find:", StringComparison.Ordinal) || t.Contains("[ERR]", StringComparison.Ordinal)
            || t.Contains("[FTL]", StringComparison.Ordinal) || t.Contains("Fatal", StringComparison.Ordinal)
            || t.Contains("FATAL", StringComparison.Ordinal) || t.Contains("Loader Exceptions", StringComparison.Ordinal))
            return new(line, LogCategory.Error);

        if (t.Contains("Couldn't find .dll", StringComparison.Ordinal) || t.Contains("[WRN]", StringComparison.Ordinal)
            || t.Contains("Warning", StringComparison.Ordinal) || t.Contains("VersionMismatch", StringComparison.Ordinal))
            return new(line, LogCategory.Warning);

        if (t.Contains("LoadWithFullPath", StringComparison.Ordinal) || t.Contains("Loading submodules", StringComparison.Ordinal)
            || t.Contains("SubModule.xml", StringComparison.Ordinal) || t.Contains("Module Initialize", StringComparison.Ordinal)
            || t.Contains("Creating module", StringComparison.Ordinal))
            return new(line, LogCategory.ModuleLoad);

        if (t.Contains("[DedicatedServer", StringComparison.Ordinal) || t.Contains("[launcher]", StringComparison.Ordinal)
            || t.Contains("[ManagedServer]", StringComparison.Ordinal))
            return new(line, LogCategory.Server);

        if (SerilogRx().IsMatch(t))
            return new(line, LogCategory.Coop);

        return new(line, LogCategory.Engine);
    }

    [GeneratedRegex(@"(^|\s)(System(\.\w+)+Exception|\w+Exception:|   at .+\(.*\)|Unhandled exception|NullReferenceException)")]
    private static partial Regex ExceptionRx();

    [GeneratedRegex(@"\[(VRB|DBG|INF|WRN|ERR|FTL)\]")]
    private static partial Regex SerilogRx();

    /// <summary>The engine spells it "RGL WARNING"; the general Warning check below is case-sensitive on "Warning".</summary>
    [GeneratedRegex(@"RGL WARNING", RegexOptions.IgnoreCase)]
    private static partial Regex RglWarningRx();
}
