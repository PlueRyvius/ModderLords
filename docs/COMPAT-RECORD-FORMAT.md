# The compat record format

A compat record is a small JSON object that says what is known about one Bannerlord mod under Bannerlord Coop: does
it work, which version was tested, and what a launcher should do with it when hosting a Coop dedicated server.
ModderLords keeps its whole compatibility database in this format, and its Export… and Import… buttons read and
write the same files. This page is for other tool and mod authors who want to read, write or exchange those files.

You do not need ModderLords' code to use the format. Everything here is plain JSON, and
[`compat-record.schema.json`](compat-record.schema.json) is a JSON Schema (draft 2020-12) you can validate against.

## The file

A file is an object with a schema version and a list of records:

```json
{
  "SchemaVersion": 1,
  "Records": [
    {
      "Id": "ExampleMod",
      "Verdict": "Works",
      "TestedVersions": ["v1.2.0"],
      "TestedCoopVersion": "v0.1.6",
      "DefaultRole": "Run",
      "Notes": "Hosted on a dedicated server with two players; nothing went wrong in two hours of play.",
      "UpdatedAt": "2026-10-14T00:00:00Z"
    }
  ]
}
```

- **Encoding:** UTF-8, no comments, no trailing commas.
- **One record per mod.** `Id` is the key; two records with the same `Id` in one file is an error.
- **Only `Id` is required.** Every other field means "no opinion" when it is left out, `null`, an empty list or an
  empty object. A reader falls back to its own default for it.
- **File names:** anything ending in `.json`. ModderLords names a single export `compat-<Id>.json`.

The same shape is used in three places:

| File | What it is |
| --- | --- |
| [`src/ModderLords.Core/compat-db.json`](../src/ModderLords.Core/compat-db.json) | The curated database. It ships with every release, and installed copies fetch the latest one from `main`. |
| `%LOCALAPPDATA%\ModderLords\compat-db.local.json` | The user's own records, written by Record… and Import…. |
| Any exported file | What Export… writes and Import… reads. |

The live curated file is at
`https://raw.githubusercontent.com/PlueRyvius/ModderLords/main/src/ModderLords.Core/compat-db.json`.

## Fields

Names are case-sensitive when you write them (see [Strict and lenient](#strict-and-lenient-reading)). Enum values
are written as their names, not numbers.

### Identity and the test report

| Field | Type | Meaning |
| --- | --- | --- |
| `Id` | string, required | The mod's module id, exactly as in its `SubModule.xml` (`<Id value="..."/>`). Compared without regard to case. Letters, digits, `.`, `_`, `-`, and single spaces between words; at most 64 characters. |
| `Verdict` | `"Unknown"`, `"Works"`, `"NeedsRecipe"`, `"Broken"` | `Works`: runs under Coop as it is. `NeedsRecipe`: runs, but only with the settings in this record (role, excluded folders, settings and so on). `Broken`: does not work under Coop. `Unknown` is the same as leaving it out. |
| `TestedVersions` | list of strings | The mod versions the verdict was checked against, in the manifest's form (`"v1.2.3"`, `"e1.5.2"`). ModderLords flags a record as untested when the installed version is not in this list. At most 20. |
| `TestedCoopVersion` | string | The Bannerlord Coop version it was tested with, e.g. `"v0.1.6"`. |
| `Notes` | string | Free text for people: what was tested, what goes wrong, what to watch for. At most 8,000 characters. |
| `Url` | string | Where to get the mod or read about it. `http://` or `https://` only. |
| `UpdatedAt` | date-time string | When the record was last changed, in UTC, e.g. `"2026-10-14T01:52:02Z"`. Used to decide which copy wins on import. |

### What the launcher does on the dedicated server

These change what happens on every host that has the mod, so get them right.

| Field | Type | Meaning |
| --- | --- | --- |
| `DefaultRole` | `"Run"`, `"DependencyOnly"`, `"AsShipped"`, `"ClientOptional"` | How the dedicated server treats the mod when it is first added to a profile. See [Roles](#roles). The host can change it afterwards. |
| `KeepSubModules` | list of strings | Submodule classes (full type names, e.g. `"MCM.MCMSubModule"`) that still load when the mod's role is `DependencyOnly`. For a mod whose settings core must run headless while its UI must not. |
| `ServerExcludedFolders` | list of strings | Folders directly inside the mod that the dedicated server is not shown, typically client-only assets: `"Assets"`, `"AssetSources"`, `"RuntimeDataCache"`. Plain folder names only: no paths, no `..`, never `bin`. |
| `ClientExcludedFolders` | list of strings | The other direction: folders only the server uses, which the players' games are not shown. Same rules. |
| `ClientLoadsAfterCoop` | boolean | `true` when the mod patches Coop itself and must load after it on the players' machines, even though its manifest does not say so. |
| `ServerAuthoritative` | boolean | Experimental. `true` runs the mod's campaign and mission behaviours on the server only and skips them on the players' games. Needs ModderLords' compat module on both sides. Leave it out unless you have tested it. |
| `ClientSideBehaviors` | list of strings | With `ServerAuthoritative`, behaviour classes (full type names) that still run on the players' games, such as a UI behaviour. |

### Mod settings and config files

| Field | Type | Meaning |
| --- | --- | --- |
| `DefaultSettings` | object: settings id → setting name → text | Mod settings (MCM or the mod's own settings classes) the mod needs under Coop. The host's game gets them as overrides at launch unless the host already set that value, and settings sync passes them to the players. Every value is text: `"false"`, `"12"`, `"0.5"`. |
| `SettingsTypes` | list of strings | Hints for finding the mod's settings: classes to treat as settings. Full type names, or a prefix ending in `*`. |
| `IgnoreSettingsTypes` | list of strings | Classes never to treat as settings. Same form. |
| `EnsureLines` | list of `{ File, Section, Value }` | Lines the mod's own config file must contain under Coop. See [EnsureLines](#ensurelines). At most 10. |

Lists hold at most 50 entries of at most 200 characters each. No text may contain control characters, line breaks included (even in `Notes`), or invisible direction and
formatting characters.

### Roles

| Role | On the dedicated server |
| --- | --- |
| `Run` | The mod's code loads on the server. Tags that would make the headless engine skip its submodules are removed. The usual choice for gameplay mods. |
| `DependencyOnly` | The mod stays in the load order, so its id and version still match the players' in Coop's join check, but none of its code loads (except `KeepSubModules`). For UI frameworks and other client-side libraries. |
| `AsShipped` | The manifest is used exactly as the mod ships it, and the engine decides. |
| `ClientOptional` | Not on the server at all, and left out of the join check, so each player may run it or not. For mods that only change what one player sees: overlays, UI, camera. |

### EnsureLines

Some mods detect Coop by reading a list of module ids from their own config file, and that list goes stale. An
EnsureLines entry makes sure a line is present before launch:

```json
"EnsureLines": [
  { "File": "coop-modules.txt", "Section": "modules", "Value": "{coopModuleId}" }
]
```

- `File`: a path relative to the mod's folder. It must stay inside it (no drive, no leading separator, no `..`), use
  plain names (letters, digits, space, `.`, `_`, `-`), and be a line-based config file: `.txt`, `.ini`, `.cfg`,
  `.conf`, `.toml` or `.properties`. At most 100 characters.
- `Section`: an INI section name without the brackets. Leave it out to mean anywhere in the file.
- `Value`: the line. `{coopModuleId}` is replaced with the id the installed Coop loads as (it is not always
  `Coop`); no other token exists. At most 300 characters.

It only ever adds a line. A line already there, compared without regard to case, is left alone; nothing is removed or
reordered; the file's line endings and BOM are kept; a missing section is added at the end with the line under it, and a
missing file is created.

## Strict and lenient reading

There are two readers on purpose:

- **The launcher reads leniently,** so an old build can still read a newer file. Property names match without regard
  to case, enums are also accepted as numbers, and unknown properties are ignored. A file whose `SchemaVersion` is
  higher than the launcher knows is refused as a whole.
- **The submission gate is strict:** exact property names, each once, enums by name, no unknown properties, and the
  size limits above. The curated database is held to the same rules in CI.

If you write files, write the strict form; [`compat-record.schema.json`](compat-record.schema.json) checks it. If you
read files, read leniently and ignore what you do not know.

## Importing and merging

Import… merges a file into the user's local records by `Id`:

- a record the user does not have is added;
- a record the user has is replaced only when the incoming `UpdatedAt` is newer (no `UpdatedAt` counts as oldest);
- otherwise the user's record is kept, and they are told.

The user's local records then lie over the curated ones field by field: whatever the local record sets wins, and
whatever it leaves out comes from the curated record. Two exceptions:

- `Verdict`, `TestedVersions`, `TestedCoopVersion` and `Notes` travel together. When a local record has a verdict, all
  four come from it, so a user's verdict never borrows someone else's test notes.
- `DefaultSettings` merges per setting: a local value wins, other settings in the curated record stay.

Because a field left out means "no opinion", a record can say only the one thing you know. A record with just `Id`
and `ServerExcludedFolders` is fine.

If you produce records for other people, set `UpdatedAt` each time you change one, or an older copy a user already has
will be kept instead of yours.

## Sending records to the curated database

Records reach `compat-db.json` through a GitHub issue form, checked by a workflow and merged by the maintainer:

1. Open <https://github.com/PlueRyvius/ModderLords/issues/new?template=compat-record.yml>.
2. Put the mod's id in *Module id* and the record, or a whole file with up to 30 records, in *Compatibility record*.
   For several records, list every id in *Module id*, separated by commas.
3. The workflow comments with any problems; edit the issue and it checks again. Once it passes, it opens a pull
   request, which the maintainer reviews and merges.

The full flow, and what the reviewer looks at, is in [COMPAT-RECORDS.md](COMPAT-RECORDS.md).

## Checking a file

With any JSON Schema validator, for example [`check-jsonschema`](https://pypi.org/project/check-jsonschema/):

```bash
check-jsonschema --schemafile docs/compat-record.schema.json my-records.json
```

Or with the tool CI uses, from a checkout of this repository:

```bash
dotnet run --project tools/CompatRecordIntake -c Release -- check my-records.json
```

The tool is the authority. The schema covers the same rules, except a few that a schema cannot express cleanly: the
control and invisible-character check, the same `Id` twice in one file, Windows reserved device names in
`EnsureLines` paths, and that `Url` parses as an address.

## Versioning

This is `SchemaVersion` 1. New optional fields can be added within version 1, which is why readers should ignore
unknown properties. The version goes up only for a change an old reader would get wrong: a field that changes
meaning or becomes required. A field's meaning, as described here, does not change within a version.
