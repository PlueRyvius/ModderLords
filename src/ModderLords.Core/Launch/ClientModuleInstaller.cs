using ModderLords.Core.Modules;
using ModderLords.Core.Profiles;
using ModderLords.Core.Saves;

namespace ModderLords.Core.Launch;

/// <summary>
/// Keeps the shared ModderLords.Compat module installed in the game's Modules folder, where the client loads from.
///
/// The module has to be on both sides of the handshake, but only the server side is ours to place: the client copy
/// is a folder in the game install that a player would otherwise have to copy out of the release zip by hand, and
/// that silently rots when the launcher ships a newer build. So the launcher installs it, and replaces it when the
/// build it carries is newer than the one on disk. It never downgrades, and never touches any other module.
/// </summary>
public static class ClientModuleInstaller
{
    public enum InstallOutcome
    {
        /// <summary>The installed copy is the same version or newer; nothing written.</summary>
        UpToDate,
        /// <summary>No copy was there; one was written.</summary>
        Installed,
        /// <summary>An older copy was replaced.</summary>
        Updated,
        /// <summary>Nothing to install: the launcher has no bundled copy, or the game install was not found.</summary>
        Unavailable,
        /// <summary>The game folder refused the write; the message says why.</summary>
        Failed,
    }

    public sealed record InstallResult(InstallOutcome Outcome, string? InstalledVersion, string? BundledVersion, string Message, string? BackupPath = null);

    /// <summary>Replaced copies are kept here, next to the profiles, so a bad update can be put back by hand.</summary>
    public static string BackupRoot() => Path.Combine(ProfileStore.RootDir, "module-backups");

    public static string TargetDir(string gameRoot) => Path.Combine(gameRoot, "Modules", LaunchSession.SyncModuleId);

    /// <summary>
    /// Installs or updates the client copy of the sync module. Safe to call on every launch: it does nothing at all
    /// when the installed version is already current.
    /// </summary>
    public static InstallResult Ensure(string? gameRoot, string? backupRoot = null)
    {
        var bundled = LaunchSession.LocateSyncModule();
        if (bundled is null)
            return new InstallResult(InstallOutcome.Unavailable, null, null,
                $"the launcher has no bundled {LaunchSession.SyncModuleId} to install (expected it under compat\\ next to the exe)");
        return Ensure(bundled, gameRoot, backupRoot);
    }

    /// <summary>The same, with the bundled copy supplied rather than located next to the exe.</summary>
    public static InstallResult Ensure(DiscoveredModule bundled, string? gameRoot, string? backupRoot = null)
    {
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
            return new InstallResult(InstallOutcome.Unavailable, null, bundled.Version, "the game install was not found, so the client module cannot be installed");

        var target = TargetDir(gameRoot);
        var installed = Directory.Exists(target) && File.Exists(Path.Combine(target, "SubModule.xml"))
            ? ModuleCatalog.TryParse(target, ModuleSourceKind.GameModules, out _)?.Version
            : null;

        if (installed is not null && SaveHeaderReader.CompareVersions(bundled.Version, installed) <= 0)
            return new InstallResult(InstallOutcome.UpToDate, installed, bundled.Version,
                $"{LaunchSession.SyncModuleId} {installed} is already installed for the client");

        try
        {
            string? backup = null;
            if (Directory.Exists(target))
            {
                // Keep the copy we are about to replace: it is the only way back if a new build misbehaves mid-session.
                backup = Path.Combine(backupRoot ?? BackupRoot(), DateTime.Now.ToString("yyyyMMdd-HHmmssfff"), LaunchSession.SyncModuleId);
                CopyDirectory(target, backup);
                Directory.Delete(target, recursive: true);
            }
            CopyDirectory(bundled.FolderPath, target);
            return installed is null
                ? new InstallResult(InstallOutcome.Installed, bundled.Version, bundled.Version,
                    $"installed {LaunchSession.SyncModuleId} {bundled.Version} for the client", backup)
                : new InstallResult(InstallOutcome.Updated, bundled.Version, bundled.Version,
                    $"updated the client's {LaunchSession.SyncModuleId} from {installed} to {bundled.Version}", backup);
        }
        catch (UnauthorizedAccessException ex)
        {
            // Game installs usually live under Program Files, where a non-elevated write is refused.
            return new InstallResult(InstallOutcome.Failed, installed, bundled.Version,
                $"could not write {target} ({ex.Message}). Run the launcher as administrator once, or copy compat\\{LaunchSession.SyncModuleId} there by hand.");
        }
        catch (Exception ex)
        {
            return new InstallResult(InstallOutcome.Failed, installed, bundled.Version, $"could not install the client module: {ex.Message}");
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }
}
