using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModderLords.Core.Live;

namespace ModderLords.App.ViewModels;

/// <summary>
/// One setting in the editor. Value is always the wire text; BoolValue is a typed view over it for the CheckBox.
/// Original = what the server last reported (or the cached copy); OverrideValue = the host override stored for
/// the profile. Live, edits are measured against the server value; offline, against the override (or the cache).
/// </summary>
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

    /// <summary>What the server last reported (or the cached copy of it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(Baseline))]
    private string _original = "";

    /// <summary>The host override stored for this profile, re-applied at every launch; null = none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(Baseline), nameof(HasOverride), nameof(OverrideTip))]
    private string? _overrideValue;

    /// <summary>Offline: edits are compared against the override (else the cache); live: against the server value.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(Baseline))]
    private bool _offlineMode;

    public string Baseline => OfflineMode ? OverrideValue ?? Original : Original;
    public bool IsDirty => Editable && !string.Equals(Value, Baseline, StringComparison.Ordinal);
    public bool HasOverride => OverrideValue is not null;
    public string OverrideTip => OverrideValue is null ? "" : $"Host override {OverrideValue}, re-applied at every launch. Server/cached value: {Original}";

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

    public LivePropertyVm(LiveSettingsProperty p, string? overrideValue, bool offline)
    {
        Id = p.Id;
        DisplayName = string.IsNullOrWhiteSpace(p.DisplayName) ? p.Id : p.DisplayName;
        Hint = p.Hint;
        Kind = p.Kind;
        _offlineMode = offline;
        _overrideValue = overrideValue;
        Update(p, overrideValue, offline);
        Value = Baseline;
    }

    /// <summary>A fresh description (server or cache): keep an unsaved edit, otherwise follow the baseline.</summary>
    public void Update(LiveSettingsProperty p, string? overrideValue, bool offline)
    {
        Kind = p.Kind; Editable = p.Editable; RequireRestart = p.RequireRestart;
        Choices = p.Choices ?? new List<string>();
        Min = p.Min; Max = p.Max;
        var wasDirty = IsDirty;
        OfflineMode = offline;
        OverrideValue = overrideValue;
        Original = p.Value ?? "";
        if (!wasDirty) Value = Baseline;
        OnPropertyChanged(nameof(IsBool)); OnPropertyChanged(nameof(IsEnum)); OnPropertyChanged(nameof(IsText)); OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(RangeText)); OnPropertyChanged(nameof(KindText)); OnPropertyChanged(nameof(Choices)); OnPropertyChanged(nameof(ToolTip));
    }

    public void Revert() => Value = Baseline;
}

public sealed class LiveGroupVm
{
    public string Name { get; }
    public ObservableCollection<LivePropertyVm> Properties { get; } = new();
    public LiveGroupVm(string name) => Name = name;
}

/// <summary>One settings object (one mod's MCM settings or one plain settings class) with its groups.</summary>
public partial class LiveObjectVm : ObservableObject
{
    public string SettingsId { get; }
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private int _overrideCount;
    public ObservableCollection<LiveGroupVm> Groups { get; } = new();
    public IEnumerable<LivePropertyVm> AllProperties => Groups.SelectMany(g => g.Properties);
    public int DirtyCount => AllProperties.Count(p => p.IsDirty);
    public bool IsStatic => SettingsId.StartsWith("static:", StringComparison.Ordinal);

    public LiveObjectVm(string settingsId) => SettingsId = settingsId;

    public void Update(LiveSettingsObject o, SettingsOverrides overrides, bool offline)
    {
        DisplayName = string.IsNullOrWhiteSpace(o.DisplayName) ? o.SettingsId : o.DisplayName;
        var editable = o.Groups.Sum(g => g.Properties.Count(p => p.Editable));
        Subtitle = $"{o.PropertyCount} setting(s), {editable} editable" + (IsStatic ? " · mod's own settings" : " · MCM") + (o.Folder is null ? "" : $" · {o.Folder}");

        // Rebuild groups in place so the selection and any unsent edits survive a rewrite of settings.json.
        var existing = AllProperties.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var newGroups = new List<LiveGroupVm>();
        foreach (var g in o.Groups)
        {
            var gv = new LiveGroupVm(string.IsNullOrWhiteSpace(g.Name) ? "General" : g.Name);
            foreach (var p in g.Properties)
            {
                var ov = overrides.Get(o.SettingsId, p.Id);
                if (existing.TryGetValue(p.Id, out var pv)) pv.Update(p, ov, offline);
                else pv = new LivePropertyVm(p, ov, offline);
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
        RefreshOverrideCount();
        OnPropertyChanged(nameof(DirtyCount));
    }

    public void RefreshOverrideCount() => OverrideCount = AllProperties.Count(p => p.HasOverride);
}

/// <summary>
/// The Mod settings tab. Live (server running, Settings sync on): mirrors the server's settings from the live
/// directory and sends edits back. Offline: shows the last description the server reported (cache) and edits the
/// host overrides that the launcher stages at the next launch. Everything UI-facing runs on the dispatcher.
/// </summary>
public partial class LiveSettingsViewModel : ObservableObject
{
    private LiveSettingsClient? _client;
    private LiveSettingsDocument? _last;
    private string _profileName = "default";
    private SettingsOverrides _overrides = new();
    private string? _liveDir;
    private bool _ackLogged;

    public ObservableCollection<LiveObjectVm> Objects { get; } = new();

    [ObservableProperty] private LiveObjectVm? _selectedObject;
    [ObservableProperty] private string _statusText = "Server not running.";
    [ObservableProperty] private bool _isLive;
    /// <summary>Tab gate: live, or something cached/overridden to edit offline.</summary>
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private string _gateHint = "Available once the server has run with Settings sync on (Server tab).";
    [ObservableProperty] private string _modeText = "Offline";
    [ObservableProperty] private string _applyReport = "";
    [ObservableProperty] private bool _isBusy;

    /// <summary>Console sink for tool lines (set by the main view model).</summary>
    public Action<string>? Log { get; set; }

    // ---- lifecycle ----------------------------------------------------------------------------------------

    /// <summary>Profile chosen in the launcher (not while the server runs): load its cache and overrides for offline editing.</summary>
    public void OnProfileSelected(string profileName)
    {
        if (IsLive) return;
        _profileName = profileName;
        _overrides = SettingsOverridesStore.Load(profileName);
        Objects.Clear();
        SelectedObject = null;
        ApplyReport = "";
        var cache = SettingsOverridesStore.LoadCache(profileName);
        if (cache is not null) Merge(cache, offline: true);
        UpdateOfflineState();
    }

    private void UpdateOfflineState()
    {
        IsLive = false;
        ModeText = "Offline";
        var has = Objects.Count > 0;
        IsEnabled = has || !_overrides.IsEmpty;
        if (has)
        {
            StatusText = $"Server not running: editing host overrides for '{_profileName}'; they apply at the next launch. Values shown are from {(_last?.WrittenAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "the last run")}."
                         + (_overrides.IsEmpty ? "" : $" {_overrides.Count} override(s) stored.");
            GateHint = "Editing overrides offline.";
        }
        else
        {
            StatusText = "Server not running. Launch once with Settings sync on and the settings it reports become editable here, live and offline.";
            GateHint = StatusText;
        }
    }

    public void OnLaunched(string? liveDir, bool settingsSync, string profileName)
    {
        Detach();
        _profileName = profileName;
        _overrides = SettingsOverridesStore.Load(profileName);
        _ackLogged = false;
        ApplyReport = "";
        if (!settingsSync || liveDir is null)
        {
            IsLive = false;
            ModeText = "Offline";
            IsEnabled = Objects.Count > 0 || !_overrides.IsEmpty;
            StatusText = "Settings sync is off in this profile; turn it on in the Server tab and restart the server to edit mod settings live.";
            GateHint = StatusText;
            return;
        }
        _liveDir = liveDir;
        IsEnabled = true;
        IsLive = true;
        ModeText = "Live";
        StatusText = "Waiting for the server's settings… (they appear once the ModderLords.Compat module has loaded, usually within a minute of launch)";
        GateHint = "Live while the server runs.";
        foreach (var p in Objects.SelectMany(o => o.AllProperties)) p.OfflineMode = false;
        _client = new LiveSettingsClient(liveDir);
        _client.Changed += doc => Application.Current?.Dispatcher.BeginInvoke(() => OnDocument(doc));
        var initial = _client.Read();
        if (initial is not null) OnDocument(initial);
    }

    public void OnStopped()
    {
        Detach();
        foreach (var p in Objects.SelectMany(o => o.AllProperties)) p.OfflineMode = true;
        UpdateOfflineState();
        ApplyCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
        ClearOverridesCommand.NotifyCanExecuteChanged();
    }

    private void Detach()
    {
        _client?.Dispose();
        _client = null;
    }

    // ---- documents ----------------------------------------------------------------------------------------

    private void OnDocument(LiveSettingsDocument? doc)
    {
        if (doc is null) return;
        Merge(doc, offline: false);
        try { SettingsOverridesStore.SaveCache(_profileName, doc); } catch (Exception ex) { Log?.Invoke("mod settings: cache write failed: " + ex.Message); }
        IsLive = _client is not null;
        ModeText = "Live";
        StatusText = $"Live, {Objects.Count} settings object(s), updated {doc.WrittenAt.ToLocalTime():HH:mm:ss}" + (_overrides.IsEmpty ? "" : $"; {_overrides.Count} override(s) stored for this profile");
        LogOverridesAckOnce();
        ApplyCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
        ClearOverridesCommand.NotifyCanExecuteChanged();
    }

    private void Merge(LiveSettingsDocument doc, bool offline)
    {
        _last = doc;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in doc.Objects)
        {
            seen.Add(o.SettingsId);
            var vm = Objects.FirstOrDefault(v => v.SettingsId == o.SettingsId);
            if (vm is null) { vm = new LiveObjectVm(o.SettingsId); Objects.Add(vm); }
            vm.Update(o, _overrides, offline);
        }
        for (var i = Objects.Count - 1; i >= 0; i--)
            if (!seen.Contains(Objects[i].SettingsId)) Objects.RemoveAt(i);
        SelectedObject ??= Objects.FirstOrDefault();
    }

    private void LogOverridesAckOnce()
    {
        if (_ackLogged || _liveDir is null || _overrides.IsEmpty) return;
        var ack = SettingsOverridesStore.ReadAck(_liveDir);
        if (ack is null || ack.Applied.Count == 0) return;
        _ackLogged = true;
        foreach (var a in ack.Applied) Log?.Invoke($"override {a.SettingsId}: {a.Report}; {a.Persisted}");
        Log?.Invoke($"{ack.Applied.Count} override(s) applied at start" + (ack.Pending.Count > 0 ? $", {ack.Pending.Count} pending (settings object not created yet): {string.Join(", ", ack.Pending)}" : ""));
    }

    partial void OnSelectedObjectChanged(LiveObjectVm? value)
    {
        ApplyReport = "";
        ApplyCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
        ClearOverridesCommand.NotifyCanExecuteChanged();
    }

    // ---- commands -----------------------------------------------------------------------------------------

    private bool CanApply() => !IsBusy && SelectedObject is not null && (IsLive || IsEnabled);
    private bool CanClear() => !IsBusy && SelectedObject is not null && _overrides.Objects.ContainsKey(SelectedObject.SettingsId);

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task Apply()
    {
        var obj = SelectedObject;
        if (obj is null) return;
        var dirty = obj.AllProperties.Where(p => p.IsDirty).ToList();
        if (dirty.Count == 0) { ApplyReport = "Nothing changed."; return; }
        var bad = dirty.Where(p => p.HasError).ToList();
        if (bad.Count > 0) { ApplyReport = "Fix first: " + string.Join(", ", bad.Select(p => $"{p.DisplayName} ({p.Error})")); return; }
        var values = dirty.ToDictionary(p => p.Id, p => p.Value.Trim(), StringComparer.Ordinal);

        if (_client is null)
        {
            // Offline: the override is the deliverable; the module applies it at the next launch.
            StoreOverrides(obj, values);
            ApplyReport = $"Saved {values.Count} override(s) for '{_profileName}'; applied when the server next starts.";
            Log?.Invoke($"mod settings: {values.Count} override(s) stored for {obj.SettingsId} (offline)");
            UpdateOfflineState();
            return;
        }

        IsBusy = true;
        ApplyCommand.NotifyCanExecuteChanged();
        ApplyReport = $"Sending {dirty.Count} change(s)…";
        try
        {
            var ack = await _client.ApplyAsync(obj.SettingsId, values, TimeSpan.FromSeconds(15));
            if (ack is null)
            {
                ApplyReport = "No answer from the server within 15 s. Is the ModderLords.Compat module loaded? (Console: look for [ModderLords.Compat] lines.)";
                Log?.Invoke($"live apply {obj.SettingsId}: no ack");
                return;
            }
            var restart = dirty.Any(p => p.RequireRestart) ? " Some of these are marked restart-required by the mod." : "";
            ApplyReport = (ack.Ok ? "Applied: " : "Failed: ") + ack.Report + (ack.Persisted is null ? "" : "; " + ack.Persisted) + ". Players receive it on the next sync tick; also stored as a host override for this profile." + restart;
            Log?.Invoke($"live apply {obj.SettingsId}: {(ack.Ok ? "ok" : "FAILED")}, {ack.Report}" + (ack.Persisted is null ? "" : $", {ack.Persisted}"));
            if (ack.Ok)
            {
                foreach (var p in dirty) p.Original = p.Value.Trim();
                StoreOverrides(obj, values);
            }
        }
        catch (Exception ex) { ApplyReport = ex.Message; }
        finally
        {
            IsBusy = false;
            ApplyCommand.NotifyCanExecuteChanged();
            RevertCommand.NotifyCanExecuteChanged();
            ClearOverridesCommand.NotifyCanExecuteChanged();
        }
    }

    private void StoreOverrides(LiveObjectVm obj, Dictionary<string, string> values)
    {
        _overrides.Set(obj.SettingsId, values);
        try { SettingsOverridesStore.Save(_profileName, _overrides); }
        catch (Exception ex) { ApplyReport = "Override not saved: " + ex.Message; return; }
        foreach (var p in obj.AllProperties)
            if (values.TryGetValue(p.Id, out var v)) p.OverrideValue = v;
        obj.RefreshOverrideCount();
        ClearOverridesCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Revert()
    {
        if (SelectedObject is null) return;
        foreach (var p in SelectedObject.AllProperties) p.Revert();
        ApplyReport = "";
    }

    /// <summary>Forgets the stored overrides for the selected object. Live: the server keeps its current values; they are just no longer re-applied at launch.</summary>
    [RelayCommand(CanExecute = nameof(CanClear))]
    private void ClearOverrides()
    {
        var obj = SelectedObject;
        if (obj is null) return;
        _overrides.Clear(obj.SettingsId);
        try { SettingsOverridesStore.Save(_profileName, _overrides); }
        catch (Exception ex) { ApplyReport = "Overrides not cleared: " + ex.Message; return; }
        foreach (var p in obj.AllProperties)
        {
            p.OverrideValue = null;
            if (!IsLive) p.Value = p.Original;
        }
        obj.RefreshOverrideCount();
        ApplyReport = IsLive
            ? "Overrides cleared for this object; the server keeps its current values, they are just not re-applied at the next launch."
            : "Overrides cleared for this object; values shown are the last ones the server reported.";
        Log?.Invoke($"mod settings: overrides cleared for {obj.SettingsId}");
        if (!IsLive) UpdateOfflineState();
        ClearOverridesCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Reload()
    {
        if (_client is not null)
        {
            var doc = _client.Read();
            if (doc is not null) OnDocument(doc);
            return;
        }
        OnProfileSelected(_profileName);
    }
}
