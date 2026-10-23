using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModderLords.Coop.Admin;
using ModderLords.App.Localization;

namespace ModderLords.App.ViewModels;

/// <summary>One number the editor can change: an attribute, a trait, or one of the hero's single values.</summary>
public partial class EditValue : ObservableObject
{
    public EditValue(string id, string name, int original, int min, int max, string detail = "")
    {
        Id = id;
        Name = name;
        Original = original;
        _value = original;
        Min = min;
        Max = max;
        Detail = detail;
    }

    public string Id { get; }
    public string Name { get; }
    public string Detail { get; }
    public int Original { get; }
    public int Min { get; }
    public int Max { get; }
    public string Range => $"{Min} to {Max}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    private int _value;

    public bool IsChanged => Value != Original;
    public bool InRange => Value >= Min && Value <= Max;
}

/// <summary>One skill: its value and its focus are edited together.</summary>
public partial class SkillEdit : ObservableObject
{
    public SkillEdit(HeroSkill skill, string attributeName, int maxSkill, int maxFocus)
    {
        Id = skill.Id;
        Name = skill.Name;
        Attribute = attributeName;
        OriginalValue = skill.Value;
        OriginalFocus = skill.Focus;
        _value = skill.Value;
        _focus = skill.Focus;
        MaxValue = maxSkill;
        MaxFocus = maxFocus;
    }

    public string Id { get; }
    public string Name { get; }
    public string Attribute { get; }
    public int OriginalValue { get; }
    public int OriginalFocus { get; }
    public int MaxValue { get; }
    public int MaxFocus { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    private int _value;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    private int _focus;

    public bool IsChanged => Value != OriginalValue || Focus != OriginalFocus;
}

/// <summary>One relative in the editor's family strip; clicking it opens that hero in the same editor.</summary>
public sealed record RelativeRow(HeroRelative Relative, string Relation, string OwnClan)
{
    public string Target => HeroTargets.Hero(Relative.HeroId);
    public string Name => Relative.Name;
    public bool Female => Relative.Female;
    public bool Alive => Relative.Alive;
    public string Initial => Relative.Name.Length > 0 ? Relative.Name.Substring(0, 1).ToUpperInvariant() : "?";
    /// <summary>Outside the clan of the hero being edited: shown with a dashed edge.</summary>
    public bool OtherClan => Relative.Clan.Length > 0 && Relative.Clan != OwnClan;
    public string Detail => (Relative.Alive ? Relative.Age.ToString(System.Globalization.CultureInfo.CurrentCulture) : Loc.T("dead"))
        + (OtherClan ? ", " + Relative.Clan : "");
    public string ToolTip => $"{Relation}: {Relative.Name}" + (Relative.Alive ? Loc.F(", {0}", Relative.Age) : Loc.T(", dead"))
        + (Relative.Clan.Length > 0 ? Loc.F(", clan {0}", Relative.Clan) : "") + Loc.T(". Click to edit.");
}

/// <summary>A hero whose face "Copy face from" can take: a relative on show, a player, or a companion.</summary>
public sealed record FaceSource(string Target, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The character editor: one hero (a player's own, or a companion or family member of a player's clan), read from the
/// running server and changed there. Only the values the host actually changed are sent, as new values, and the server
/// checks every one before applying any.
/// </summary>
public partial class CharacterEditorViewModel : ObservableObject
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(15);

    private readonly AdminClient _admin;
    private readonly Func<bool> _serving;

    private readonly IReadOnlyList<FaceSource> _otherFaces;
    private readonly Stack<string> _history = new();

    /// <param name="target">A player's Steam id, or <see cref="HeroTargets.Hero"/> for any other hero.</param>
    /// <param name="faces">Players and companions offered by "Copy face from", besides the relatives on show.</param>
    public CharacterEditorViewModel(AdminClient admin, string target, string name, Func<bool> serving, IReadOnlyList<FaceSource>? faces = null)
    {
        _admin = admin;
        _serving = serving;
        _otherFaces = faces ?? [];
        _target = target;
        _title = "Edit " + name;
        TraitsView = CollectionViewSource.GetDefaultView(Traits);
        TraitsView.Filter = o => ShowHiddenTraits || o is not EditValue t || t.Detail.Length == 0;
    }

    /// <summary>The hero being edited. Changes when a relative is opened from the family strip.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    private string _target;

    /// <summary>Asks before taking every perk away. Replaced in tests, which have no message box.</summary>
    public Func<string, bool> Confirm { get; set; } = message =>
        System.Windows.MessageBox.Show(message, "Reset perks", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.OK;

    /// <summary>The hero's name as typed here; sent only when it differs from <see cref="OriginalName"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNameChanged))]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNameChanged))]
    private string _originalName = "";

    public bool IsNameChanged => Name.Trim() != OriginalName;

    public IReadOnlyList<string> Genders { get; } = ["Male", "Female"];

    /// <summary>0 = male, 1 = female, as the Gender list shows them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGenderChanged))]
    private int _genderIndex;

    private int _originalGender;
    public bool IsGenderChanged => GenderIndex != _originalGender;

    /// <summary>Appearance DNA as typed or pasted here; sent only when it differs from the hero's.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBodyChanged))]
    private string _body = "";

    private string _originalBody = "";
    public bool IsBodyChanged => _originalBody.Length > 0 && HeroBodies.Normalize(Body) != _originalBody;

    public ObservableCollection<RelativeRow> Parents { get; } = new();
    public ObservableCollection<RelativeRow> Spouses { get; } = new();
    public ObservableCollection<RelativeRow> Siblings { get; } = new();
    public ObservableCollection<RelativeRow> Children { get; } = new();
    public ObservableCollection<FaceSource> FaceSources { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyFaceFromCommand))]
    private FaceSource? _faceSource;

    // ---- family actions -------------------------------------------------------------------------------

    [ObservableProperty] private string _heroSearch = "";
    public ObservableCollection<HeroRelative> HeroResults { get; } = new();

    /// <summary>The hero the family actions are about: who to marry, who becomes a parent.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MarryCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetFatherCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetMotherCommand))]
    [NotifyCanExecuteChangedFor(nameof(AdoptCommand))]
    private HeroRelative? _pickedHero;

    [ObservableProperty] private string _clanSearch = "";
    public ObservableCollection<HoldingClan> ClanResults { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveToClanCommand))]
    private HoldingClan? _pickedClan;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DivorceCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearFatherCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearMotherCommand))]
    private HeroDetail? _shown;

    [RelayCommand]
    private async Task SearchHeroes()
    {
        if (HeroSearch.Trim().Length < 2) { Status = Loc.T("Type at least two letters of the name."); return; }
        if (!_serving()) { Status = Loc.T("The server is not running."); return; }
        try
        {
            var reply = await _admin.RequestAsync("findheroes", [FamilyArgs.Search(HeroSearch)], ReplyTimeout);
            if (!reply.Ok) { Status = reply.Error ?? ""; return; }
            HeroResults.Clear();
            foreach (var h in HeroRelative.ListFrom(reply).Where(h => HeroTargets.Hero(h.HeroId) != Target)) HeroResults.Add(h);
            PickedHero = HeroResults.FirstOrDefault();
            Status = HeroResults.Count == 0 ? Loc.F("No hero is called anything like \u201c{0}\u201d.", HeroSearch.Trim()) : Loc.F("{0} hero(es) found; pick one below.", HeroResults.Count);
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    [RelayCommand]
    private async Task SearchClans()
    {
        if (!_serving()) { Status = Loc.T("The server is not running."); return; }
        try
        {
            var reply = await _admin.RequestAsync("findclans", [FamilyArgs.Search(ClanSearch.Length > 0 ? ClanSearch : " ")], ReplyTimeout);
            if (!reply.Ok) { Status = reply.Error ?? ""; return; }
            ClanResults.Clear();
            foreach (var c in HoldingsList.From(reply).Clans) ClanResults.Add(c);
            PickedClan = ClanResults.FirstOrDefault();
            Status = Loc.F("{0} clan(s) found; pick one below.", ClanResults.Count);
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    private bool HasPicked() => PickedHero is not null && Loaded && !IsBusy;
    private bool IsMarried() => Shown?.Spouse is not null && Loaded && !IsBusy;
    private bool HasFather() => Shown?.Father is not null && Loaded && !IsBusy;
    private bool HasMother() => Shown?.Mother is not null && Loaded && !IsBusy;
    private bool HasPickedClan() => PickedClan is not null && Loaded && !IsBusy;

    [RelayCommand(CanExecute = nameof(HasPicked))]
    private Task Marry() => Family(FamilyArgs.Marry(PickedHero!.HeroId), Loc.F("Marry {0} and {1}?\n\nThe game decides which clan they live in, as at any wedding, and may move one of them there.", OriginalName, PickedHero!.Name));

    [RelayCommand(CanExecute = nameof(IsMarried))]
    private Task Divorce() => Family(FamilyArgs.Divorce(), Loc.F("Divorce {0} and {1}?\n\nEach is recorded as the other's former spouse. Neither changes clan.", OriginalName, Shown!.Spouse!.Name));

    [RelayCommand(CanExecute = nameof(HasPicked))]
    private Task SetFather() => Family(FamilyArgs.Father(PickedHero!.HeroId), Loc.F("Make {0} the father of {1}?", PickedHero!.Name, OriginalName));

    [RelayCommand(CanExecute = nameof(HasPicked))]
    private Task SetMother() => Family(FamilyArgs.Mother(PickedHero!.HeroId), Loc.F("Make {0} the mother of {1}?", PickedHero!.Name, OriginalName));

    [RelayCommand(CanExecute = nameof(HasFather))]
    private Task ClearFather() => Family(FamilyArgs.Father(null), Loc.F("Remove {0} as the father of {1}?", Shown!.Father!.Name, OriginalName));

    [RelayCommand(CanExecute = nameof(HasMother))]
    private Task ClearMother() => Family(FamilyArgs.Mother(null), Loc.F("Remove {0} as the mother of {1}?", Shown!.Mother!.Name, OriginalName));

    [RelayCommand(CanExecute = nameof(HasPicked))]
    private Task Adopt() => Family(FamilyArgs.Adopt(PickedHero!.HeroId), Loc.F("Let {0} adopt {1}?\n\n{0} becomes their {2}, and {1} moves into {0}'s clan. Leaving a party or a governorship works as it does when the game moves a hero after a wedding.",
        PickedHero!.Name, OriginalName, PickedHero!.Female ? Loc.T("mother") : Loc.T("father")));

    [RelayCommand(CanExecute = nameof(HasPickedClan))]
    private Task MoveToClan() => Family(FamilyArgs.Clan(PickedClan!.Id), Loc.F("Move {0} into clan {1}?\n\nThey leave their party or governorship the way the game moves a hero after a wedding.", OriginalName, PickedClan!.Name));

    /// <summary>Asks, sends one family action, and shows the hero as it is afterwards.</summary>
    private async Task Family(string[] action, string question)
    {
        if (!_serving()) { Status = Loc.T("The server is not running."); return; }
        if (HasPendingChanges && !Confirm(Loc.F("Apply or discard the changes to {0} first: a family change reloads them. Discard them?", OriginalName))) return;
        if (!Confirm(question)) return;
        IsBusy = true;
        try
        {
            var reply = await _admin.RequestAsync("family", new[] { Target }.Concat(action), ReplyTimeout);
            if (!reply.Ok) { Status = Loc.T("Nothing was changed: ") + reply.Error; return; }
            var detail = HeroDetail.From(reply);
            Show(detail);
            Status = string.Join("; ", detail.Changes) + ".";
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>Saved faces. Replaced in tests with one in a temp folder.</summary>
    public FaceLibrary Library { get; set; } = new();
    public ObservableCollection<SavedFace> LibraryFaces { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseLibraryFaceCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteLibraryFaceCommand))]
    private SavedFace? _libraryFace;

    /// <summary>The name "Save as" files the DNA in the box under.</summary>
    [ObservableProperty] private string _libraryName = "";

    /// <summary>Picks a file to export DNA to, or one to import from. Replaced in tests, which have no dialogs.</summary>
    public Func<string, string?> PickSaveFile { get; set; } = name =>
    {
        var d = new Microsoft.Win32.SaveFileDialog { Title = Loc.T("Export appearance DNA"), Filter = "Text (*.txt)|*.txt", FileName = name + ".txt" };
        return d.ShowDialog() == true ? d.FileName : null;
    };
    public Func<string?> PickOpenFile { get; set; } = () =>
    {
        var d = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("Import appearance DNA"), Filter = "Text (*.txt)|*.txt|All files (*.*)|*.*" };
        return d.ShowDialog() == true ? d.FileName : null;
    };
    public Func<string> ReadClipboard { get; set; } = () => System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "";
    public Action<string> WriteClipboard { get; set; } = System.Windows.Clipboard.SetText;

    /// <summary>True when Apply would send something; opening another hero asks before throwing it away.</summary>
    public bool HasPendingChanges => Loaded && (Tokens().Any() || IsNameChanged || IsBodyChanged);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetPerksCommand))]
    private int _perkCount;

    /// <summary>The hero on show is dead: death took their stats, so only name, gender and appearance can change, and Revive is offered.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReviveCommand))]
    private bool _isDead;

    public ObservableCollection<EditValue> Values { get; } = new();
    public ObservableCollection<EditValue> Attributes { get; } = new();
    public ObservableCollection<SkillEdit> Skills { get; } = new();
    public ObservableCollection<EditValue> Traits { get; } = new();
    public ICollectionView TraitsView { get; }

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _status = "Loading…";
    [ObservableProperty] private bool _showHiddenTraits;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetPerksCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReviveCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetPerksCommand))]
    private bool _loaded;

    partial void OnShowHiddenTraitsChanged(bool value) => TraitsView.Refresh();

    private bool CanReload() => !IsBusy;

    /// <summary>Opens a relative in this editor; Back returns to the hero before.</summary>
    [RelayCommand]
    private async Task OpenRelative(RelativeRow? row)
    {
        if (row is null || IsBusy || row.Target == Target) return;
        if (HasPendingChanges && !Confirm(Loc.F("Open {0}? The changes not yet applied to {1} are discarded.", row.Name, OriginalName))) return;
        _history.Push(Target);
        Target = row.Target;
        await Reload();
    }

    private bool CanBack() => _history.Count > 0;

    [RelayCommand(CanExecute = nameof(CanBack))]
    private async Task Back()
    {
        if (IsBusy || _history.Count == 0) return;
        if (HasPendingChanges && !Confirm(Loc.F("Go back? The changes not yet applied to {0} are discarded.", OriginalName))) return;
        Target = _history.Pop();
        await Reload();
    }

    [RelayCommand]
    private void CopyBody()
    {
        try { WriteClipboard(HeroBodies.Normalize(Body)); Status = Loc.T("Appearance DNA copied. Paste it into the game's character editor, or onto another hero here."); }
        catch (Exception ex) { Status = ex.Message; }
    }

    [RelayCommand]
    private void PasteBody()
    {
        string text;
        try { text = HeroBodies.Normalize(ReadClipboard()); }
        catch (Exception ex) { Status = ex.Message; return; }
        UseBody(text, Loc.T("the clipboard"));
    }

    [RelayCommand]
    private void ExportBody()
    {
        if (PickSaveFile(OriginalName.Length > 0 ? OriginalName : "appearance") is not { } path) return;
        try { System.IO.File.WriteAllText(path, HeroBodies.Normalize(Body) + Environment.NewLine); Status = Loc.T("Appearance DNA saved to ") + path; }
        catch (Exception ex) { Status = ex.Message; }
    }

    [RelayCommand]
    private void ImportBody()
    {
        if (PickOpenFile() is not { } path) return;
        try { UseBody(HeroBodies.Normalize(System.IO.File.ReadAllText(path)), System.IO.Path.GetFileName(path)); }
        catch (Exception ex) { Status = ex.Message; }
    }

    private bool CanCopyFaceFrom() => FaceSource is not null && !IsBusy;

    /// <summary>Reads another hero's DNA into the box. Nothing changes until Apply.</summary>
    [RelayCommand(CanExecute = nameof(CanCopyFaceFrom))]
    private async Task CopyFaceFrom()
    {
        if (FaceSource is not { } source) return;
        if (!_serving()) { Status = Loc.T("The server is not running."); return; }
        try
        {
            var reply = await _admin.RequestAsync("hero", [source.Target], ReplyTimeout);
            if (!reply.Ok) { Status = Loc.T("The server could not read that hero: ") + reply.Error; return; }
            var other = HeroDetail.From(reply);
            if (other.Body.Length == 0) { Status = Loc.T("The server did not send that hero's appearance."); return; }
            UseBody(HeroBodies.Normalize(other.Body), other.Name);
            if (other.Female != (GenderIndex == 1))
                Status += " " + Loc.F("{0} is {1}; set Gender to match, or the same DNA gives a different face.", other.Name, other.Female ? Loc.T("female") : Loc.T("male"));
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    /// <summary>Re-reads the library, keeping the selection where it still exists.</summary>
    public void LoadLibrary()
    {
        try { ShowLibrary(Library.Load()); }
        catch (Exception ex) { Status = Loc.T("The face library could not be read: ") + ex.Message; }
    }

    private void ShowLibrary(IReadOnlyList<SavedFace> faces, string? select = null)
    {
        var keep = select ?? LibraryFace?.Name;
        LibraryFaces.Clear();
        foreach (var f in faces) LibraryFaces.Add(f);
        LibraryFace = LibraryFaces.FirstOrDefault(f => string.Equals(f.Name, keep, StringComparison.CurrentCultureIgnoreCase));
    }

    private bool HasLibraryFace() => LibraryFace is not null;

    /// <summary>Puts a saved face in the DNA box. Nothing changes until Apply.</summary>
    [RelayCommand(CanExecute = nameof(HasLibraryFace))]
    private void UseLibraryFace()
    {
        if (LibraryFace is not { } face) return;
        UseBody(face.Body, face.Name);
        if (face.Female != (GenderIndex == 1))
            Status += " " + Loc.F("{0} was saved from a {1} hero; set Gender to match, or the same DNA gives a different face.", face.Name, face.Female ? Loc.T("female") : Loc.T("male"));
    }

    /// <summary>Saves the DNA in the box under <see cref="LibraryName"/>, asking before replacing a face of that name.</summary>
    [RelayCommand]
    private void SaveToLibrary()
    {
        var name = LibraryName.Trim();
        var body = HeroBodies.Normalize(Body);
        if (FaceLibrary.Problem(name, body) is { } problem) { Status = Loc.T("Not saved: ") + problem; return; }
        try
        {
            if (Library.Contains(name) && !Confirm(Loc.F("Replace the saved face \u201c{0}\u201d?", name))) return;
            ShowLibrary(Library.Save(name, body, GenderIndex == 1), name);
            LibraryName = "";
            Status = Loc.F("Saved \u201c{0}\u201d to the face library. Pick it under Library to put it on any hero.", name);
        }
        catch (Exception ex) { Status = Loc.T("The face was not saved: ") + ex.Message; }
    }

    [RelayCommand(CanExecute = nameof(HasLibraryFace))]
    private void DeleteLibraryFace()
    {
        if (LibraryFace is not { } face) return;
        if (!Confirm(Loc.F("Delete the saved face \u201c{0}\u201d from the library?", face.Name))) return;
        try
        {
            ShowLibrary(Library.Delete(face.Name));
            Status = Loc.F("Deleted \u201c{0}\u201d from the face library.", face.Name);
        }
        catch (Exception ex) { Status = Loc.T("The face was not deleted: ") + ex.Message; }
    }

    private void UseBody(string text, string from)
    {
        if (HeroBodies.Problem(text) is { } problem) { Status = Loc.F("Not used ({0}): {1}", from, problem); return; }
        Body = text;
        Status = Loc.F("Appearance from {0} is ready. Apply to change {1}'s face; the age in it is not used.", from, OriginalName);
    }

    [RelayCommand(CanExecute = nameof(CanReload))]
    private async Task Reload()
    {
        if (!_serving()) { Status = Loc.T("The server is not running."); return; }
        IsBusy = true;
        try
        {
            var reply = await _admin.RequestAsync("hero", [Target], ReplyTimeout);
            if (!reply.Ok) { Status = Loc.T("The server could not read this hero: ") + reply.Error; return; }
            Show(HeroDetail.From(reply));
            Status = Loc.T("Change any value, then Apply. Values are sent as new values; the server checks every one before changing any.");
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    private bool CanApply() => Loaded && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task Apply()
    {
        var outOfRange = Values.Concat(Attributes).Concat(Traits).Where(v => v.IsChanged && !v.InRange).Select(v => $"{v.Name} must be {v.Range}")
            .Concat(Skills.Where(s => s.IsChanged && (s.Value < 0 || s.Value > s.MaxValue || s.Focus < 0 || s.Focus > s.MaxFocus))
                .Select(s => $"{s.Name}: skill 0 to {s.MaxValue}, focus 0 to {s.MaxFocus}"))
            .ToList();
        if (outOfRange.Count > 0) { Status = Loc.T("Not sent: ") + string.Join("; ", outOfRange) + "."; return; }

        var rename = IsNameChanged ? Name.Trim() : null;
        if (rename is not null && HeroNames.Problem(rename) is { } problem) { Status = Loc.T("Not sent: ") + problem; return; }
        var body = IsBodyChanged ? HeroBodies.Normalize(Body) : null;
        if (body is not null && HeroBodies.Problem(body) is { } bodyProblem) { Status = Loc.T("Not sent: ") + bodyProblem; return; }
        var tokens = Tokens().ToList();
        if (tokens.Count == 0 && rename is null && body is null) { Status = Loc.T("Nothing has changed."); return; }
        if (!_serving()) { Status = Loc.T("The server is not running."); return; }
        IsBusy = true;
        try
        {
            // The values first: they are checked as a whole, so a refusal leaves the name and appearance as they were too.
            var applied = 0;
            var refused = new List<string>();
            if (tokens.Count > 0)
            {
                var reply = await _admin.RequestAsync("edit", new[] { Target }.Concat(tokens), ReplyTimeout);
                if (!reply.Ok) { Status = Loc.T("Nothing was changed: ") + reply.Error; return; }
                var detail = HeroDetail.From(reply);
                applied += detail.Changes.Count;
                refused.AddRange(detail.NotApplied);
                Show(detail);
            }
            if (rename is not null)
            {
                var reply = await _admin.RequestAsync("rename", [Target, HeroNames.Encode(rename)], ReplyTimeout);
                if (!reply.Ok)
                {
                    Name = rename;
                    Status = (applied > 0 ? Loc.F("Applied {0} change(s), but the name was not changed: ", applied) : Loc.T("The name was not changed: ")) + reply.Error;
                    return;
                }
                var detail = HeroDetail.From(reply);
                applied += detail.Changes.Count;
                Show(detail);
            }
            if (body is not null)
            {
                var reply = await _admin.RequestAsync("body", [Target, HeroBodies.Encode(body)], ReplyTimeout);
                if (!reply.Ok)
                {
                    Body = body;
                    Status = (applied > 0 ? Loc.F("Applied {0} change(s), but the appearance was not changed: ", applied) : Loc.T("The appearance was not changed: ")) + reply.Error;
                    return;
                }
                var detail = HeroDetail.From(reply);
                applied += detail.Changes.Count;
                Show(detail);
            }
            Status = refused.Count > 0
                ? Loc.F("Applied {0}; the game refused {1}: {2}", applied, refused.Count, string.Join("; ", refused))
                : Loc.F("Applied {0} change(s). The player's game picks them up through Coop's own sync.", applied);
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    partial void OnIsBusyChanged(bool value) => NotifyFamilyCommands();
    partial void OnLoadedChanged(bool value) => NotifyFamilyCommands();

    private void NotifyFamilyCommands()
    {
        foreach (var c in new IRelayCommand[] { MarryCommand, DivorceCommand, SetFatherCommand, SetMotherCommand, ClearFatherCommand, ClearMotherCommand, AdoptCommand, MoveToClanCommand })
            c.NotifyCanExecuteChanged();
    }

    private bool CanRevive() => Loaded && !IsBusy && IsDead;

    /// <summary>Brings the dead hero back in their home settlement, with skills rebuilt from their template.</summary>
    [RelayCommand(CanExecute = nameof(CanRevive))]
    private async Task Revive()
    {
        if (!_serving()) { Status = Loc.T("The server is not running."); return; }
        if (!Confirm(Loc.F("Bring {0} back to life?\n\nThey return in their home settlement with full health. Death took their skills, perks and equipment, "
                + "so these are rebuilt the way the game first made them; set them again here afterwards if needed.", OriginalName))) return;
        IsBusy = true;
        try
        {
            var reply = await _admin.RequestAsync("revive", [Target], ReplyTimeout);
            if (!reply.Ok) { Status = Loc.T("Not revived: ") + reply.Error; return; }
            var detail = HeroDetail.From(reply);
            Show(detail);
            Status = Loc.F("{0} is alive again. {1}.", detail.Name, string.Join("; ", detail.Changes));
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    private bool CanResetPerks() => Loaded && !IsBusy && PerkCount > 0;

    /// <summary>Takes away every perk the hero has chosen, so they can be chosen again in the game's character screen.</summary>
    [RelayCommand(CanExecute = nameof(CanResetPerks))]
    private async Task ResetPerks()
    {
        if (!_serving()) { Status = Loc.T("The server is not running."); return; }
        if (!Confirm($"Take away all {PerkCount} perk(s) {OriginalName} has chosen?\n\nSkills, focus and attributes stay as they are. "
                + "The perks can be chosen again in the game's character screen. Values changed here and not yet applied are discarded.")) return;
        IsBusy = true;
        try
        {
            var reply = await _admin.RequestAsync("resetperks", [Target], ReplyTimeout);
            if (!reply.Ok) { Status = Loc.T("No perk was taken away: ") + reply.Error; return; }
            var detail = HeroDetail.From(reply);
            Show(detail);
            Status = detail.NotApplied.Count > 0
                ? Loc.T("Perks were reset, but ") + string.Join("; ", detail.NotApplied) + "."
                : Loc.T("All perks were taken away. They can be chosen again in the game's character screen.");
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>The changed values, as edit tokens. Unchanged ones are left out, so the server leaves them alone.</summary>
    internal IEnumerable<string> Tokens()
    {
        foreach (var v in Values.Where(v => v.IsChanged))
            yield return v.Id switch
            {
                "age" => HeroEdits.Age(v.Value),
                "level" => HeroEdits.Level(v.Value),
                "gold" => HeroEdits.Gold(v.Value),
                "renown" => HeroEdits.ClanRenown(v.Value),
                "hp" => HeroEdits.HitPoints(v.Value),
                "unspent_attr" => HeroEdits.UnspentAttributePoints(v.Value),
                "unspent_focus" => HeroEdits.UnspentFocusPoints(v.Value),
                _ => throw new InvalidOperationException("Unknown value " + v.Id),
            };
        if (IsGenderChanged) yield return HeroEdits.Female(GenderIndex == 1);
        foreach (var a in Attributes.Where(a => a.IsChanged)) yield return HeroEdits.Attribute(a.Id, a.Value);
        foreach (var s in Skills)
        {
            if (s.Value != s.OriginalValue) yield return HeroEdits.Skill(s.Id, s.Value);
            if (s.Focus != s.OriginalFocus) yield return HeroEdits.Focus(s.Id, s.Focus);
        }
        foreach (var t in Traits.Where(t => t.IsChanged)) yield return HeroEdits.Trait(t.Id, t.Value);
    }

    internal void Show(HeroDetail hero)
    {
        Title = Loc.F("Edit {0}", hero.Name);
        Summary = Loc.F("{0}: level {1}, {2}, {3}, {4}. {5} perk(s).", hero.Name, hero.Level, (hero.Female ? "female" : "male"), hero.Age, hero.Culture, hero.Perks)
            + (hero.Alive ? "" : Loc.T(" Dead."))
            + (hero.Clan.Length > 0 ? Loc.F(" Clan {0}.", hero.Clan) : "")
            + (hero.SteamId.Length > 0 ? Loc.F(" Steam id {0}.", hero.SteamId) : "")
            + (hero.Busy.Length > 0 ? Loc.F(" Now {0}: edits are refused until that is over.", hero.Busy) : "");
        OriginalName = hero.Name;
        Name = hero.Name;
        PerkCount = hero.Perks;
        _originalGender = hero.Female ? 1 : 0;
        GenderIndex = _originalGender;
        _originalBody = HeroBodies.Normalize(hero.Body);
        Body = _originalBody;
        OnPropertyChanged(nameof(IsGenderChanged));
        OnPropertyChanged(nameof(IsBodyChanged));
        ShowFamily(hero);

        Shown = hero;
        IsDead = !hero.Alive;
        Values.Clear();
        Attributes.Clear();
        Skills.Clear();
        Traits.Clear();
        if (IsDead)
        {
            // Nothing else to show: the game threw it away on their death.
            TraitsView.Refresh();
            Loaded = true;
            return;
        }
        Values.Add(new EditValue("age", "Age", hero.Age, 0, hero.MaxAge));
        Values.Add(new EditValue("level", "Level", hero.Level, 1, hero.MaxLevel));
        Values.Add(new EditValue("gold", "Gold", hero.Gold, 0, int.MaxValue));
        if (hero.Renown >= 0)
            Values.Add(new EditValue("renown", hero.Clan.Length > 0 ? $"Clan renown ({hero.Clan})" : "Clan renown", hero.Renown, 0, hero.MaxRenown));
        Values.Add(new EditValue("hp", "Hit points", hero.HitPoints, 1, Math.Max(1, hero.MaxHitPoints)));
        Values.Add(new EditValue("unspent_attr", "Unspent attribute points", hero.UnspentAttributePoints, 0, 1000));
        Values.Add(new EditValue("unspent_focus", "Unspent focus points", hero.UnspentFocusPoints, 0, 1000));

        Attributes.Clear();
        foreach (var a in hero.Attributes) Attributes.Add(new EditValue(a.Id, a.Name, a.Value, 0, hero.MaxAttribute));

        var attributeNames = hero.Attributes.ToDictionary(a => a.Id, a => a.Name, StringComparer.Ordinal);
        Skills.Clear();
        foreach (var s in hero.Skills)
            Skills.Add(new SkillEdit(s, attributeNames.GetValueOrDefault(s.Attribute, s.Attribute), hero.MaxSkill, hero.MaxFocus));

        Traits.Clear();
        // Personality traits first (the ones the game shows); the rest are bookkeeping NPCs use and stay behind the toggle.
        foreach (var t in hero.Traits.OrderByDescending(t => t.Personality).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
            Traits.Add(new EditValue(t.Id, t.Name, t.Value, t.Min, t.Max, t.Personality ? "" : "hidden"));
        TraitsView.Refresh();
        Loaded = true;
    }

    private void ShowFamily(HeroDetail hero)
    {
        static void Fill(ObservableCollection<RelativeRow> list, IEnumerable<RelativeRow> rows)
        {
            list.Clear();
            foreach (var r in rows) list.Add(r);
        }
        RelativeRow Row(HeroRelative r, string relation) => new(r, relation, hero.Clan);
        Fill(Parents, new[] { (hero.Father, "Father"), (hero.Mother, "Mother") }.Where(p => p.Item1 is not null).Select(p => Row(p.Item1!, p.Item2)));
        Fill(Spouses, hero.Spouse is { } spouse ? [Row(spouse, "Spouse")] : []);
        Fill(Siblings, hero.Siblings.OrderByDescending(r => r.Age).Select(r => Row(r, r.Female ? "Sister" : "Brother")));
        Fill(Children, hero.Children.OrderByDescending(r => r.Age).Select(r => Row(r, r.Female ? "Daughter" : "Son")));

        var keep = FaceSource?.Target;
        var self = hero.HeroId.Length > 0 ? HeroTargets.Hero(hero.HeroId) : Target;
        FaceSources.Clear();
        foreach (var f in Parents.Concat(Spouses).Concat(Siblings).Concat(Children)
                     .Select(r => new FaceSource(r.Target, $"{r.Name} ({r.Relation.ToLowerInvariant()})"))
                     .Concat(_otherFaces)
                     .Where(f => f.Target != Target && f.Target != self)
                     .DistinctBy(f => f.Target))
            FaceSources.Add(f);
        FaceSource = FaceSources.FirstOrDefault(f => f.Target == keep);
    }
}
