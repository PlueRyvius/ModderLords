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

namespace ModderLords.Coop.Launch;

/// <summary>Turns engine exit codes into something a server operator can act on.</summary>
public static class ExitCodeExplainer
{
    public static string Explain(int code) => code switch
    {
        0 => "Clean stop.",
        2 => "Load failure or timeout: the campaign/save never reached SERVING. Check the last module or submodule named above the failure.",
        3 => "Fatal error while serving. Look for the last exception in the log.",
        4 => "Coop module verification failed: engine\\Modules\\Coop was modified or replaced. Re-verify the workshop item.",
        // The compat module ends the process itself after generating a world; these say the stop was on
        // purpose, so a successful creation is never read as a crash.
        11 => "World created and saved; the server stopped on purpose.",
        12 => "World creation failed; the server stopped without a usable save. The reason is on the 'worldcreate: fail' line above.",
        -1 => "The engine exited with -1: usually an unhandled startup failure. Scroll up for the reason.",
        unchecked((int)0xC0000005) => "Access violation (0xC0000005) inside native code. Inspect the crash dump and engine log to identify the cause.",
        unchecked((int)0xC000013A) => "Terminated by Ctrl+C / console close.",
        // Deliberately does NOT promise the exception text. The engine often dies before anything can report it -
        // a missing assembly or a SubModuleClassType that is not in the mod's DLLs fails inside module init, and
        // the handler that would have logged it is part of what failed. Saying "the exception text is above" sent
        // people hunting through thousands of lines for something that was never written (2026-09-19).
        unchecked((int)0xE0434352) => ".NET unhandled exception (0xE0434352). The engine frequently logs no exception "
                                      + "text for this - a missing assembly or submodule class fails before anything can report it. "
                                      + "Read the WARNING lines from before the launch; they usually name the cause.",
        int.MinValue => "Exit code unavailable (process handle lost).",
        _ => $"Exit code {code} (0x{code:X8}). Not a documented server code; treat as a crash and read the last log lines.",
    };
}
