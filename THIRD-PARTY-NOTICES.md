# Third-party notices

The `LICENSE` at the root covers the code in this repository only. The release archive is a
self-contained build, so it also carries components owned by other people, each under its own terms.
Nothing in this project's licence grants any rights over them.

## Redistributed in the release archive

| Component | Version | Licence | Notes |
|---|---|---|---|
| .NET runtime and WPF (`System.*`, `Microsoft.*`, `Presentation*`, `coreclr`, `hostfxr`, …) | .NET 10 | MIT (Microsoft) | Bundled because the app publishes self-contained, so testers need no .NET install. |
| Bannerlord.ModuleManager / .Models | 6.0.249 | MIT | Reads and validates Bannerlord module manifests. |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | MVVM source generators used by the WPF app. |

## Build-time only, never redistributed

These are referenced with `ExcludeAssets="runtime"`, so they are compiled against but not shipped:

| Component | Version | Notes |
|---|---|---|
| Bannerlord.ReferenceAssemblies.Core | 1.4.8.119303 | Publicly published reference assemblies for the game's public API. Stubs only — no TaleWorlds game code is copied, compiled in, or distributed here. |
| Lib.Harmony | 2.4.2 | Ships with the game or with the Harmony framework mod; the modules resolve it at runtime from the player's install. |

## Referenced but not owned, redistributed, or modified

- **Mount & Blade II: Bannerlord** (TaleWorlds Entertainment). The launcher starts the game's own
  dedicated server executable, already installed by the user through Steam. No game file is copied,
  patched, or redistributed.
- **Bannerlord Coop** (Steam Workshop item 3770450698, source-available). The launcher locates the
  Coop workshop item on the user's machine and runs the server that ships inside it. Coop's own code is
  never bundled or modified — this was a deliberate design boundary from the start, and the compatibility
  work lives entirely in this project's hook and overlay.
- **Community mods** loaded through the launcher remain under whatever terms their authors set. The
  launcher links to them where they already live via NTFS junctions and never edits a mod folder.

If you believe something here is attributed incorrectly, please open an issue.
