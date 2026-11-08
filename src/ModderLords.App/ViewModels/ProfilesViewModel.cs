using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModderLords.App.Localization;
using ModderLords.Core.Launch;
using ModderLords.Core.Modules;
using ModderLords.Core.Profiles;
using ModderLords.Core.Saves;

namespace ModderLords.App.ViewModels;

/// <summary>What a profile's Status column says (docs/PROFILE-MANAGER.md, decision 5).</summary>
public enum ProfileStatus { Ready, Missing, Changed, Checking, Unreadable, Deleted }

/// <summary>One profile in the Profiles window (or, in Recently deleted, one waiting to be restored).</summary>
public sealed partial class ProfileRow : ObservableObject
{
    public required string Name { get; init; }
    public string Group { get; init; } = "";
    public bool Pinned { get; init; }
    public int ModCount { get; init; }
    public bool HasCoop { get; init; }
    public bool IsCurrent { get; init; }
    public ProfileStatus Status { get; init; }
    /// <summary>The status column's words: "Ready", "2 missing", "Mods changed".</summary>
    public string StatusText { get; init; } = "";
    /// <summary>The details pane's sentence about the status.</summary>
    public string StatusDetail { get; init; } = "";
    /// <summary>Mod ids, for the search box (it matches mod names too).</summary>
    public IReadOnlyList<string> ModIds { get; init; } = [];
    /// <summary>For a deleted profile: the folder it waits in.</summary>
    public string? DeletedFolder { get; init; }

    public bool IsDeleted => DeletedFolder is not null;
    public bool CanUse => !IsDeleted && Status != ProfileStatus.Unreadable;

    public string Summary => IsDeleted
        ? StatusDetail
        : Loc.F("{0} mods", ModCount) + (HasCoop ? " · Coop" : "") + (Group.Length > 0 ? " · " + Group : "");
}

/// <summary>One entry in the Profiles window's group list.</summary>
public sealed record ProfileGroupRow(string Key, string Label, int Count)
{
    public const string AllKey = "\u0001all";
    public const string PinnedKey = "\u0001pinned";
    public const string DeletedKey = "\u0001deleted";
    public const string UngroupedKey = "";

    /// <summary>A group the user named (not All, Pinned, Ungrouped or Recently deleted): it can be renamed and deleted.</summary>
    public bool IsNamed => Key.Length > 0 && Key[0] != '\u0001';
}

/// <summary>
/// The Profiles window (docs/PROFILE-MANAGER.md, decisions 3–5 and 11): groups on the left, the profiles of the chosen
/// group (or every match for the search) in the middle, the selected one's details on the right. Changes to a profile's
/// place (group, pin) are written straight to its file; switching, renaming and deleting go through
/// <see cref="MainViewModel"/>, which owns the open profile and its unsaved edits.
/// </summary>
public partial class ProfilesViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private List<ProfileRow> _all = [];
    private List<ProfileRow> _deleted = [];

    /// <summary>
    /// The installed mods per mod setup (game folder plus extra mod folders). Nearly every profile shares the open
    /// one's, which the app has already scanned; another setup is scanned once in the background.
    /// </summary>
    private readonly Dictionary<string, ModuleCatalog> _catalogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _scanning = new(StringComparer.OrdinalIgnoreCase);

    public ProfilesViewModel(MainViewModel main)
    {
        _main = main;
        if (main.ScannedCatalog is { Catalog: var catalog }) _catalogs[SetupKey(main.Profile)] = catalog;
        Reload();
    }

    public ObservableCollection<ProfileGroupRow> Groups { get; } = new();
    public ObservableCollection<ProfileRow> Rows { get; } = new();

    [ObservableProperty] private ProfileGroupRow? _selectedGroup;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private ProfileRow? _selected;

    /// <summary>False in an empty group: the details pane hides rather than show buttons for nothing.</summary>
    public bool HasSelection => Selected is not null;
    [ObservableProperty] private string _search = "";
    /// <summary>What the last action did, or why it could not.</summary>
    [ObservableProperty] private string _message = "";

    /// <summary>Asks for a name (new profile, rename, duplicate, a group): prompt, suggestion, the name being renamed, a check. Replaceable for tests.</summary>
    internal Func<string, string, string?, Func<string, string?>, string?> AskName { get; set; } = (prompt, initial, current, problemOf) =>
    {
        var ask = new NameProfileWindow(prompt, initial, current, problemOf) { Owner = System.Windows.Application.Current?.Windows.OfType<ProfilesWindow>().FirstOrDefault() };
        return ask.ShowDialog() == true ? ask.ProfileName : null;
    };

    /// <summary>Asks before a delete. Replaceable for tests.</summary>
    internal Func<string, bool> ConfirmDelete { get; set; } = name => System.Windows.MessageBox.Show(
        Loc.F("Delete profile '{0}'? It goes to Recently deleted, where you can restore it.", name),
        Loc.T("Delete profile"), System.Windows.MessageBoxButton.YesNo) == System.Windows.MessageBoxResult.Yes;

    /// <summary>Raised when a profile was put to use, so the window can close.</summary>
    public event Action? Used;

    /// <summary>Reads every profile again and rebuilds both lists, keeping the selection where it can.</summary>
    public void Reload()
    {
        var keepGroup = SelectedGroup?.Key;
        var keepRow = Selected?.Name;
        var unscanned = new Dictionary<string, Profile>(StringComparer.OrdinalIgnoreCase);
        _all = ProfileStore.List().Select(name => Row(name, unscanned)).ToList();
        _deleted = ProfileStore.ListDeleted().Select(d => new ProfileRow
        {
            Name = d.Name,
            DeletedFolder = d.Folder,
            Status = ProfileStatus.Deleted,
            StatusText = Loc.T("Deleted"),
            StatusDetail = Loc.F("Deleted {0:g}. Right-click to restore it.", d.DeletedAt),
        }).ToList();

        var index = ProfileIndexStore.Load();
        var named = _all.Select(r => r.Group).Where(g => g.Length > 0).Concat(index.Groups)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
        Groups.Clear();
        Groups.Add(new ProfileGroupRow(ProfileGroupRow.AllKey, Loc.T("All profiles"), _all.Count));
        Groups.Add(new ProfileGroupRow(ProfileGroupRow.PinnedKey, Loc.T("Pinned"), _all.Count(r => r.Pinned)));
        foreach (var g in named)
            Groups.Add(new ProfileGroupRow(g, g, _all.Count(r => r.Group.Equals(g, StringComparison.OrdinalIgnoreCase))));
        Groups.Add(new ProfileGroupRow(ProfileGroupRow.UngroupedKey, Loc.T("Ungrouped"), _all.Count(r => r.Group.Length == 0)));
        if (_deleted.Count > 0) Groups.Add(new ProfileGroupRow(ProfileGroupRow.DeletedKey, Loc.T("Recently deleted"), _deleted.Count));
        SelectedGroup = Groups.FirstOrDefault(g => g.Key == keepGroup) ?? Groups[0];
        Filter();
        Selected = Rows.FirstOrDefault(r => r.Name == keepRow) ?? Rows.FirstOrDefault(r => r.IsCurrent) ?? Rows.FirstOrDefault();

        foreach (var (key, profile) in unscanned) _ = ScanAsync(key, profile);
    }

    /// <summary>
    /// Scans one mod setup in the background, then shows the statuses that were waiting on it. In the app only: tests
    /// and other callers without a UI thread see "Checking…".
    /// </summary>
    private async Task ScanAsync(string key, Profile profile)
    {
        if (System.Windows.Application.Current is null || !_scanning.Add(key)) return;
        try
        {
            var catalog = await Task.Run(() => ClientLaunchSession.Scan(profile, out _));
            _catalogs[key] = catalog;
        }
        catch (Exception ex) { Message = Loc.T("Could not check some profiles' mods: ") + ex.Message; return; }
        finally { _scanning.Remove(key); }
        Reload();
    }

    /// <summary>What decides which mods a profile can find: its game folder and its extra mod folders.</summary>
    private static string SetupKey(Profile p) =>
        (p.GameRoot ?? "") + "|" + string.Join("|", p.CustomModRoots.Select(r => r.Trim()).OrderBy(r => r, StringComparer.OrdinalIgnoreCase));

    partial void OnSelectedGroupChanged(ProfileGroupRow? value) => Filter();
    partial void OnSearchChanged(string value) => Filter();

    /// <summary>The rows the list shows: a search looks across every group, by profile name or mod id.</summary>
    private void Filter()
    {
        var keep = Selected?.Name;
        IEnumerable<ProfileRow> rows = _all;
        var term = Search.Trim();
        if (term.Length > 0)
            rows = rows.Where(r => r.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || r.ModIds.Any(m => m.Contains(term, StringComparison.OrdinalIgnoreCase)));
        else if (SelectedGroup is { } g)
            rows = g.Key switch
            {
                ProfileGroupRow.AllKey => rows,
                ProfileGroupRow.PinnedKey => rows.Where(r => r.Pinned),
                ProfileGroupRow.DeletedKey => _deleted,
                _ => rows.Where(r => r.Group.Equals(g.Key, StringComparison.OrdinalIgnoreCase)),
            };
        Rows.Clear();
        var ordered = SelectedGroup?.Key == ProfileGroupRow.DeletedKey && term.Length == 0
            ? rows   // newest first, as listed
            : rows.OrderByDescending(r => r.Pinned).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var r in ordered) Rows.Add(r);
        Selected = Rows.FirstOrDefault(r => r.Name == keep) ?? Rows.FirstOrDefault();
    }

    private ProfileRow Row(string name, Dictionary<string, Profile> unscanned)
    {
        var isCurrent = name.Equals(_main.Profile.Name, StringComparison.OrdinalIgnoreCase);
        // The open profile as it is in the app (unsaved edits included); the others as saved.
        var profile = isCurrent ? _main.Profile : ProfileStore.TryLoad(name, out _);
        if (profile is null)
            return new ProfileRow { Name = name, IsCurrent = isCurrent, Status = ProfileStatus.Unreadable, StatusText = Loc.T("Can't open"),
                StatusDetail = Loc.T("This profile cannot be read by this version of ModderLords. It was probably saved by a newer one.") };
        var enabled = profile.EnabledMods.ToList();
        var key = SetupKey(profile);
        _catalogs.TryGetValue(key, out var catalog);
        if (catalog is null) unscanned.TryAdd(key, profile);
        var missing = catalog is null ? [] : enabled.Where(m => m.Role != Core.Overlay.ServerRole.ClientOptional && !Installed(catalog, m.Id).Any()).Select(m => m.Id).ToList();
        var changed = catalog is null ? [] : enabled.Where(m => m.LastVersion is { } last && Installed(catalog, m.Id).Any()
            && !Installed(catalog, m.Id).Any(c => SaveHeaderReader.VersionsEqual(c.Version, last))).Select(m => m.Id).ToList();
        var (status, text, detail) = (missing.Count, changed.Count) switch
        {
            _ when catalog is null => (ProfileStatus.Checking, Loc.T("Checking…"), Loc.T("Looking for this profile's mods in its own folders.")),
            ( > 0, _) => (ProfileStatus.Missing, Loc.F("{0} missing", missing.Count), Loc.T("Not installed: ") + string.Join(", ", missing)),
            (_, > 0) => (ProfileStatus.Changed, Loc.T("Mods changed"), Loc.T("A different version than at its last launch: ") + string.Join(", ", changed)),
            _ => (ProfileStatus.Ready, Loc.T("Ready"), Loc.T("Every mod is installed, at the version of its last launch.")),
        };
        return new ProfileRow
        {
            Name = profile.Name,
            Group = profile.Group ?? "",
            Pinned = profile.Pinned,
            ModCount = enabled.Count,
            HasCoop = enabled.Any(m => m.Id is "Coop" or "CoopNightly"),
            IsCurrent = isCurrent,
            Status = status,
            StatusText = text,
            StatusDetail = detail,
            ModIds = enabled.Select(m => m.Id).ToList(),
        };
    }

    /// <summary>
    /// The installed copies of a mod. Coop's Workshop build has carried the id CoopNightly and now Coop; a profile
    /// saved with either finds whichever is installed, as the launch does (LoadOrder treats them as one).
    /// </summary>
    private static IEnumerable<DiscoveredModule> Installed(ModuleCatalog catalog, string id) =>
        id is "Coop" or "CoopNightly" ? catalog.Candidates("Coop").Concat(catalog.Candidates("CoopNightly")) : catalog.Candidates(id);

    // ---- actions on a profile ------------------------------------------------------------------------------

    [RelayCommand]
    private void Use(ProfileRow? row)
    {
        row ??= Selected;
        if (row is null || !row.CanUse) return;
        if (!row.IsCurrent && !_main.SwitchTo(row.Name, out var why)) { Message = why ?? ""; return; }
        Used?.Invoke();
    }

    [RelayCommand]
    private void NewProfile()
    {
        var n = 1;
        string suggested;
        do { suggested = $"profile{n++}"; } while (ProfileStore.Exists(suggested));
        if (AskName(Loc.T("Name this profile"), suggested, null, s => ProfileStore.NameProblem(s)) is not { } name) return;
        var group = SelectedGroup is { IsNamed: true } named ? named.Key : "";
        ProfileStore.Save(new Profile { Name = name, Group = group, SaveName = _main.Profile.SaveName });
        Message = Loc.F("Created '{0}'.", name);
        Reload();
        Selected = Rows.FirstOrDefault(r => r.Name == name);
    }

    [RelayCommand]
    private void Rename(ProfileRow? row)
    {
        row ??= Selected;
        if (row is null || row.IsDeleted) return;
        if (AskName(Loc.T("Rename this profile"), row.Name, row.Name, s => ProfileStore.NameProblem(s, row.Name)) is not { } name || name == row.Name) return;
        Message = _main.RenameNamed(row.Name, name) ?? Loc.F("Renamed '{0}' to '{1}'.", row.Name, name);
        Reload();
    }

    [RelayCommand]
    private void Duplicate(ProfileRow? row)
    {
        row ??= Selected;
        if (row is null || row.IsDeleted) return;
        var n = 2;
        string suggested;
        do { suggested = $"{row.Name} {n++}"; } while (ProfileStore.Exists(suggested));
        if (AskName(Loc.T("Name the copy"), suggested, null, s => ProfileStore.NameProblem(s)) is not { } name) return;
        // The open profile's copy carries its unsaved edits, as a copy of what is on screen.
        if (row.IsCurrent) { _main.SaveCurrentAs(name); }
        else ProfileStore.Duplicate(row.Name, name);
        Message = Loc.F("Copied '{0}' to '{1}'.", row.Name, name);
        Reload();
    }

    [RelayCommand]
    private void TogglePin(ProfileRow? row)
    {
        row ??= Selected;
        if (row is null || row.IsDeleted) return;
        SetPlacement(row, pinned: !row.Pinned);
    }

    /// <summary>Moves a profile to a group (from the right-click menu, or a drop onto the group list).</summary>
    public void MoveTo(ProfileRow row, string group)
    {
        if (row.IsDeleted || group is ProfileGroupRow.AllKey or ProfileGroupRow.DeletedKey) return;
        if (group is ProfileGroupRow.PinnedKey) { SetPlacement(row, pinned: true); return; }
        SetPlacement(row, group: group);
    }

    private void SetPlacement(ProfileRow row, string? group = null, bool? pinned = null)
    {
        if (!TryPlace(row.Name, row.IsCurrent, group, pinned)) return;
        Reload();
    }

    private bool TryPlace(string name, bool isCurrent, string? group, bool? pinned)
    {
        try
        {
            ProfileStore.UpdatePlacement(name, group, pinned);
            if (isCurrent) _main.SetPlacement(group, pinned);
            return true;
        }
        catch (Exception ex) { Message = ex.Message; return false; }
    }

    [RelayCommand]
    private void Delete(ProfileRow? row)
    {
        row ??= Selected;
        if (row is null || row.IsDeleted || !ConfirmDelete(row.Name)) return;
        Message = _main.DeleteNamed(row.Name);
        Reload();
    }

    /// <summary>Puts a profile from Recently deleted back in the list.</summary>
    [RelayCommand]
    private void Restore(ProfileRow? row)
    {
        row ??= Selected;
        if (row?.DeletedFolder is not { } folder) return;
        try { Message = Loc.F("Restored '{0}'.", ProfileStore.Restore(folder)); }
        catch (Exception ex) { Message = ex.Message; return; }
        _main.RefreshProfileList();
        var name = row.Name;
        Reload();
        SelectedGroup = Groups[0];
        Selected = Rows.FirstOrDefault(r => r.Name == name);
    }

    // ---- groups --------------------------------------------------------------------------------------------

    [RelayCommand]
    private void NewGroup()
    {
        if (AskName(Loc.T("Name this group"), Loc.T("New group"), null, s => GroupProblem(s, null)) is not { } name) return;
        ProfileIndexStore.AddGroup(name);
        Reload();
        SelectedGroup = Groups.FirstOrDefault(g => g.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Asks for a new group's name and moves <paramref name="row"/> into it.</summary>
    [RelayCommand]
    private void MoveToNewGroup(ProfileRow? row)
    {
        row ??= Selected;
        if (row is null || row.IsDeleted || AskName(Loc.T("Name this group"), Loc.T("New group"), null, s => GroupProblem(s, null)) is not { } name) return;
        ProfileIndexStore.AddGroup(name);
        SetPlacement(row, group: name);
    }

    /// <summary>Renames a group: every profile in it moves to the new name.</summary>
    [RelayCommand]
    private void RenameGroup(ProfileGroupRow? group)
    {
        group ??= SelectedGroup;
        if (group is not { IsNamed: true }) return;
        if (AskName(Loc.T("Rename this group"), group.Key, null, s => GroupProblem(s, group.Key)) is not { } name || name == group.Key) return;
        var moved = 0;
        foreach (var row in _all.Where(r => r.Group.Equals(group.Key, StringComparison.OrdinalIgnoreCase)))
            if (TryPlace(row.Name, row.IsCurrent, name, null)) moved++;
        ProfileIndexStore.RemoveGroup(group.Key);
        ProfileIndexStore.AddGroup(name);
        Message = Loc.F("Renamed group '{0}' to '{1}' ({2} profiles).", group.Key, name, moved);
        Reload();
        SelectedGroup = Groups.FirstOrDefault(g => g.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Removes a group. Its profiles are kept, in Ungrouped: deleting a group never deletes a profile.</summary>
    [RelayCommand]
    private void DeleteGroup(ProfileGroupRow? group)
    {
        group ??= SelectedGroup;
        if (group is not { IsNamed: true }) return;
        var moved = 0;
        foreach (var row in _all.Where(r => r.Group.Equals(group.Key, StringComparison.OrdinalIgnoreCase)))
            if (TryPlace(row.Name, row.IsCurrent, "", null)) moved++;
        ProfileIndexStore.RemoveGroup(group.Key);
        Message = moved == 0 ? Loc.F("Removed group '{0}'.", group.Key) : Loc.F("Removed group '{0}'; its {1} profiles are now in Ungrouped.", group.Key, moved);
        SelectedGroup = null;
        Reload();
    }

    private string? GroupProblem(string name, string? renaming)
    {
        var n = name.Trim();
        if (n.Length == 0) return Loc.T("A group needs a name.");
        if (renaming is not null && n.Equals(renaming, StringComparison.Ordinal)) return null;
        if (Groups.Any(g => g.IsNamed && g.Key.Equals(n, StringComparison.OrdinalIgnoreCase) && !g.Key.Equals(renaming, StringComparison.OrdinalIgnoreCase)))
            return Loc.T("There is already a group with that name.");
        return null;
    }

    /// <summary>The groups a profile can be moved to, for the right-click menu's "Move to group" list.</summary>
    public IReadOnlyList<string> MoveTargets() =>
        Groups.Where(g => g.IsNamed || g.Key == ProfileGroupRow.UngroupedKey).Select(g => g.Key).ToList();
}
