# Compat records: from a user's machine to the bundled database

`src/ModderLords.Core/compat-db.json` ships with every release. This is how a record somebody worked out on their own
machine gets into it. Nothing in this flow merges by itself: the last step is always the maintainer merging a pull
request by hand.

## The flow

1. **The user submits.** the Submit… button on ModderLords' Mods tab opens the *Compat record* issue form
   (`.github/ISSUE_TEMPLATE/compat-record.yml`) with the fields filled in. The form can also be filled in by hand.
   The template gives the issue the `compat-record` label.
2. **The workflow checks it.** `.github/workflows/compat-record.yml` runs on issues carrying that label when they are
   opened, edited or labelled. It runs `tools/CompatRecordIntake`, which reads the record out of the issue body and
   validates it.
3. **Refused:** the workflow comments on the issue with the reasons, in plain sentences, and stops. The run still
   counts as successful, so a bad submission does not send a failure mail. The submitter edits the issue and it is
   checked again.
4. **Accepted:** the workflow merges the record into `compat-db.json` on the branch `compat-record/issue-<number>`,
   opens one pull request for it (`Closes #<number>`), and comments on the issue with the link. Editing the issue
   updates the same branch and the same pull request.
5. **The maintainer reviews and merges** the pull request. Merging closes the issue.

The workflow keeps one comment per issue and rewrites it on each run.

## The contract with the app

The app builds a prefilled link to the form, and the workflow reads the submitted form back. Both depend on:

| Field id | Label (rendered as a `###` heading in the issue) | Kind |
| --- | --- | --- |
| `mod` | Module id | input, required |
| `mod-version` | Mod version | input |
| `coop-version` | Coop version | input |
| `record` | Compatibility record | textarea, required, `render: json` |
| `notes` | Notes | textarea |

Title prefix `Compat record: `, label `compat-record`. The labels are constants in `CompatIntake`
(`src/ModderLords.Core/Compat/CompatIntake.cs`) and `CompatIntakeTests` fails when the template and the constants
drift apart. `record` holds either one record object or the export-file shape
`{ "SchemaVersion": 1, "Records": [ ... ] }` with exactly one record.

## What is checked

The rules live in `CompatIntake`, next to `CompatDb`, and the record is finally read with `CompatDb.Parse`, so the
gate and the launcher share one schema. The gate is stricter than the launcher on purpose: the launcher ignores what
it does not know so that an old build can read a newer file, while a submission is refused for it.

- exactly one record; `Id` not empty, made of letters, digits, `.`, `_` and `-` (at most 64), and equal to the
  *Module id* field;
- only known properties, spelt with their exact case, each at most once;
- `Verdict` and `DefaultRole` by name only (a number is refused, although the launcher's converter would take one);
- size: the record at most 32 KB, and every text and list has its own limit (see the constants in `CompatIntake`);
- no control characters and no invisible direction or formatting characters in any text;
- `Url` is a plain `http://` or `https://` address;
- `EnsureLines`: `File` is a relative path that stays inside the mod's folder (no drive, no leading separator, no
  `..`, plain names only) and is a line-based config file (`.txt .ini .cfg .conf .toml .properties`); `Value` may
  only use the `{coopModuleId}` token;
- `DefaultSettings`: settings id, then setting name, then the value as text, bounded at every level.

`EnsureLines` and `DefaultSettings` change what the launcher does on every host that has the mod. The checks only prove
they are well-formed. The pull request shows both at the top, under "Review these first": read them before anything
else.

The same rules run in CI against the committed file (`check`, below), so a hand-edited record is held to them as well.
If a legitimate record ever needs something the rules refuse, change the rule in `CompatIntake` in the same pull
request rather than working round it.

## How the record is merged

By id, the way `CompatDb` compares ids (without regard to case): an existing record is replaced whole, otherwise the
record is added. `UpdatedAt` is set to the time of the run, in UTC.

`compat-db.json` is hand-written. It is not in `CompatDb.Serialize`'s order, it leaves out empty lists, and it keeps
characters such as `'` and `<` that `Serialize` writes as `\u` escapes. Writing the whole file through `Serialize`
would turn a one-record change into a diff of the entire file. So the merge splices text: the one record's lines are
exactly what `Serialize` writes for it, and every other byte of the file is left alone. Two consequences:

- a record that came through the form looks different from its hand-written neighbours (every list is written, even
  when empty, and non-ASCII text is escaped). Both forms load identically;
- a new record goes where `Serialize`'s ordering puts it only when the file is already in that order. Today it is
  not, so new records are added at the end. To change that, sort the file once in a pull request of its own.

## Running it locally

```powershell
# What CI runs: hold the committed database to the submission rules.
dotnet run --project tools/CompatRecordIntake -c Release -- check src/ModderLords.Core/compat-db.json

# What the workflow runs. body.md is an issue body as GitHub renders the form: "### <label>" headings, each followed
# by the value, with the record inside a json code fence. It edits the --db file in place, so point it at a copy.
dotnet run --project tools/CompatRecordIntake -c Release -- intake --body body.md --db copy-of-compat-db.json --out out --issue 123
```

`intake` writes `outcome.txt` (`ok`, `unchanged` or `invalid`), `comment.md`, and for `ok` also `pr-title.txt`,
`pr-body.md` and `commit-message.txt` into the `--out` folder. The workflow does nothing with the issue except hand
its body to this tool and publish the files it writes.

## One-time setup (maintainer)

1. **Create the label.** *Issues > Labels > New label*, name `compat-record`. A template cannot create a label; it
   can only apply one that exists. Without the label the form still opens issues, but the workflow never runs.
2. **Let workflows open pull requests.** *Settings > Actions > General > Workflow permissions*: tick **Allow GitHub
   Actions to create and approve pull requests**. Without it the push succeeds and `gh pr create` fails. The workflow
   asks for its own `contents`, `pull-requests` and `issues` permissions, so the default permission setting on the
   same page can stay read-only.

Nothing else: no secrets and no personal token.

## CI on these pull requests

GitHub does not start workflows for events caused by the built-in `GITHUB_TOKEN`. The intake workflow pushes the
branch and opens the pull request with that token, so **CI does not run on these pull requests by itself**, and the
pull request shows no checks.

What has already run by then: the validator, on the submitted record and again on the merged file. What has not:
the build and the test suite, which include the test that loads the committed database.

The simplest way to run CI, with no extra token: **close the pull request and reopen it.** Reopening is your action,
not the token's, and `pull_request: reopened` starts CI. Do it after your last look at the content: if the submitter
edits the issue afterwards, the workflow pushes again and the new commit again has no checks.

If branch protection ever requires CI to pass, this is the step that satisfies it.

## Security notes

The issue title and body are written by anyone. The workflow is built so that this text is only ever data:

- no workflow expression is interpolated into a script; the only issue value a script sees is the issue number,
  through `env:`;
- the tool that reads the body comes from the default branch, never from the issue or a fork;
- the branch name is the issue number; the commit message and pull request title contain the module id only after it
  passed the character whitelist; everything else published is written to a file by the tool and passed as a file;
- text quoted back in a refusal comment is cut short, reduced to plain ASCII and shown as code; values in the pull
  request body are JSON in code blocks with anything outside plain ASCII escaped;
- the free-text *Notes* field is never copied anywhere; it stays in the issue;
- actions are pinned to a commit, and the token has three permissions.

The review is still the real gate. A record that passes every check can be wrong, or can set a `DefaultSettings`
value that spoils a mod for every host.
