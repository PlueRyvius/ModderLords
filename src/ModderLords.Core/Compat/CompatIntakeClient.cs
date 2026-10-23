using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModderLords.Core.Compat;

/// <summary>What Submit… sends: the issue form's fields, nothing more. See <see cref="CompatSubmission"/> for what may go in them.</summary>
public sealed record CompatSubmissionFields(
    [property: JsonPropertyName("mod")] string Mod,
    [property: JsonPropertyName("modVersion")] string? ModVersion,
    [property: JsonPropertyName("coopVersion")] string? CoopVersion,
    [property: JsonPropertyName("record")] string Record,
    [property: JsonPropertyName("notes")] string? Notes);

/// <summary>How a direct send went. <see cref="IssueUrl"/> is set when the issue exists; otherwise <see cref="Message"/> says why not.</summary>
public sealed record CompatIntakeResult(bool Sent, string? IssueUrl, string Message);

/// <summary>
/// Sends a compat record to ModderLords' intake worker (<c>worker/compat-intake</c>), which opens the same "Compat
/// record" issue the GitHub form would, from a bot account. The player needs no GitHub account and nothing goes through
/// the clipboard. When no intake is configured, or it fails, Submit… opens the prefilled GitHub form as before.
/// </summary>
public static class CompatIntakeClient
{
    /// <summary>The deployed worker's submit address. Empty until one is deployed: Submit… then always uses the form.</summary>
    public const string DefaultEndpoint = "";

    /// <summary>Overrides <see cref="DefaultEndpoint"/>, for testing against a worker run locally or a staging one.</summary>
    public const string EndpointVariable = "MODDERLORDS_COMPAT_INTAKE";

    /// <summary>The whole exchange; Submit… waits on it, so it is kept short and the form is the fallback.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>The intake to send to, or null when there is none. Only https, except to this PC (a local worker).</summary>
    public static Uri? Endpoint => Parse(Environment.GetEnvironmentVariable(EndpointVariable) is { Length: > 0 } v ? v : DefaultEndpoint);

    public static Uri? Parse(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            ? uri : null;

    public static async Task<CompatIntakeResult> SendAsync(HttpClient http, Uri endpoint, CompatSubmissionFields fields, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await http.PostAsJsonAsync(endpoint, fields, timeout.Token);
            var reply = await ReadReply(response, timeout.Token);
            if (response.StatusCode == HttpStatusCode.Created && reply?.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var issue)
                && issue.Scheme == Uri.UriSchemeHttps && issue.Host == "github.com")
                return new CompatIntakeResult(true, issue.ToString(), "sent");
            var why = response.StatusCode switch
            {
                HttpStatusCode.UnprocessableEntity => "the intake refused it" + (reply?.Problems is { Length: > 0 } p ? ": " + string.Join(" ", p.Take(3)) : ""),
                HttpStatusCode.TooManyRequests => reply?.Error ?? "too many submissions for now",
                _ => $"the intake answered {(int)response.StatusCode}",
            };
            return new CompatIntakeResult(false, null, why);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new CompatIntakeResult(false, null, "the intake did not answer in time"); }
        catch (HttpRequestException ex) { return new CompatIntakeResult(false, null, "could not reach the intake (" + ex.Message + ")"); }
    }

    private sealed record Reply(
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("problems")] string[]? Problems);

    private static async Task<Reply?> ReadReply(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadFromJsonAsync<Reply>(ct); }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }
}
