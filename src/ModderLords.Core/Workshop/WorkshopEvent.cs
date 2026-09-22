using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModderLords.Core.Workshop;

public enum WorkshopEventKind { Subscribed, Downloading, Installed, Failed, SteamUnavailable }

/// <summary>
/// One step of a Workshop subscribe, as the helper process reports it: one JSON object per line on stdout, so the app
/// can show progress as it happens and never has to load the Steam library itself.
/// </summary>
/// <param name="Id">The Workshop item, or 0 for a report about Steam as a whole (<see cref="WorkshopEventKind.SteamUnavailable"/>).</param>
/// <param name="Progress">0..1 while downloading.</param>
/// <param name="Parent">Set when the item was pulled in because another one lists it as required.</param>
public sealed record WorkshopEvent(ulong Id, WorkshopEventKind Kind, string? Detail, double? Progress = null, ulong? Parent = null)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToLine() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads one line of helper output; null for anything that is not an event (stray output, a blank line).</summary>
    public static WorkshopEvent? TryParse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{')) return null;
        try { return JsonSerializer.Deserialize<WorkshopEvent>(line, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The item id in a Workshop link (<c>...filedetails/?id=123</c>, <c>steam://url/CommunityFilePage/123</c>) or a
    /// bare id. Null for anything else, including Nexus and other sites, which nothing here can subscribe to.
    /// </summary>
    public static ulong? ParseWorkshopId(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        var s = source.Trim();
        if (s.All(char.IsDigit)) return ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var bare) && bare > 0 ? bare : null;
        string? digits = null;
        var q = s.IndexOf("id=", StringComparison.OrdinalIgnoreCase);
        if (q >= 0 && s.Contains("steamcommunity.com", StringComparison.OrdinalIgnoreCase))
            digits = new string(s[(q + 3)..].TakeWhile(char.IsDigit).ToArray());
        else if (s.StartsWith("steam://url/CommunityFilePage/", StringComparison.OrdinalIgnoreCase))
            digits = new string(s["steam://url/CommunityFilePage/".Length..].TakeWhile(char.IsDigit).ToArray());
        return ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }

    /// <summary>Opens the item in the Steam client's own browser, where Subscribe is one click.</summary>
    public static string SteamClientUrl(ulong id) => $"steam://url/CommunityFilePage/{id}";
    public static string WebUrl(ulong id) => $"https://steamcommunity.com/sharedfiles/filedetails/?id={id}";
}
