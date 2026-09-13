# Notes for coding agents

ModderLords is a Bannerlord mod loader (Player mode) with Bannerlord Coop dedicated-server hosting (Host mode).
C#/.NET 10, WPF app. Read `README.md` for what it does and `docs/DEVELOPMENT.md` for how it is built.

## Releasing

**Follow `docs/RELEASING.md` step by step.** Installed copies update themselves from GitHub releases, so a published
release reaches every user at once. Never publish (step 7) without the maintainer's explicit go-ahead.

Test builds go out as prereleases with a suffixed tag, which installed copies ignore.

## Working rules

- **Start from GitHub state.** `git fetch`, branch from `origin/main`, one PR per change.
- **Never stack PRs, and never push to a merged PR's branch.** Check merged work by content:

  ```powershell
  git merge-base --is-ancestor <sha> origin/main
  ```

- **Agents cannot merge on this repo** (`gh pr merge` is blocked). The maintainer merges.
- **Close any running ModderLords before building.** A running dev build locks the App's output exe.
- **Run the app for UI changes.** A green build proves nothing for UI. Never drive the desktop while someone is using the machine; take a screenshot first.
- **Windows PowerShell 5.1:**
  - Pass commit messages and release notes via files (`git commit -F`, `--notes-file`).
  - Build non-ASCII characters in scripts from code points, e.g. `[char]0x2026`.
- **Licensing:** never copy code from Bannerlord Coop or HexTool. Both are source-available only.
