using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using ModderLords.Core.Overlay;

namespace ModderLords.App.ViewModels;

/// <summary>Who is shown one top-level folder of a mod.</summary>
public enum FolderSide { Both, ServerOnly, ClientOnly }

/// <summary>One folder in the folders dialog: its name, who is shown it, and whether that can be changed.</summary>
public partial class ModFolderRow : ObservableObject
{
    public const string BothLabel = "Server + Client";
    public const string ServerOnlyLabel = "Server only";
    public const string ClientOnlyLabel = "Client only";

    /// <summary>The drop-down's entries, in the order of <see cref="FolderSide"/>.</summary>
    public static IReadOnlyList<string> Labels { get; } = [BothLabel, ServerOnlyLabel, ClientOnlyLabel];

    public required string Name { get; init; }
    /// <summary>False for a name a list holds that is not a folder of the copy being looked at.</summary>
    public bool IsPresent { get; init; } = true;
    /// <summary>The launch decides this folder itself, whatever a list says, so the row only reports it.</summary>
    public bool IsLocked { get; init; }
    public bool CanChange => !IsLocked;
    public string Note { get; init; } = "";

    [ObservableProperty] private FolderSide _side;

    /// <summary><see cref="Side"/> as the drop-down shows it.</summary>
    public string Choice
    {
        get => Labels[(int)Side];
        set
        {
            var index = Labels.ToList().IndexOf(value);
            if (index >= 0) Side = (FolderSide)index;
        }
    }

    partial void OnSideChanged(FolderSide value) => OnPropertyChanged(nameof(Choice));
}

/// <summary>
/// The folders dialog without its window: the folders of one copy of a mod and the two lists a profile keeps about
/// them go in, rows come out, and the rows turn back into the two lists.
///
/// The profile does not store a choice per folder. It stores the two exceptions: the folders the server is not
/// shown ("Client only", <c>ServerExcludedFolders</c>) and the ones the game is not shown ("Server only",
/// <c>ClientExcludedFolders</c>). Everything else is "Server + Client", which is why a mod nobody has opened this
/// dialog for needs nothing stored at all.
/// </summary>
public sealed class ModFolderChoices
{
    private readonly List<string> _recordServerExcluded;
    /// <summary>Names of locked rows the server list already held; see <see cref="ServerExcluded"/>.</summary>
    private readonly HashSet<string> _lockedInServerList = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ModFolderRow> Rows { get; }

    /// <param name="foldersOnDisk">Names of the folders directly inside this copy of the mod.</param>
    /// <param name="profileServerExcluded">The profile's "Client only" list; null when it has no opinion.</param>
    /// <param name="recordServerExcluded">The compat record's list, which applies while the profile has none.</param>
    /// <param name="profileClientExcluded">The profile's "Server only" list.</param>
    public ModFolderChoices(IEnumerable<string> foldersOnDisk, IReadOnlyList<string>? profileServerExcluded,
        IReadOnlyList<string> recordServerExcluded, IReadOnlyList<string>? profileClientExcluded)
    {
        _recordServerExcluded = Names(recordServerExcluded);
        var clientOnly = Names(profileServerExcluded ?? recordServerExcluded);
        var serverOnly = Names(profileClientExcluded);
        var onDisk = Names(foldersOnDisk);

        // Names a list holds that this copy has no folder for are rows too. Another copy of the mod may have the
        // folder, and a row is the only place a host can see such a name and take it off the list.
        var rows = new List<ModFolderRow>();
        foreach (var name in onDisk.Concat(clientOnly.Concat(serverOnly).Where(n => !Has(onDisk, n)))
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var present = Has(onDisk, name);
            if (LockedSide(name) is { } locked)
            {
                if (Has(clientOnly, name)) _lockedInServerList.Add(name);
                rows.Add(new ModFolderRow
                {
                    Name = name, IsPresent = present, IsLocked = true, Side = locked.Side,
                    Note = locked.Reason + (present ? "" : " " + NotPresentNote),
                });
                continue;
            }
            // In both lists nobody would be shown the folder. "Client only" wins: the server's list is the older one
            // and the one a compat record can also be the source of.
            var side = Has(clientOnly, name) ? FolderSide.ClientOnly : Has(serverOnly, name) ? FolderSide.ServerOnly : FolderSide.Both;
            var note = present ? "" : NotPresentNote;
            if (ServerFolderExclusions.Problem(name) is { } problem)
                note = $"Ignored when launching: {problem}. Choose {ModFolderRow.BothLabel} to remove it.";
            rows.Add(new ModFolderRow { Name = name, IsPresent = present, Side = side, Note = note });
        }
        Rows = rows;
    }

    internal const string NotPresentNote = "Not present in this copy of the mod. Choose " + ModFolderRow.BothLabel + " to forget it.";

    /// <summary>
    /// The two folders whose side is not a choice. <c>bin</c> holds the mod's code and both sides load it (the server
    /// through its own redirect). <c>AssetPackages</c> is never given to the dedicated server, whatever a list says:
    /// that is hard-coded where the server's shadow is built (OverlayApplier.BuildShadow).
    /// </summary>
    private static (FolderSide Side, string Reason)? LockedSide(string name) =>
        name.Equals("bin", StringComparison.OrdinalIgnoreCase)
            ? (FolderSide.Both, "Always both: this is the mod's code.")
        : name.Equals("AssetPackages", StringComparison.OrdinalIgnoreCase)
            ? (FolderSide.ClientOnly, "Always client only: the dedicated server is never shown AssetPackages.")
        : null;

    /// <summary>
    /// What to store as the profile's "Client only" list. Null is "no opinion", so the compat record's list applies:
    /// that is the answer whenever the rows say exactly what the record says, which keeps a profile that follows the
    /// database following it when the dialog is opened and closed, and never pins an empty list against a record a
    /// later release might ship. Anything else is the explicit list, and an empty one overrules the record.
    ///
    /// A locked row is not a choice, so it must not look like an edit: it hands back whatever the list said about it.
    /// </summary>
    public List<string>? ServerExcluded
    {
        get
        {
            var names = Rows.Where(r => r.IsLocked ? _lockedInServerList.Contains(r.Name) : r.Side == FolderSide.ClientOnly)
                .Select(r => r.Name).ToList();
            return SameNames(names, _recordServerExcluded) ? null : names;
        }
    }

    /// <summary>What to store as the profile's "Server only" list; null when there is none.</summary>
    public List<string>? ClientExcluded
    {
        get
        {
            var names = Rows.Where(r => !r.IsLocked && r.Side == FolderSide.ServerOnly).Select(r => r.Name).ToList();
            return names.Count == 0 ? null : names;
        }
    }

    /// <summary>Names of the folders directly inside a mod's folder; none when it is missing or cannot be read.</summary>
    public static IReadOnlyList<string> FoldersIn(string? modFolder)
    {
        if (string.IsNullOrWhiteSpace(modFolder) || !Directory.Exists(modFolder)) return [];
        try { return Directory.EnumerateDirectories(modFolder).Select(d => Path.GetFileName(d)!).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Whether two lists name the same folders, whatever their order or casing.</summary>
    public static bool SameNames(IEnumerable<string>? a, IEnumerable<string>? b) =>
        new HashSet<string>(Names(a), StringComparer.OrdinalIgnoreCase).SetEquals(Names(b));

    private static List<string> Names(IEnumerable<string>? names) =>
        (names ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static bool Has(List<string> names, string name) => names.Contains(name, StringComparer.OrdinalIgnoreCase);
}
