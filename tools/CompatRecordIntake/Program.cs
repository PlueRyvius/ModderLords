using System.Globalization;
using System.Text;
using ModderLords.Core.Compat;

// The command-line end of ModderLords.Core's CompatIntake. See docs/COMPAT-RECORDS.md.
//
//   check <compat-db.json>
//       Holds every record of a database file to the submission rules. Exit 1 when any record fails.
//
//   intake --body <file> --db <compat-db.json> --out <dir> --issue <number> [--previous <file>] [--now <utc>]
//       Reads a "Compat record" issue body, validates the record in it and merges it into the database file in place.
//       Writes what the workflow needs into <dir>, so the workflow never builds text out of the issue itself:
//         outcome.txt          ok | unchanged | invalid
//         comment.md           the comment for the issue
//         pr-title.txt         (ok only) the pull request title
//         pr-body.md           (ok only) the pull request body
//         commit-message.txt   (ok only) the commit message
//       A record that fails validation is an expected outcome, not an error: exit 0 with outcome "invalid".
//       --previous is the pull request branch's copy of the database, when one exists, so a re-run of an unchanged
//       submission produces the same bytes. --now fixes the timestamp for a reproducible local run.

const string Marker = "<!-- compat-record-intake -->";
var utf8 = new UTF8Encoding(false);

try
{
    return args.Length > 0 && args[0] == "check" && args.Length == 2 ? Check(args[1])
        : args.Length > 0 && args[0] == "intake" ? Intake(Options(args.Skip(1).ToArray()))
        : Usage();
}
catch (Exception ex)
{
    Console.Error.WriteLine("compat-record intake failed: " + ex.Message);
    return 1;
}

int Usage()
{
    Console.Error.WriteLine("usage: CompatRecordIntake check <compat-db.json>");
    Console.Error.WriteLine("       CompatRecordIntake intake --body <file> --db <compat-db.json> --out <dir> --issue <number> [--previous <file>] [--now <utc>]");
    return 64;
}

Dictionary<string, string> Options(string[] rest)
{
    var known = new[] { "--body", "--db", "--out", "--issue", "--previous", "--now" };
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < rest.Length; i += 2)
    {
        if (!known.Contains(rest[i]) || i + 1 >= rest.Length) throw new ArgumentException($"unexpected argument '{rest[i]}'");
        options[rest[i]] = rest[i + 1];
    }
    foreach (var required in new[] { "--body", "--db", "--out", "--issue" })
        if (!options.ContainsKey(required)) throw new ArgumentException($"missing {required}");
    return options;
}

int Check(string dbPath)
{
    var problems = CompatIntake.ValidateDatabase(File.ReadAllText(dbPath));
    if (problems.Count == 0)
    {
        Console.WriteLine($"OK: {dbPath} passes the compat record checks.");
        return 0;
    }
    Console.Error.WriteLine($"{dbPath} does not pass the compat record checks:");
    foreach (var p in problems) Console.Error.WriteLine("  - " + p);
    return 1;
}

int Intake(Dictionary<string, string> o)
{
    // The issue number is the only thing the branch name is built from, so it is held to digits here as well.
    if (!int.TryParse(o["--issue"], NumberStyles.None, CultureInfo.InvariantCulture, out var issue) || issue <= 0)
        throw new ArgumentException("--issue must be a positive number");
    var now = o.TryGetValue("--now", out var fixedNow)
        ? DateTime.Parse(fixedNow, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
        : DateTime.UtcNow;
    var outDir = o["--out"];
    Directory.CreateDirectory(outDir);
    foreach (var stale in new[] { "outcome.txt", "comment.md", "pr-title.txt", "pr-body.md", "commit-message.txt" })
        File.Delete(Path.Combine(outDir, stale));

    var body = File.ReadAllText(o["--body"]);
    var problems = new List<string>();
    var form = CompatIntake.ParseIssueBody(body, problems);
    var submission = CompatIntake.ValidateIssue(body);
    if (!submission.IsValid)
    {
        Write(outDir, "comment.md", InvalidComment(submission.Problems));
        return Finish(outDir, "invalid", string.Join(Environment.NewLine, submission.Problems.Select(p => "  - " + p)));
    }

    var record = submission.Record!;
    var dbPath = o["--db"];
    var dbText = File.ReadAllText(dbPath);
    var previousText = o.TryGetValue("--previous", out var previousPath) && File.Exists(previousPath) ? File.ReadAllText(previousPath) : null;
    var merge = CompatIntake.Merge(dbText, record, now, previousText);

    if (merge.Unchanged)
    {
        Write(outDir, "comment.md", $"{Marker}\nThe compatibility database already holds exactly this record for `{record.Id}`, so there is nothing to change. Thank you all the same.\n\nIf you meant to change something, edit the record in this issue (the pencil on the first post) and it will be checked again.\n");
        return Finish(outDir, "unchanged", $"  {record.Id} is already in the database with these values");
    }

    File.WriteAllText(dbPath, merge.Text, utf8);
    var changes = CompatIntake.Describe(merge.Previous, merge.Record);
    var verb = merge.Added ? "Add" : "Update";
    Write(outDir, "pr-title.txt", $"Compat record: {record.Id}");
    Write(outDir, "commit-message.txt", $"Compat record: {record.Id}\n\n{verb} the compatibility record for {record.Id}, submitted in issue #{issue}.\n");
    Write(outDir, "pr-body.md", PullRequestBody(issue, merge, changes, CompatIntake.SafeVersion(form.ModVersion), CompatIntake.SafeVersion(form.CoopVersion)));
    Write(outDir, "comment.md", $"{Marker}\nThank you. The record for `{record.Id}` passed the automatic checks and is now waiting for the maintainer to review it. Nothing is merged automatically.\n\nIf you edit the record in this issue, the pull request is updated to match.\n");
    return Finish(outDir, "ok", $"  {(merge.Added ? "new record" : "replaces the existing record")} for {record.Id}; {changes.Count} field(s) differ");
}

int Finish(string outDir, string outcome, string detail)
{
    Write(outDir, "outcome.txt", outcome + "\n");
    Console.WriteLine("outcome: " + outcome);
    Console.WriteLine(detail);
    // On a runner, hand the outcome to the next step. The value is one of three fixed words.
    if (Environment.GetEnvironmentVariable("GITHUB_OUTPUT") is { Length: > 0 } githubOutput)
        File.AppendAllText(githubOutput, $"outcome={outcome}\n", utf8);
    return 0;
}

void Write(string dir, string name, string text) => File.WriteAllText(Path.Combine(dir, name), text, utf8);

string InvalidComment(IReadOnlyList<string> problems)
{
    var sb = new StringBuilder();
    sb.Append(Marker).Append('\n');
    sb.Append("Thank you for the submission. The record could not be accepted as it is:\n\n");
    foreach (var p in problems) sb.Append("- ").Append(p).Append('\n');
    sb.Append("\nTo fix it, edit this issue (the pencil on the first post) and it will be checked again. ");
    sb.Append("The easiest way to get a record that passes is the Submit… button on ModderLords' Mods tab, which fills the form in for you.\n");
    sb.Append("\nIf an earlier version of this issue already opened a pull request, that pull request still holds the earlier record.\n");
    return sb.ToString();
}

string PullRequestBody(int issue, CompatIntake.MergeResult merge, IReadOnlyList<CompatIntake.FieldChange> changes, string? modVersion, string? coopVersion)
{
    var record = merge.Record;
    var sb = new StringBuilder();
    sb.Append($"Closes #{issue}\n\n");
    sb.Append(merge.Added
        ? $"**New** compatibility record for `{record.Id}`, submitted through the issue form in #{issue}.\n"
        : $"**Replaces** the existing compatibility record for `{record.Id}`, submitted through the issue form in #{issue}.\n");
    if (modVersion is not null || coopVersion is not null)
        sb.Append($"\nThe submitter reports testing with mod version `{modVersion ?? "not given"}` and Coop version `{coopVersion ?? "not given"}`.\n");

    sb.Append("\n## Review these first\n\n");
    sb.Append("These three fields change what the launcher does on every host that has this mod. The automatic checks only prove they are well-formed, not that they are right.\n\n");
    sb.Append("### ServerExcludedFolders\n\nTop-level folders of the mod that the dedicated server is not shown.\n\n");
    sb.Append(Fenced(CompatIntake.Display(record.ServerExcludedFolders))).Append('\n');
    sb.Append("### EnsureLines\n\nLines the launcher writes into files inside the mod's own folder before launch.\n\n");
    sb.Append(Fenced(CompatIntake.Display(record.EnsureLines))).Append('\n');
    sb.Append("### DefaultSettings\n\nMod setting values staged as host overrides at launch and carried to every player by settings sync.\n\n");
    sb.Append(Fenced(CompatIntake.Display(record.DefaultSettings))).Append('\n');

    sb.Append("## What changes\n\n");
    if (merge.Added)
    {
        sb.Append("There was no record for this mod. The whole record is new:\n\n");
        sb.Append(Fenced(CompatIntake.Display(record))).Append('\n');
    }
    else if (changes.Count == 0)
    {
        sb.Append("No field differs from the record already in the database; only `UpdatedAt` moves.\n\n");
    }
    else
    {
        sb.Append("Fields that differ from the record already in the database (`UpdatedAt` always moves and is left out):\n\n");
        foreach (var change in changes)
        {
            sb.Append($"### {change.Field}\n\nBefore:\n\n").Append(Fenced(change.Before));
            sb.Append("\nAfter:\n\n").Append(Fenced(change.After)).Append('\n');
        }
    }

    sb.Append("---\n\n");
    sb.Append("Opened by the compat-record workflow. Values above are shown as JSON with anything outside plain ASCII escaped, so nothing in them can hide. ");
    sb.Append("CI does not start by itself on a pull request opened by a workflow: close and reopen this pull request to run it (docs/COMPAT-RECORDS.md).\n");
    return sb.ToString();
}

// A code block whose fence is longer than any run of backticks inside it, so the content cannot end the block early.
string Fenced(string? json)
{
    if (json is null) return "None.\n";
    var longest = 0;
    var run = 0;
    foreach (var c in json)
    {
        run = c == '`' ? run + 1 : 0;
        longest = Math.Max(longest, run);
    }
    var fence = new string('`', Math.Max(3, longest + 1));
    return $"{fence}json\n{json}\n{fence}\n";
}
