# Profile manager and verified profiles

Status: **designed 2026-11-08 with the maintainer; build step 1 (the window, groups, pins, quick switch) built
2026-11-08, verified profiles not yet.** The decisions are below, then the window, the
verified-profiles file, how a profile earns each badge, player submissions, and the build order. Every open question
was settled on 2026-11-08 (at the end).

## Why

The toolbar's profile row (a dropdown plus New, Save, Rename, Delete) is a flat list that grows with every profile:
Belle, Core, Crytech71, EE1100, Harness, TAOM, Winter and the rest sit side by side whatever they are for. New players
start from an empty profile with no way to know which mod sets work. The maintainer wants profiles grouped, and wants
ModderLords to offer profiles that are known to work, with the strength of that claim shown honestly.

## Decisions

| # | Decision | Why |
|---|---|---|
| 1 | **The toolbar keeps one profile button** (the name, a **Coop** badge when the Coop mod is in it) and **Save**. New, Rename and Delete move into the Profiles window. | The row was clutter; organizing happens rarely, switching often. |
| 2 | **The button opens a quick-switch menu first**: pinned profiles, recent ones, then **Manage profiles…** (also a keyboard shortcut). | A dialog for every switch would cost more clicks than today's dropdown. |
| 3 | **The Profiles window**: groups on the left (Verified by ModderLords, then Pinned and your groups), the profiles in a list with Name, Mods and Status columns, the selected profile's details on the right. Right-click a profile for Use, Rename, Duplicate, Pin, Move to group, Export mod list, Delete; drag it onto a group to move it. The search box has focus when it opens and also matches mod names; Enter uses the selected profile. | The app's existing patterns: a list like the Mods tab, context menus as on Windows, a calm window with information in the right pane. |
| 4 | **Groups are one level**, one group per profile, stored in the profile's own file (`"Group"`, `"Pinned"`). Profiles stay where they are on disk. | How people think of profiles; nothing breaks for older versions or scripts. Nesting and tags can come later if one level is not enough. |
| 5 | **The Status column answers "can I use this?"**: **Ready**, **N missing** (mods not installed), **Mods changed** (since last launch, or since the profile was verified). Test results show in the details pane when a profile has them. | It applies to everyone; most people never run the tests. |
| 6 | **Verified profiles come from GitHub** (`src/ModderLords.Core/verified-profiles.json`), fetched like the compat database: a copy ships with each release, refreshed from the default branch with its ETag, "never worse than bundled". | New ones can be added between releases. |
| 7 | **Three levels of verified**, by who vouches: **gold**, the maintainer; **silver**, Coodsz (by PR to this repository); **green**, players who report it works. A profile shows its highest level; the details list every one with its evidence. | The maintainer's choice: say how strong the claim is, not only that there is one. |
| 8 | **Every level is tied to exact versions**: the game, Coop and each mod's version (and, where recorded, its install fingerprint). When your installed mods differ, the profile says so ("verified with MS2 0.9.49, you have 0.9.50") instead of claiming it works. | Anyone can download ModderLords; a claim must stay true for the files it was made on. |
| 9 | **A verified profile can be used directly.** Changing one of its mods asks once to make your own copy. **Get missing mods…** opens the existing Workshop subscribe window for the ones you lack. | No speed bump for someone who just wants to play; the verified profile itself never changes under its badge. |
| 10 | **Verified profiles are for anyone who downloads ModderLords.** They list mods by id, version and Workshop id; ModderLords never ships or downloads a mod's files itself. | The audience the maintainer chose; it also sets how careful the wording and pinning must be. |
| 11 | **Switching is refused with a reason** while the server runs (Host), and asks Save / Discard / Cancel when the current profile has unsaved changes. Delete asks first and moves the file aside (recoverable), never erases it. | Profiles are the user's data; switching under a running server breaks it. |

## The window

The mockup agreed on 2026-11-08 is described by decisions 1–3, 5, 9 and 11. In the app's own dark theme: the window,
chrome and list header backgrounds, the selection blue for the selected row and the main action (**Use this
profile**), the green / amber / red badges for Ready / missing / failed. The badge colours for the three levels:

| Level | Badge | Who |
|---|---|---|
| Gold | gold rosette, "Verified by ModderLords" | the maintainer |
| Silver | silver rosette, "Verified by Coodsz" | Coodsz |
| Green | the theme's success green, "Works for N players" | players' reports |

## The verified-profiles file

`src/ModderLords.Core/verified-profiles.json`, one entry per profile:

```jsonc
{
  "schemaVersion": 1,
  "profiles": [
    {
      "id": "coop-modular-smithing-2",            // stable: badges and reports refer to it
      "name": "Coop + Modular Smithing 2",
      "description": "Vanilla campaign in Coop with Modular Smithing 2.",
      "game": "v1.4.8",
      "mods": [
        { "id": "CoopNightly", "version": "v0.1.7", "workshopId": 3770450698 },
        { "id": "ModularSmithing2", "version": "v0.9.49", "workshopId": 3790263286, "fingerprint": "data #… code #…" }
      ],
      "loadOrder": [ "…" ],                       // as the profile file has it
      "verifications": [
        { "level": "gold", "by": "PlueRyvius", "date": "2026-11-08", "tests": "core, smithing, parity: single player and Coop", "runs": [ "20261108-…" ], "notes": "" },
        { "level": "silver", "by": "Coodsz", "date": "2026-11-09", "notes": "Played 2 hours hosted." },
        { "level": "green", "reports": 3, "lastReport": "2026-11-12" }
      ]
    }
  ]
}
```

- A verification counts only for the versions the entry lists; a different installed version shows **Mods changed**
  with the difference.
- Using a verified profile writes a normal profile file (read-only flag set) on first use, so launching works exactly as
  today.

## How a profile earns each level

- **Gold (the maintainer):** the maintainer runs the in-game tests on it (`docs/INGAME-TEST-HARNESS.md`), core plus the
  modules that apply, in single player and Coop, then adds or updates the entry in a PR with the run ids, and merges
  it. Nobody else can merge to `main`, so the badge needs no further signature.
- **Silver (Coodsz):** Coodsz adds a `silver` verification to the profile's entry in a PR to this repository (a
  branch of their own or a fork), with the date and what was checked; the maintainer merges it. Decided 2026-11-08.
- **Green (players):** a player for whom a verified profile works sends a report from the Profiles window
  (**Report: it works**), through the same intake as compat records (`docs/COMPAT-RECORDS.md`: the intake worker or
  the issue form, a workflow that checks it, a PR the maintainer merges). The report carries the profile id, the game,
  Coop and mod versions and fingerprints, and what the launcher saw (a hosted session on the map, or a passing test
  run). The workflow counts it only when the versions match the entry. **One accepted report makes a profile green**
  (decided 2026-11-08); the details show how many players have reported it and when the last one did.

## Players submitting a profile

Decided 2026-11-08: players can submit a whole profile, not only report that ours work.

- **Submit as verified profile…** in a profile's right-click menu sends it through the same intake as reports: the
  profile's name and description (the player writes them), every mod with its id, version, Workshop id and
  fingerprint, the load order, the game and Coop versions, and what the launcher saw work (a hosted session on the
  map, or a passing test run).
- **The workflow refuses** a profile that others could not reproduce: a mod with no Workshop id (installed by hand or
  local-only), a mod with no version, or a profile the launcher never saw work. It says why, in plain sentences, as
  for compat records.
- **Accepted,** it opens a PR adding the entry to `verified-profiles.json` with one `green` verification from the
  submitter, so it shows green once the maintainer merges it. Gold and silver can follow as for any entry.
- The maintainer's review is the gate: the PR shows every mod, so a profile with something unwanted in it is not
  merged.

## Build order (proposed)

1. Groups and pins in the profile file; the Profiles window with the list, status column, right-click menu, drag to
   group, search; the quick-switch menu. No verified profiles yet. **Built 2026-11-08**: `ProfilesWindow`,
   `ProfilesViewModel`, `ProfileIndex` (recent profiles and group names, `profile-index.json`), and
   `ProfileStore.Delete` moving files to `deleted-profiles\` with a **Recently deleted** group and **Restore**.
   Agreed after a review of the first build (2026-11-08): the quick-switch menu lists the other profiles too while
   few are pinned or recent (the rest in a submenu), so switching never needs the window; groups can be renamed and
   deleted (a deleted group's profiles move to Ungrouped); **Mods changed** shows in plain text and only **missing**
   mods in amber; a profile whose game folder or extra mod folders differ from the open one's is checked by its own
   scan, in the background ("Checking…" until then). Export mod list stays on the Share tab for now.
2. `verified-profiles.json`, bundled and fetched like the compat database; the Verified section; use directly, copy
   on change, Get missing mods; the version comparison (decision 8).
3. Gold entries: the maintainer's first verified profiles, from test runs.
4. Green reports and submitted profiles through the intake, and the workflow that checks and counts them.

## Questions settled with the maintainer

1. ~~Silver~~: decided 2026-11-08, a PR to this repository that the maintainer merges.
2. ~~Green threshold~~: decided 2026-11-08, one accepted report.
3. ~~Can players propose whole profiles~~: decided 2026-11-08, yes (see "Players submitting a profile").
4. ~~Profiles and the app's mode~~: decided 2026-11-08, no, for now. The Coop badge shows what a profile contains; the
   app's mode stays global.
