# Releasing ModderLords

Every installed copy checks GitHub for a newer release on startup and offers it to the user. **Publishing a normal
release is therefore outward-facing: the moment it is published, every friend's copy is offered it.** Follow these steps
in order, and get the maintainer's explicit go-ahead before step 7.

## The contract the updater relies on

Break any of these and installed copies silently stop seeing the release (or refuse it):

| Rule | Why |
|---|---|
| Tag is `vX.Y.Z`, exactly three numbers | `UpdateChecker.ParseTag` rejects anything else, including `-rc1` / `-test` suffixes |
| One asset named exactly `ModderLords-X.Y.Z.zip`, same X.Y.Z as the tag | The checker looks for that name and nothing else |
| `ModderLords.exe` at the zip root | `UpdateInstaller.Install` refuses a zip without it |
| A **normal** release, not a draft or prerelease | The check reads `/releases/latest`, which skips both |
| GitHub shows a `sha256:` digest on the asset | The installer refuses a download it cannot verify. GitHub adds it automatically on upload; check it in step 8 |
| The zip comes from `scripts/package-release.ps1` | It enforces the layout, the version match, and the settings-sync adapter |

Code: `src/ModderLords.Core/Updates/`. Tests: `tests/ModderLords.Core.Tests/UpdateTests.cs`.

## Steps

### 1. Start from GitHub, not from a local branch

```powershell
git fetch origin --prune
git checkout main
git merge --ff-only origin/main
```

- **Check merged work by content, not by a PR's MERGED badge.** Stacked PRs have lost work here before. For each commit that should ship:

  ```powershell
  git merge-base --is-ancestor <sha> origin/main
  ```

- **The working tree must be clean** (`git status`).

### 2. Bump the version (in the change's own PR)

Set `<VersionPrefix>` in `Directory.Build.props` to `X.Y.Z`. That one line is the only place the version lives. The title bar, the updater's "running version" and the package script all read it.

**Put the bump in the same PR as the change being released**, not in a separate version-bump PR afterwards. One PR, one merge. If a release gathers several already-merged PRs, bump in the last of them. Agents cannot merge on this repo (`gh pr merge` is blocked); the maintainer merges.

### 3. Tests

```powershell
dotnet test
```

- All green, both `ModderLords.Core.Tests` and `ModderLords.App.Tests`.
- **Close every running ModderLords first.** A running dev build locks `src\ModderLords.App\bin\Debug\...\ModderLords.exe` and the App build fails.
- The App test fixture overrides Steam library discovery (`GamePaths.SteamLibrariesOverride`), so it sees only the
  modules the fixture creates. Asserting exact mod lists there is fine, and a failure means a real failure rather
  than something about this PC. It used to scan the real Workshop; tests written before 1.0.3 may still hedge.

### 4. Package

```powershell
.\scripts\package-release.ps1 -Version X.Y.Z
```

- Produces `artifacts\ModderLords-X.Y.Z\` and `artifacts\ModderLords-X.Y.Z.zip`.
- It refuses when `Directory.Build.props` says a different version.
- It needs the Bannerlord Coop Workshop item installed, to build the settings-sync adapter. Without it the script stops rather than shipping a broken zip.

### 5. Smoke-test the ZIP, not the dev build

1. Unzip `artifacts\ModderLords-X.Y.Z.zip` to a fresh temp folder.
2. Run `ModderLords.exe` from there. Check each of these:
   - The title shows `vX.Y.Z` with **no** "(dev build)".
   - It opens on the Mods tab and your mods are listed.
   - **Folders…** opens and shows "Automatic: …" for the game.
3. If the release touches launching, press **Play** once, or in Host mode launch the server until it reaches SERVING.

A green build proves nothing for UI; this step is not optional.

### 6. Test the update path offline (when the updater or packaging changed)

No GitHub involved. Build a fake feed pointing at the zip from step 4.

1. Get the zip's hash:

   ```powershell
   (Get-FileHash artifacts\ModderLords-X.Y.Z.zip -Algorithm SHA256).Hash.ToLower()
   ```

2. Copy the zip to `%TEMP%\ModderLords-9.9.9.zip`.
3. Write `%TEMP%\fake-release.json`:

   ```json
   {
     "tag_name": "v9.9.9", "draft": false, "prerelease": false,
     "html_url": "https://github.com/PlueRyvius/ModderLords/releases",
     "body": "Offline update test",
     "assets": [ { "name": "ModderLords-9.9.9.zip",
                   "browser_download_url": "C:\\Users\\<you>\\AppData\\Local\\Temp\\ModderLords-9.9.9.zip",
                   "size": 0, "digest": "sha256:<hash from step 1>" } ]
   }
   ```

4. From a PowerShell window, unzip the step-4 zip to another temp folder and start it with the override:

   ```powershell
   $env:MODDERLORDS_UPDATE_FEED = "$env:TEMP\fake-release.json"
   & "<temp folder>\ModderLords.exe"
   ```

5. Check, in this order:
   1. The green banner says 9.9.9 is available.
   2. **Update now** downloads, installs and restarts.
   3. `ModderLords.exe.old` exists and a `.previous` folder was created. The `.old` is gone after the next start.
   4. Profiles in `%LOCALAPPDATA%\ModderLords\profiles` are unchanged.

The "new" copy is the same build, so it still reports X.Y.Z and the banner comes back. That is expected here.

- **Clear the override afterwards:** `Remove-Item Env:MODDERLORDS_UPDATE_FEED`, or close that window.
- **Debug builds never install in place;** they open the release page. Always test a packaged Release build.
- **Don't drive the desktop while the maintainer is using the machine.** Take a screenshot first; a UI automation pass once ran during a game session.

### 7. Publish (ask first)

Write the release notes to a file. Windows PowerShell 5.1 mangles quotes in inline arguments, so always use `--notes-file`.

Notes style, following past releases: a `## ModderLords X.Y.Z` heading, then short prose covering:
- what changed, for players and for hosts;
- anything users do NOT need to do;
- how it was validated (test count, what was checked in the app).

Then, **with the maintainer's go-ahead**:

```powershell
gh release create vX.Y.Z artifacts\ModderLords-X.Y.Z.zip --target main --title "ModderLords X.Y.Z" --notes-file <notes.md>
```

Use `--target main` only when `main` is exactly what was packaged.

### 8. Verify what was published

```powershell
gh release view vX.Y.Z --json isPrerelease,isDraft,assets
gh api repos/PlueRyvius/ModderLords/releases/latest --jq .tag_name
```

- The asset name is `ModderLords-X.Y.Z.zip`.
- `digest` starts with `sha256:`.
- `isPrerelease` and `isDraft` are false.
- `/releases/latest` returns `vX.Y.Z`.

If any of these fail, installed copies will not be offered the update.

## Test builds for a few people

Publish as a **prerelease** with a suffixed tag. Installed copies ignore it on both counts; testers download it by hand.

```powershell
gh release create vX.Y.Z-test1 artifacts\ModderLords-X.Y.Z.zip --prerelease --title "ModderLords X.Y.Z test 1" --notes-file <notes.md>
```

## If a bad release went out

- **Deleting the release does not undo installs.** Copies that already updated keep that version.
- **The fix is a new release with a higher version** (X.Y.Z+1). The updater only moves forward, so re-publishing the same tag does not reach anyone who already has it.
- **Never replace the zip under an existing tag.** A copy that checked before the swap holds the old digest and will refuse the new file.
- **One step back by hand:** each updated copy keeps the files it replaced in `.previous\` beside the exe. Users can copy those back.

## Where things are

| What | Where |
|---|---|
| Version | `Directory.Build.props` → `VersionPrefix` |
| Packaging | `scripts/package-release.ps1` |
| Update check / install | `src/ModderLords.Core/Updates/UpdateChecker.cs`, `UpdateInstaller.cs` |
| Update banner | `src/ModderLords.App/ViewModels/UpdateViewModel.cs`, `MainWindow.xaml` |
| Per-user update settings | `%LOCALAPPDATA%\ModderLords\ui-state.json` (`CheckForUpdates`, `SkippedVersion`, `LastUpdateCheckUtc`) |
