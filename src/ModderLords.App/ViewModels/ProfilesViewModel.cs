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

    /// <summary>For a verified profile (the Verified group): its entry, and how each of its mods stands here.</summary>
    public VerifiedProfile? Verified { get; init; }
    public IReadOnlyList<VerifiedModCheck> Checks { get; init; } = [];
    /// <summary>The details pane's lines: each mod with its verified version and how it stands here.</summary>
    public IReadOnlyList<string> ModLines { get; init; } = [];
    /// <summary>The details pane's lines: who verified it, when, and with what.</summary>
    public IReadOnlyList<string> Claims { get; init; } = [];

    public bool IsDeleted => DeletedFolder is not null;
    public bool IsVerified => Verified is not null;
    /// <summary>Gold, Silver or Green; null for a draft and for the user's own profiles.</summary>
    public VerificationLevel? Level => Verified?.Level;
    /// <summary>The badge beside a verified profile's name.</summary>
    public string Badge => Verified is null ? "" : Level switch
    {
        VerificationLevel.Gold => Loc.T("Verified by ModderLords"),
        VerificationLevel.Silver => Loc.T("Verified by Coodsz"),
        VerificationLevel.Green => Loc.T("Works for players"),
        _ => Loc.T("Draft"),
    };
    public string Description => Verified?.Description ?? "";
    public bool CanUse => !IsDeleted && Status != ProfileStatus.Unreadable;
    /// <summary>Rename, pin, move and delete: the user's own profiles only.</summary>
    public bool CanEdit => !IsDeleted && !IsVerified;
    /// <summary>Some mod is missing or at another version, and there is somewhere to get the verified one.</summary>
    public bool CanGetMissing => Checks.Any(c => c.State != VerifiedModState.Match && c.Fetch != VerifiedFetch.None);

    public string Summary => IsDeleted
        ? StatusDetail
        : Loc.F("{0} mods", ModCount) + (HasCoop ? " · Coop" : "")
          + (Verified is { Game.Length: > 0 } v ? " · " + Loc.F("game {0}", v.Game) : Group.Length > 0 ? " · " + Group : "");
}

/// <summary>One entry in the Profiles window's group list.</summary>
public sealed record ProfileGroupRow(string Key, string Label, int Count)
{
    public const string VerifiedKey = "\u0001verified";
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
    private List<ProfileRow> _verified = [];

    /// <summary>
    /// The installed mods per mod setup (game folder plus extra mod folders). Nearly every profile shares the open
    /// one's, which the app has already scanned; another setup is scanned once in the background.
    /// </summary>
    private readonly Dictionary<string, ModuleCatalog> _catalogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _scanning = new(StringComparer.OrdinalIgnoreCase);

    public ProfilesViewModel(MainViewModel main)
    {
        _main = main;
        SubscribeFromWorkshop = main.SubscribeToWorkshop;
        if (main.ScannedCatalog is { Catalog: var catalog }) _catalogs[SetupKey(main.Profile)] = catalog;
        // Read fresh, so a file fetched since the app started is the one shown.
        VerifiedProfiles.Reload();
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

    /// <summary>Downloads GitHub releases with progress; true when any installed. Replaceable for tests.</summary>
    internal Func<IReadOnlyList<VerifiedMod>, bool> DownloadFromGitHub { get; set; } = mods =>
    {
        var window = new ModDownloadWindow(mods) { Owner = System.Windows.Application.Current?.Windows.OfType<ProfilesWindow>().FirstOrDefault() };
        window.ShowDialog();
        return window.AnyInstalled;
    };

    /// <summary>Subscribes to Workshop items; true when any installed. Replaceable for tests.</summary>
    internal Func<IReadOnlyList<(ulong WorkshopId, string ModId)>, bool> SubscribeFromWorkshop { get; set; }

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

        _verified = VerifiedProfiles.Current.Shown.Select(v => VerifiedRow(v, unscanned)).ToList();

        var index = ProfileIndexStore.Load();
        var named = _all.Select(r => r.Group).Where(g => g.Length > 0).Concat(index.Groups)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
        Groups.Clear();
        if (_verified.Count > 0) Groups.Add(new ProfileGroupRow(ProfileGroupRow.VerifiedKey, Loc.T("Verified by ModderLords"), _verified.Count));
        Groups.Add(new ProfileGroupRow(ProfileGroupRow.AllKey, Loc.T("All profiles"), _all.Count));
        Groups.Add(new ProfileGroupRow(ProfileGroupRow.PinnedKey, Loc.T("Pinned"), _all.Count(r => r.Pinned)));
        foreach (var g in named)
            Groups.Add(new ProfileGroupRow(g, g, _all.Count(r => r.Group.Equals(g, StringComparison.OrdinalIgnoreCase))));
        Groups.Add(new ProfileGroupRow(ProfileGroupRow.UngroupedKey, Loc.T("Ungrouped"), _all.Count(r => r.Group.Length == 0)));
        if (_deleted.Count > 0) Groups.Add(new ProfileGroupRow(ProfileGroupRow.DeletedKey, Loc.T("Recently deleted"), _deleted.Count));
        SelectedGroup = Groups.FirstOrDefault(g => g.Key == keepGroup) ?? AllGroup;
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

    private ProfileGroupRow AllGroup => Groups.First(g => g.Key == ProfileGroupRow.AllKey);

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
            rows = _verified.Concat(rows).Where(r => r.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || r.ModIds.Any(m => m.Contains(term, StringComparison.OrdinalIgnoreCase)));
        else if (SelectedGroup is { } g)
            rows = g.Key switch
            {
                ProfileGroupRow.VerifiedKey => _verified,
                ProfileGroupRow.AllKey => rows,
                ProfileGroupRow.PinnedKey => rows.Where(r => r.Pinned),
                ProfileGroupRow.DeletedKey => _deleted,
                _ => rows.Where(r => r.Group.Equals(g.Key, StringComparison.OrdinalIgnoreCase)),
            };
        Rows.Clear();
        var ordered = SelectedGroup?.Key is ProfileGroupRow.DeletedKey or ProfileGroupRow.VerifiedKey && term.Length == 0
            ? rows   // newest first, or the file's own order
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
    /// The installed copies of a mod, ModderLords' own downloads included. Coop's Workshop build has carried the id
    /// CoopNightly and now Coop; a profile saved with either finds whichever is installed, as the launch does
    /// (LoadOrder treats them as one).
    /// </summary>
    private static IEnumerable<DiscoveredModule> Installed(ModuleCatalog catalog, string id) =>
        (id is "Coop" or "CoopNightly" ? catalog.Candidates("Coop").Concat(catalog.Candidates("CoopNightly")) : catalog.Candidates(id))
        .Concat(DownloadedMods.Copies(id));

    /// <summary>
    /// A verified profile's row: its mods against what is installed for the open profile's folders, which a first
    /// use takes over (decision 8: a claim holds for the versions it was made with, so a difference is spelled out).
    /// </summary>
    private ProfileRow VerifiedRow(VerifiedProfile v, Dictionary<string, Profile> unscanned)
    {
        var key = SetupKey(_main.Profile);
        _catalogs.TryGetValue(key, out var catalog);
        if (catalog is null) unscanned.TryAdd(key, _main.Profile);
        var checks = catalog is null ? [] : VerifiedProfiles.Compare(v, id => Installed(catalog, id));
        var missing = checks.Where(c => c.State == VerifiedModState.Missing).Select(c => c.Mod.Id).ToList();
        var different = checks.Where(c => c.State == VerifiedModState.Different)
            .Select(c => Loc.F("verified with {0} {1}, you have {2}", c.Mod.Id, c.Mod.Version, c.Installed ?? "")).ToList();
        var (status, text, detail) = (missing.Count, different.Count) switch
        {
            _ when catalog is null => (ProfileStatus.Checking, Loc.T("Checking…"), Loc.T("Looking for this profile's mods.")),
            ( > 0, _) => (ProfileStatus.Missing, Loc.F("{0} missing", missing.Count), Loc.T("Not installed: ") + string.Join(", ", missing)),
            (_, > 0) => (ProfileStatus.Changed, Loc.T("Mods changed"), string.Join("; ", different)),
            _ => (ProfileStatus.Ready, Loc.T("Ready"), Loc.T("Every mod is installed at the version it was verified with.")),
        };
        return new ProfileRow
        {
            Name = v.Name,
            ModCount = v.Mods.Count,
            HasCoop = v.Mods.Any(m => m.Id is "Coop" or "CoopNightly"),
            IsCurrent = _main.Profile.VerifiedId == v.Id,
            Status = status,
            StatusText = text,
            StatusDetail = detail,
            ModIds = v.Mods.Select(m => m.Id).ToList(),
            Verified = v,
            Checks = checks,
            ModLines = v.Mods.Select(m => ModLine(m, checks.FirstOrDefault(c => c.Mod == m))).ToList(),
            Claims = v.Verifications.Count == 0 ? [Loc.T("Draft: not verified yet. Only development builds list it.")] : v.Verifications.Select(Claim).ToList(),
        };
    }

    private static string ModLine(VerifiedMod m, VerifiedModCheck? check)
    {
        var line = $"{m.Id} {m.Version}";
        if (check is null || check.State == VerifiedModState.Match) return line;
        line += check.State == VerifiedModState.Missing ? " · " + Loc.T("not installed") : " · " + Loc.F("you have {0}", check.Installed ?? "");
        return line + check.Fetch switch
        {
            VerifiedFetch.GitHub => " · " + Loc.T("from GitHub"),
            VerifiedFetch.Workshop => " · " + Loc.T("from the Workshop"),
            _ => "",
        };
    }

    private static string Claim(Verification v)
    {
        var who = v.Level switch
        {
            VerificationLevel.Gold => Loc.T("Verified by ModderLords"),
            VerificationLevel.Silver => Loc.T("Verified by Coodsz"),
            _ => Loc.F("Works for {0} players", v.Reports ?? 1),
        };
        var when = v.Level == VerificationLevel.Green ? v.LastReport ?? v.Date : v.Date;
        return string.Join(" · ", new[] { who, when, v.Tests, v.Notes }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    // ---- actions on a profile ------------------------------------------------------------------------------

    [RelayCommand]
    private void Use(ProfileRow? row)
    {
        row ??= Selected;
        if (row is null || !row.CanUse) return;
        if (row.Verified is { } entry)
        {
            if (!_main.OpenVerified(entry, row.Checks, out var refused)) { Message = refused ?? ""; return; }
        }
        else if (!row.IsCurrent && !_main.SwitchTo(row.Name, out var why)) { Message = why ?? ""; return; }
        Used?.Invoke();
    }

    /// <summary>
    /// Gets a verified profile's missing mods, and the verified version of those installed at another one: the
    /// maintainer's GitHub releases where the Workshop does not serve that version yet (decision 12), the Workshop
    /// for the rest. A profile file already written for it is brought up to date, so it pins what was downloaded.
    /// </summary>
    [RelayCommand]
    private void GetMissing(ProfileRow? row)
    {
        row ??= Selected;
        if (row?.Verified is not { } entry) return;
        var need = row.Checks.Where(c => c.State != VerifiedModState.Match).ToList();
        var fromGitHub = need.Where(c => c.Fetch == VerifiedFetch.GitHub).Select(c => c.Mod).ToList();
        var fromWorkshop = need.Where(c => c.Fetch == VerifiedFetch.Workshop).Select(c => (c.Mod.WorkshopId!.Value, c.Mod.Id)).ToList();
        var nowhere = need.Where(c => c.Fetch == VerifiedFetch.None).Select(c => c.Mod.Id).ToList();

        var installed = false;
        if (fromGitHub.Count > 0) installed |= DownloadFromGitHub(fromGitHub);
        if (fromWorkshop.Count > 0)
        {
            installed |= SubscribeFromWorkshop(fromWorkshop);
            if (_main.ScannedCatalog is { Catalog: var catalog }) _catalogs[SetupKey(_main.Profile)] = catalog;
        }
        Message = nowhere.Count > 0 ? Loc.T("No download is known for: ") + string.Join(", ", nowhere) : "";
        if (!installed) return;

        Reload();
        if (_verified.FirstOrDefault(r => r.Verified?.Id == entry.Id) is not { } updated) return;
        var file = ProfileStore.List().FirstOrDefault(n => ProfileStore.TryLoad(n, out _)?.VerifiedId == entry.Id);
        if (file is not null && !updated.IsCurrent && ProfileStore.TryLoad(file, out _) is { } existing)
            ProfileStore.Save(VerifiedProfiles.ToProfile(entry, file, existing, updated.Checks));
        else if (updated.IsCurrent && !_main.OpenVerified(entry, updated.Checks, out var why)) Message = why ?? "";
        Reload();
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
        if (row is null || !row.CanEdit) return;
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
        // A verified profile's copy is the user's own: its mods are theirs to change.
        if (row.Verified is { } entry) ProfileStore.Save(VerifiedProfiles.ToProfile(entry, name, _main.Profile, row.Checks, ownCopy: true));
        // The open profile's copy carries its unsaved edits, as a copy of what is on screen.
        else if (row.IsCurrent) { _main.SaveCurrentAs(name); }
        else ProfileStore.Duplicate(row.Name, name);
        Message = Loc.F("Copied '{0}' to '{1}'.", row.Name, name);
        Reload();
    }

    [RelayCommand]
    private void TogglePin(ProfileRow? row)
    {
        row ??= Selected;
        if (row is null || !row.CanEdit) return;
        SetPlacement(row, pinned: !row.Pinned);
    }

    /// <summary>Moves a profile to a group (from the right-click menu, or a drop onto the group list).</summary>
    public void MoveTo(ProfileRow row, string group)
    {
        if (!row.CanEdit || group is ProfileGroupRow.AllKey or ProfileGroupRow.DeletedKey or ProfileGroupRow.VerifiedKey) return;
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
        if (row is null || !row.CanEdit || !ConfirmDelete(row.Name)) return;
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
        SelectedGroup = AllGroup;
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
        if (row is null || !row.CanEdit || AskName(Loc.T("Name this group"), Loc.T("New group"), null, s => GroupProblem(s, null)) is not { } name) return;
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
