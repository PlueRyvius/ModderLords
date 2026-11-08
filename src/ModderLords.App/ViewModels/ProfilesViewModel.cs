using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModderLords.App.Localization;
using ModderLords.Core.Modules;
using ModderLords.Core.Profiles;
using ModderLords.Core.Saves;

namespace ModderLords.App.ViewModels;

/// <summary>What a profile's Status column says (docs/PROFILE-MANAGER.md, decision 5).</summary>
public enum ProfileStatus { Ready, Missing, Changed, Unreadable }

/// <summary>One profile in the Profiles window.</summary>
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

    public string Summary => Loc.F("{0} mods", ModCount) + (HasCoop ? " · Coop" : "") + (Group.Length > 0 ? " · " + Group : "");
}

/// <summary>One entry in the Profiles window's group list.</summary>
public sealed record ProfileGroupRow(string Key, string Label, int Count)
{
    public const string AllKey = "\u0001all";
    public const string PinnedKey = "\u0001pinned";
    public const string UngroupedKey = "";
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

    public ProfilesViewModel(MainViewModel main)
    {
        _main = main;
        Reload();
    }

    public ObservableCollection<ProfileGroupRow> Groups { get; } = new();
    public ObservableCollection<ProfileRow> Rows { get; } = new();

    [ObservableProperty] private ProfileGroupRow? _selectedGroup;
    [ObservableProperty] private ProfileRow? _selected;
    [ObservableProperty] private string _search = "";
    /// <summary>What the last action did, or why it could not.</summary>
    [ObservableProperty] private string _message = "";

    /// <summary>Asks for a name (new profile, rename, duplicate, new group): prompt, suggestion, the name being renamed, a check. Replaceable for tests.</summary>
    internal Func<string, string, string?, Func<string, string?>, string?> AskName { get; set; } = (prompt, initial, current, problemOf) =>
    {
        var ask = new NameProfileWindow(prompt, initial, current, problemOf) { Owner = System.Windows.Application.Current?.Windows.OfType<ProfilesWindow>().FirstOrDefault() };
        return ask.ShowDialog() == true ? ask.ProfileName : null;
    };

    /// <summary>Asks before a delete. Replaceable for tests.</summary>
    internal Func<string, bool> ConfirmDelete { get; set; } = name => System.Windows.MessageBox.Show(
        Loc.F("Delete profile '{0}'? It is moved to the deleted-profiles folder, so it can be put back by hand.", name),
        Loc.T("Delete profile"), System.Windows.MessageBoxButton.YesNo) == System.Windows.MessageBoxResult.Yes;

    /// <summary>Raised when a profile was put to use, so the window can close.</summary>
    public event Action? Used;

    /// <summary>Reads every profile again and rebuilds both lists, keeping the selection where it can.</summary>
    public void Reload()
    {
        var keepGroup = SelectedGroup?.Key;
        var keepRow = Selected?.Name;
        var catalog = _main.ScannedCatalog?.Catalog;
        _all = ProfileStore.List().Select(name => Row(name, catalog)).ToList();

        var index = ProfileIndexStore.Load();
        var named = _all.Select(r => r.Group).Where(g => g.Length > 0).Concat(index.Groups)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
        Groups.Clear();
        Groups.Add(new ProfileGroupRow(ProfileGroupRow.AllKey, Loc.T("All profiles"), _all.Count));
        Groups.Add(new ProfileGroupRow(ProfileGroupRow.PinnedKey, Loc.T("Pinned"), _all.Count(r => r.Pinned)));
        foreach (var g in named)
            Groups.Add(new ProfileGroupRow(g, g, _all.Count(r => r.Group.Equals(g, StringComparison.OrdinalIgnoreCase))));
        Groups.Add(new ProfileGroupRow(ProfileGroupRow.UngroupedKey, Loc.T("Ungrouped"), _all.Count(r => r.Group.Length == 0)));
        SelectedGroup = Groups.FirstOrDefault(g => g.Key == keepGroup) ?? Groups[0];
        Filter();
        Selected = Rows.FirstOrDefault(r => r.Name == keepRow) ?? Rows.FirstOrDefault(r => r.IsCurrent) ?? Rows.FirstOrDefault();
    }

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
                _ => rows.Where(r => r.Group.Equals(g.Key, StringComparison.OrdinalIgnoreCase)),
            };
        Rows.Clear();
        foreach (var r in rows.OrderByDescending(r => r.Pinned).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)) Rows.Add(r);
        Selected = Rows.FirstOrDefault(r => r.Name == keep) ?? Rows.FirstOrDefault();
    }

    private ProfileRow Row(string name, ModuleCatalog? catalog)
    {
        var isCurrent = name.Equals(_main.Profile.Name, StringComparison.OrdinalIgnoreCase);
        // The open profile as it is in the app (unsaved edits included); the others as saved.
        var profile = isCurrent ? _main.Profile : ProfileStore.TryLoad(name, out _);
        if (profile is null)
            return new ProfileRow { Name = name, IsCurrent = isCurrent, Status = ProfileStatus.Unreadable, StatusText = Loc.T("Can't open"),
                StatusDetail = Loc.T("This profile cannot be read by this version of ModderLords. It was probably saved by a newer one.") };
        var enabled = profile.EnabledMods.ToList();
        var missing = catalog is null ? [] : enabled.Where(m => m.Role != Core.Overlay.ServerRole.ClientOptional && !Installed(catalog, m.Id).Any()).Select(m => m.Id).ToList();
        var changed = catalog is null ? [] : enabled.Where(m => m.LastVersion is { } last && Installed(catalog, m.Id).Any()
            && !Installed(catalog, m.Id).Any(c => SaveHeaderReader.VersionsEqual(c.Version, last))).Select(m => m.Id).ToList();
        var (status, text, detail) = (missing.Count, changed.Count) switch
        {
            ( > 0, _) => (ProfileStatus.Missing, Loc.F("{0} missing", missing.Count), Loc.T("Not installed: ") + string.Join(", ", missing)),
            (_, > 0) => (ProfileStatus.Changed, Loc.T("Mods changed"), Loc.T("A different version than at its last launch: ") + string.Join(", ", changed)),
            _ when catalog is null => (ProfileStatus.Ready, "", Loc.T("Rescan mods to check whether every mod is installed.")),
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

    // ---- actions -------------------------------------------------------------------------------------------

    [RelayCommand]
    private void Use(ProfileRow? row)
    {
        row ??= Selected;
        if (row is null || row.Status == ProfileStatus.Unreadable) return;
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
        var group = SelectedGroup is { Key: var k } && k != ProfileGroupRow.AllKey && k != ProfileGroupRow.PinnedKey ? k : "";
        ProfileStore.Save(new Profile { Name = name, Group = group, SaveName = _main.Profile.SaveName });
        Message = Loc.F("Created '{0}'.", name);
        Reload();
        Selected = Rows.FirstOrDefault(r => r.Name == name);
    }

    [RelayCommand]
    private void Rename(ProfileRow? row)
    {
        row ??= Selected;
        if (row is null) return;
        if (AskName(Loc.T("Rename this profile"), row.Name, row.Name, s => ProfileStore.NameProblem(s, row.Name)) is not { } name || name == row.Name) return;
        Message = _main.RenameNamed(row.Name, name) ?? Loc.F("Renamed '{0}' to '{1}'.", row.Name, name);
        Reload();
    }

    [RelayCommand]
    private void Duplicate(ProfileRow? row)
    {
        row ??= Selected;
        if (row is null) return;
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
        if (row is null) return;
        SetPlacement(row, pinned: !row.Pinned);
    }

    /// <summary>Moves a profile to a group (from the right-click menu, or a drop onto the group list).</summary>
    public void MoveTo(ProfileRow row, string group)
    {
        if (group is ProfileGroupRow.AllKey) return;
        if (group is ProfileGroupRow.PinnedKey) { SetPlacement(row, pinned: true); return; }
        SetPlacement(row, group: group);
    }

    private void SetPlacement(ProfileRow row, string? group = null, bool? pinned = null)
    {
        try
        {
            ProfileStore.UpdatePlacement(row.Name, group, pinned);
            if (row.IsCurrent) _main.SetPlacement(group, pinned);
        }
        catch (Exception ex) { Message = ex.Message; return; }
        Reload();
    }

    [RelayCommand]
    private void NewGroup()
    {
        if (AskName(Loc.T("Name this group"), Loc.T("New group"), null, GroupProblem) is not { } name) return;
        ProfileIndexStore.AddGroup(name);
        Reload();
        SelectedGroup = Groups.FirstOrDefault(g => g.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Asks for a new group's name and moves <paramref name="row"/> into it.</summary>
    [RelayCommand]
    private void MoveToNewGroup(ProfileRow? row)
    {
        row ??= Selected;
        if (row is null || AskName(Loc.T("Name this group"), Loc.T("New group"), null, GroupProblem) is not { } name) return;
        ProfileIndexStore.AddGroup(name);
        SetPlacement(row, group: name);
    }

    private string? GroupProblem(string name)
    {
        var n = name.Trim();
        if (n.Length == 0) return Loc.T("A group needs a name.");
        if (Groups.Any(g => g.Key.Length > 0 && g.Key[0] != '\u0001' && g.Key.Equals(n, StringComparison.OrdinalIgnoreCase))) return Loc.T("There is already a group with that name.");
        return null;
    }

    [RelayCommand]
    private void Delete(ProfileRow? row)
    {
        row ??= Selected;
        if (row is null || !ConfirmDelete(row.Name)) return;
        Message = _main.DeleteNamed(row.Name);
        Reload();
    }

    /// <summary>The groups a profile can be moved to, for the right-click menu's "Move to group" list.</summary>
    public IReadOnlyList<string> MoveTargets() =>
        Groups.Where(g => g.Key != ProfileGroupRow.AllKey && g.Key != ProfileGroupRow.PinnedKey).Select(g => g.Key).ToList();
}
