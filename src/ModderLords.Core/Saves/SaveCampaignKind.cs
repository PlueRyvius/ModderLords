using System.IO.Compression;
using System.Text;

namespace ModderLords.Core.Saves;

/// <summary>
/// Tells a Story Mode campaign ("Campaign" in the game's menu) from a Sandbox one.
///
/// The dedicated server has no StoryMode module, so a story campaign cannot be hosted: the engine forces the load,
/// cannot resolve what StoryMode registered, and dies with a native access violation right after the save is read
/// (support bundle 20261013-043452-64894e97, three launches, three mod sets, same crash). The save's metadata does
/// not say which kind it is — every save made by a client with StoryMode enabled lists the module, sandbox worlds
/// included — so the answer has to come from the body.
///
/// The body is a raw Deflate stream after the metadata. It carries the names of the campaign behaviours that were
/// running, and StoryMode adds its own only when the game type is CampaignStoryMode. Finding one of them is the
/// test. Read-only, like everything else here.
/// </summary>
public static class SaveCampaignKind
{
    public const string StoryModeModuleId = "StoryMode";

    /// <summary>Behaviours StoryMode registers for a story campaign and never for a sandbox one.</summary>
    private static readonly byte[][] StoryBehaviours =
    [
        Encoding.ASCII.GetBytes("MainStorylineCampaignBehavior"),
        Encoding.ASCII.GetBytes("FirstPhaseCampaignBehavior"),
        Encoding.ASCII.GetBytes("StoryModeTutorialBoxCampaignBehavior"),
        Encoding.ASCII.GetBytes("LordConversationsStoryModeBehavior"),
    ];

    /// <summary>
    /// True for a story campaign, false for anything else that could be read, null when the body could not be read.
    /// A save that does not list StoryMode is answered from the header alone; only the others are decompressed
    /// (tens of megabytes, a few hundred milliseconds), so call this for the save being hosted, not for a whole list.
    /// </summary>
    public static bool? IsStoryCampaign(SaveHeader header)
    {
        if (!header.ModuleIds.Contains(StoryModeModuleId, StringComparer.OrdinalIgnoreCase)) return false;
        try
        {
            using var fs = File.OpenRead(header.Path);
            Span<byte> lenBytes = stackalloc byte[4];
            if (fs.Read(lenBytes) != 4) return null;
            fs.Position = 4 + BitConverter.ToInt32(lenBytes);
            using var body = new DeflateStream(fs, CompressionMode.Decompress);
            return ContainsAny(body, StoryBehaviours);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Streams <paramref name="source"/> once; the tail of each block is carried over so a needle split
    /// across two reads is still found.</summary>
    internal static bool ContainsAny(Stream source, byte[][] needles)
    {
        var overlap = needles.Max(n => n.Length) - 1;
        var buffer = new byte[overlap + 256 * 1024];
        var carried = 0;
        while (true)
        {
            var n = source.Read(buffer, carried, buffer.Length - carried);
            if (n <= 0) return false;
            var window = buffer.AsSpan(0, carried + n);
            foreach (var needle in needles)
                if (window.IndexOf(needle) >= 0) return true;
            carried = Math.Min(overlap, window.Length);
            window[^carried..].CopyTo(buffer);
        }
    }

    public static string Warning(string saveName) =>
        $"WARNING save '{saveName}' is a Story Mode campaign (started with Campaign, not Sandbox). The server has no " +
        "StoryMode module, so it cannot host this world and is expected to crash while loading it. " +
        "Start a Sandbox campaign in the game with the same mods and host that save instead.";
}
