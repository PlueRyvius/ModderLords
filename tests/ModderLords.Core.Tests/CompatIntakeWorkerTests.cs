using System.Net;
using System.Text;
using System.Text.Json;
using ModderLords.Core.Compat;

namespace ModderLords.Core.Tests;

/// <summary>
/// The intake worker (worker/compat-intake) builds the issue the form would. These hold it to the same contract as the
/// form: the labels and limits in its form.js, and a body it built (the golden fixture its own tests compare against)
/// read by the very parser the intake workflow runs.
/// </summary>
public sealed class CompatIntakeWorkerTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ModderLords.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
    }

    [Fact]
    public void A_body_the_worker_built_passes_the_intake()
    {
        var body = File.ReadAllText(RepoFile("worker", "compat-intake", "test", "fixtures", "issue-body.md"));
        var submission = CompatIntake.ValidateIssue(body);
        Assert.True(submission.IsValid, string.Join("; ", submission.Problems));
        Assert.Equal("ImprovedGarrisons", submission.Record!.Id);

        var form = CompatIntake.ParseIssueBody(body, []);
        Assert.Equal("v0.1.6", form.CoopVersion);
        Assert.StartsWith("_Sent from ModderLords' Submit…", form.Notes);
        Assert.Contains(@"\### Module id", form.Notes);
    }

    [Fact]
    public void The_workers_labels_and_limits_match_the_intakes()
    {
        var js = File.ReadAllText(RepoFile("worker", "compat-intake", "src", "form.js"));
        foreach (var label in CompatIntake.FormLabels) Assert.Contains($"\"{label}\"", js);
        Assert.Contains("ISSUE_LABEL = \"compat-record\"", js);
        Assert.Contains("NO_RESPONSE = \"_No response_\"", js);
        Assert.Contains($"submissionBytes: {CompatIntake.MaxSubmissionBytes / 1024} * 1024", js);
        Assert.Contains($"recordsPerIssue: {CompatIntake.MaxRecordsPerIssue},", js);
        Assert.Contains($"idLength: {CompatIntake.MaxIdLength},", js);
        Assert.Contains($"versionLength: {CompatIntake.MaxVersionLength},", js);
        Assert.Contains($"notesLength: {CompatIntake.MaxNotesLength},", js);
    }

    private sealed class Handler(HttpStatusCode status, string reply) : HttpMessageHandler
    {
        public List<string> Sent { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(status) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly Uri Endpoint = new("https://intake.example/submit");
    private static readonly CompatSubmissionFields Fields = new("A", "v1.0.0", null, "{\"Id\":\"A\"}", null);

    [Fact]
    public async Task A_created_issue_comes_back_as_its_link_and_only_the_form_fields_are_sent()
    {
        var handler = new Handler(HttpStatusCode.Created, "{\"number\":7,\"url\":\"https://github.com/PlueRyvius/ModderLords/issues/7\"}");
        var result = await CompatIntakeClient.SendAsync(new HttpClient(handler), Endpoint, Fields);

        Assert.True(result.Sent);
        Assert.Equal("https://github.com/PlueRyvius/ModderLords/issues/7", result.IssueUrl);
        using var sent = JsonDocument.Parse(Assert.Single(handler.Sent));
        Assert.Equal(new[] { "mod", "modVersion", "coopVersion", "record", "notes" }, sent.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, "{\"error\":\"x\",\"problems\":[\"\\\"mod\\\" is missing.\"]}", "refused")]
    [InlineData(HttpStatusCode.TooManyRequests, "{\"error\":\"Too many submissions from here in the last hour.\"}", "Too many")]
    [InlineData(HttpStatusCode.BadGateway, "not json", "502")]
    // A "created" answer that does not point at github.com is not trusted: the app would open whatever it named.
    [InlineData(HttpStatusCode.Created, "{\"url\":\"https://evil.example/x\"}", "201")]
    public async Task Anything_else_is_not_sent_and_says_why(HttpStatusCode status, string reply, string why)
    {
        var result = await CompatIntakeClient.SendAsync(new HttpClient(new Handler(status, reply)), Endpoint, Fields);
        Assert.False(result.Sent);
        Assert.Null(result.IssueUrl);
        Assert.Contains(why, result.Message);
    }

    [Theory]
    [InlineData("https://intake.example/submit", true)]
    [InlineData("http://localhost:8787/submit", true)]
    [InlineData("http://intake.example/submit", false)]
    [InlineData("", false)]
    [InlineData("not a url", false)]
    public void Only_https_or_this_PC(string value, bool accepted) => Assert.Equal(accepted, CompatIntakeClient.Parse(value) is not null);
}
