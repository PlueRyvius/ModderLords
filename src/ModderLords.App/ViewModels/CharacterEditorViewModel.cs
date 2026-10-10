using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModderLords.Coop.Admin;

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

/// <summary>
/// The character editor: one player's hero, read from the running server and changed there. Only the values the host
/// actually changed are sent, as new values, and the server checks every one before applying any.
/// </summary>
public partial class CharacterEditorViewModel : ObservableObject
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(15);

    private readonly AdminClient _admin;
    private readonly Func<bool> _serving;

    public CharacterEditorViewModel(AdminClient admin, string steamId, string name, Func<bool> serving)
    {
        _admin = admin;
        _serving = serving;
        SteamId = steamId;
        _title = "Edit " + name;
        TraitsView = CollectionViewSource.GetDefaultView(Traits);
        TraitsView.Filter = o => ShowHiddenTraits || o is not EditValue t || t.Detail.Length == 0;
    }

    public string SteamId { get; }

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
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _loaded;

    partial void OnShowHiddenTraitsChanged(bool value) => TraitsView.Refresh();

    private bool CanReload() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanReload))]
    private async Task Reload()
    {
        if (!_serving()) { Status = "The server is not running."; return; }
        IsBusy = true;
        try
        {
            var reply = await _admin.RequestAsync("hero", [SteamId], ReplyTimeout);
            if (!reply.Ok) { Status = "The server could not read this hero: " + reply.Error; return; }
            Show(HeroDetail.From(reply));
            Status = "Change any value, then Apply. Values are sent as new values; the server checks every one before changing any.";
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
        if (outOfRange.Count > 0) { Status = "Not sent: " + string.Join("; ", outOfRange) + "."; return; }

        var tokens = Tokens().ToList();
        if (tokens.Count == 0) { Status = "Nothing has changed."; return; }
        if (!_serving()) { Status = "The server is not running."; return; }
        IsBusy = true;
        try
        {
            var reply = await _admin.RequestAsync("edit", new[] { SteamId }.Concat(tokens), ReplyTimeout);
            if (!reply.Ok) { Status = "Nothing was changed: " + reply.Error; return; }
            var detail = HeroDetail.From(reply);
            Show(detail);
            Status = detail.NotApplied.Count > 0
                ? $"Applied {detail.Changes.Count}; the game refused {detail.NotApplied.Count}: {string.Join("; ", detail.NotApplied)}"
                : $"Applied {detail.Changes.Count} change(s). The player's game picks them up through Coop's own sync.";
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
                "hp" => HeroEdits.HitPoints(v.Value),
                "unspent_attr" => HeroEdits.UnspentAttributePoints(v.Value),
                "unspent_focus" => HeroEdits.UnspentFocusPoints(v.Value),
                _ => throw new InvalidOperationException("Unknown value " + v.Id),
            };
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
        Title = $"Edit {hero.Name}";
        Summary = $"{hero.Name}: level {hero.Level}, {(hero.Female ? "female" : "male")}, {hero.Age}, {hero.Culture}. {hero.Perks} perk(s). Steam id {hero.SteamId}."
            + (hero.Busy.Length > 0 ? $" Now {hero.Busy}: edits are refused until that is over." : "");

        Values.Clear();
        Values.Add(new EditValue("age", "Age", hero.Age, 0, hero.MaxAge));
        Values.Add(new EditValue("level", "Level", hero.Level, 1, hero.MaxLevel));
        Values.Add(new EditValue("gold", "Gold", hero.Gold, 0, int.MaxValue));
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
}
