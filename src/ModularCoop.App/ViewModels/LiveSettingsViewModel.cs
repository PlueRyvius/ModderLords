using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModularCoop.Core.Live;

namespace ModularCoop.App.ViewModels;

/// <summary>One MCM property in the editor. Value is always the wire text; BoolValue is a typed view over it for the CheckBox.</summary>
public partial class LivePropertyVm : ObservableObject
{
    public string Id { get; }
    public string DisplayName { get; }
    public string? Hint { get; }
    public string Kind { get; private set; }
    public bool Editable { get; private set; }
    public bool RequireRestart { get; private set; }
    public List<string> Choices { get; private set; } = new();
    public double? Min { get; private set; }
    public double? Max { get; private set; }

    public bool IsBool => Editable && Kind == "bool";
    public bool IsEnum => Editable && Kind == "enum";
    public bool IsText => Editable && !IsBool && !IsEnum;
    public bool IsReadOnly => !Editable;
    public string RangeText => Min is not null && Max is not null ? $"{Min:0.###} to {Max:0.###}" : "";
    public string KindText => Editable ? Kind : Kind + " (not editable here)";
    public string ToolTip => (string.IsNullOrWhiteSpace(Hint) ? Id : Hint + "\n(" + Id + ")") + (RequireRestart ? "\nThe mod says this one needs a restart to take effect." : "");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(BoolValue), nameof(Error), nameof(HasError))]
    private string _value = "";

    /// <summary>What the server last reported; Revert goes back to it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _original = "";

    public bool IsDirty => Editable && !string.Equals(Value, Original, StringComparison.Ordinal);

    public bool BoolValue
    {
        get => string.Equals(Value, "true", StringComparison.OrdinalIgnoreCase);
        set => Value = value ? "true" : "false";
    }

    public string? Error
    {
        get
        {
            if (!Editable || !IsDirty) return null;
            switch (Kind)
            {
                case "int":
                    if (!long.TryParse(Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return "whole number expected";
                    if (Min is not null && l < Min) return $"below {Min:0}";
                    if (Max is not null && l > Max) return $"above {Max:0}";
                    return null;
                case "float":
                    if (!double.TryParse(Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return "number expected (use a dot for decimals)";
                    if (Min is not null && d < Min) return $"below {Min:0.###}";
                    if (Max is not null && d > Max) return $"above {Max:0.###}";
                    return null;
                case "enum":
                    return Choices.Contains(Value) ? null : "not one of the choices";
                default:
                    return null;
            }
        }
    }
    public bool HasError => Error is not null;

    public LivePropertyVm(LiveSettingsProperty p)
    {
        Id = p.Id;
        DisplayName = string.IsNullOrWhiteSpace(p.DisplayName) ? p.Id : p.DisplayName;
        Hint = p.Hint;
        Kind = p.Kind;
        Update(p);
        Value = Original;
    }

    /// <summary>A fresh description from the server: keep an unsaved edit, otherwise follow the server's value.</summary>
    public void Update(LiveSettingsProperty p)
    {
        Kind = p.Kind; Editable = p.Editable; RequireRestart = p.RequireRestart;
        Choices = p.Choices ?? new List<string>();
        Min = p.Min; Max = p.Max;
        var wasDirty = IsDirty;
        Original = p.Value ?? "";
        if (!wasDirty) Value = Original;
        OnPropertyChanged(nameof(IsBool)); OnPropertyChanged(nameof(IsEnum)); OnPropertyChanged(nameof(IsText)); OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(RangeText)); OnPropertyChanged(nameof(KindText)); OnPropertyChanged(nameof(Choices)); OnPropertyChanged(nameof(ToolTip));
    }

    public void Revert() => Value = Original;
}

public sealed class LiveGroupVm
{
    public string Name { get; }
    public ObservableCollection<LivePropertyVm> Properties { get; } = new();
    public LiveGroupVm(string name) => Name = name;
}

/// <summary>One MCM settings object (one mod, usually) with its groups.</summary>
public partial class LiveObjectVm : ObservableObject
{
    public string SettingsId { get; }
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _subtitle = "";
    public ObservableCollection<LiveGroupVm> Groups { get; } = new();
    public IEnumerable<LivePropertyVm> AllProperties => Groups.SelectMany(g => g.Properties);
    public int DirtyCount => AllProperties.Count(p => p.IsDirty);

    public LiveObjectVm(string settingsId) => SettingsId = settingsId;

    public void Update(LiveSettingsObject o)
    {
        DisplayName = string.IsNullOrWhiteSpace(o.DisplayName) ? o.SettingsId : o.DisplayName;
        var editable = o.Groups.Sum(g => g.Properties.Count(p => p.Editable));
        Subtitle = $"{o.PropertyCount} setting(s), {editable} editable" + (o.Folder is null ? "" : $" · {o.Folder}");

        // Rebuild groups in place so the selection and any dirty edits survive a rewrite of settings.json.
        var existing = AllProperties.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var newGroups = new List<LiveGroupVm>();
        foreach (var g in o.Groups)
        {
            var gv = new LiveGroupVm(string.IsNullOrWhiteSpace(g.Name) ? "General" : g.Name);
            foreach (var p in g.Properties)
            {
                if (existing.TryGetValue(p.Id, out var pv)) pv.Update(p);
                else pv = new LivePropertyVm(p);
                gv.Properties.Add(pv);
            }
            newGroups.Add(gv);
        }
        var same = newGroups.Count == Groups.Count && newGroups.Zip(Groups).All(t => t.First.Name == t.Second.Name && t.First.Properties.SequenceEqual(t.Second.Properties));
        if (!same)
        {
            Groups.Clear();
            foreach (var g in newGroups) Groups.Add(g);
        }
        OnPropertyChanged(nameof(DirtyCount));
    }
}

/// <summary>
/// The Mod settings tab: mirrors the running server's MCM settings from the live directory and sends edits back.
/// Attach/Detach follow the engine's lifetime; everything UI-facing runs on the dispatcher.
/// </summary>
public partial class LiveSettingsViewModel : ObservableObject
{
    private LiveSettingsClient? _client;
    private LiveSettingsDocument? _last;

    public ObservableCollection<LiveObjectVm> Objects { get; } = new();

    [ObservableProperty] private LiveObjectVm? _selectedObject;
    [ObservableProperty] private string _statusText = "Server not running.";
    [ObservableProperty] private bool _isLive;
    /// <summary>Tab gate: engine running with Settings sync on.</summary>
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private string _gateHint = "Available while the server runs with Settings sync on (Server tab).";
    [ObservableProperty] private string _applyReport = "";
    [ObservableProperty] private bool _isBusy;

    /// <summary>Console sink for tool lines (set by the main view model).</summary>
    public Action<string>? Log { get; set; }

    public void OnLaunched(string? liveDir, bool settingsSync)
    {
        Detach();
        ApplyReport = "";
        if (!settingsSync || liveDir is null)
        {
            IsEnabled = false;
            IsLive = false;
            StatusText = "Settings sync is off in this profile; turn it on in the Server tab and restart the server to edit mod settings live.";
            GateHint = StatusText;
            return;
        }
        IsEnabled = true;
        IsLive = false;
        StatusText = "Waiting for the server's settings… (they appear once the ModularCoop.Compat module has loaded, usually within a minute of launch)";
        GateHint = "Live while the server runs.";
        _client = new LiveSettingsClient(liveDir);
        _client.Changed += doc => Application.Current?.Dispatcher.BeginInvoke(() => OnDocument(doc));
        var initial = _client.Read();
        if (initial is not null) OnDocument(initial);
    }

    public void OnStopped()
    {
        Detach();
        IsEnabled = false;
        IsLive = false;
        StatusText = Objects.Count > 0 ? "Server stopped; showing the last values it reported." : "Server not running.";
        GateHint = "Available while the server runs with Settings sync on (Server tab).";
    }

    private void Detach()
    {
        _client?.Dispose();
        _client = null;
    }

    private void OnDocument(LiveSettingsDocument? doc)
    {
        if (doc is null) return;
        _last = doc;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in doc.Objects)
        {
            seen.Add(o.SettingsId);
            var vm = Objects.FirstOrDefault(v => v.SettingsId == o.SettingsId);
            if (vm is null) { vm = new LiveObjectVm(o.SettingsId); Objects.Add(vm); }
            vm.Update(o);
        }
        for (var i = Objects.Count - 1; i >= 0; i--)
            if (!seen.Contains(Objects[i].SettingsId)) Objects.RemoveAt(i);
        SelectedObject ??= Objects.FirstOrDefault();
        IsLive = _client is not null;
        StatusText = (IsLive ? "Live" : "Last seen") + $", {Objects.Count} settings object(s), updated {doc.WrittenAt.ToLocalTime():HH:mm:ss}";
        ApplyCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedObjectChanged(LiveObjectVm? value)
    {
        ApplyReport = "";
        ApplyCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
    }

    private bool CanApply() => IsLive && !IsBusy && SelectedObject is not null;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task Apply()
    {
        var obj = SelectedObject;
        if (obj is null || _client is null) return;
        var dirty = obj.AllProperties.Where(p => p.IsDirty).ToList();
        if (dirty.Count == 0) { ApplyReport = "Nothing changed."; return; }
        var bad = dirty.Where(p => p.HasError).ToList();
        if (bad.Count > 0) { ApplyReport = "Fix first: " + string.Join(", ", bad.Select(p => $"{p.DisplayName} ({p.Error})")); return; }

        IsBusy = true;
        ApplyCommand.NotifyCanExecuteChanged();
        ApplyReport = $"Sending {dirty.Count} change(s)…";
        try
        {
            var values = dirty.ToDictionary(p => p.Id, p => p.Value.Trim(), StringComparer.Ordinal);
            var ack = await _client.ApplyAsync(obj.SettingsId, values, TimeSpan.FromSeconds(15));
            if (ack is null)
            {
                ApplyReport = "No answer from the server within 15 s. Is the ModularCoop.Compat module loaded? (Console: look for [ModularCoop.Compat] lines.)";
                Log?.Invoke($"live apply {obj.SettingsId}: no ack");
                return;
            }
            var restart = dirty.Any(p => p.RequireRestart) ? " Some of these are marked restart-required by the mod." : "";
            ApplyReport = (ack.Ok ? "Applied: " : "Failed: ") + ack.Report + (ack.Persisted is null ? "" : "; " + ack.Persisted) + ". Players receive it on the next sync tick." + restart;
            Log?.Invoke($"live apply {obj.SettingsId}: {(ack.Ok ? "ok" : "FAILED")}, {ack.Report}" + (ack.Persisted is null ? "" : $", {ack.Persisted}"));
            // The module rewrites settings.json right after the apply; until it lands, treat what we sent as the new baseline.
            if (ack.Ok) foreach (var p in dirty) p.Original = p.Value.Trim();
            obj.Update(_last is null ? new LiveSettingsObject { SettingsId = obj.SettingsId, DisplayName = obj.DisplayName } : _last.Objects.First(o => o.SettingsId == obj.SettingsId));
        }
        catch (Exception ex) { ApplyReport = ex.Message; }
        finally
        {
            IsBusy = false;
            ApplyCommand.NotifyCanExecuteChanged();
            RevertCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Revert()
    {
        if (SelectedObject is null) return;
        foreach (var p in SelectedObject.AllProperties) p.Revert();
        ApplyReport = "";
    }

    [RelayCommand]
    private void Reload()
    {
        var doc = _client?.Read();
        if (doc is null) { if (_client is null) StatusText = "Server not running."; return; }
        OnDocument(doc);
    }
}
