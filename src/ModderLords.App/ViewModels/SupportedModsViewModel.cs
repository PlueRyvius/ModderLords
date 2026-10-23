using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using ModderLords.App.Localization;
using ModderLords.Core.Compat;

namespace ModderLords.App.ViewModels;

/// <summary>One mod in the Supported mods tab: collapsed to its name and status, expanded to what ModderLords does for it.</summary>
public partial class SupportedModRow : ObservableObject
{
    public SupportedModRow(SupportedMod mod) => Mod = mod;

    public SupportedMod Mod { get; }
    public string Name => Mod.Name;
    public string Summary => Mod.Summary;
    public IReadOnlyList<string> Details => Mod.Details;
    public IReadOnlyList<string> Setup => Mod.Setup;
    public IReadOnlyList<string> Limits => Mod.Limits;
    public bool HasDetails => Mod.Details.Count > 0;
    public bool HasSetup => Mod.Setup.Count > 0;
    public bool HasLimits => Mod.Limits.Count > 0;
    public string StatusText => Mod.Status switch
    {
        "Works" => Loc.T("Works"),
        "Settings" => Loc.T("Works with Settings sync"),
        "Partial" => Loc.T("Partly works"),
        "Broken" => Loc.T("Does not work"),
        _ => Mod.Status,
    };
    public string Ids => Loc.T("Module id: ") + string.Join(", ", Mod.ModuleIds);
    public string Footer => string.Join("  ·  ", new[]
    {
        Mod.NeedsCompat ? Loc.T("Players need ModderLords.Compat (Settings sync on)") : null,
        Mod.Since.Length > 0 ? Loc.T("Since ModderLords ") + Mod.Since : null,
    }.Where(s => s is not null));
    public bool HasFooter => Footer.Length > 0;

    /// <summary>Any of its module ids is installed on this PC.</summary>
    [ObservableProperty] private bool _installed;
}

/// <summary>One "for every mod" point, split at its first colon into a bold lead and the explanation.</summary>
public sealed record GeneralPoint(string Lead, string Text)
{
    public static GeneralPoint From(string line)
    {
        var at = line.IndexOf(": ", StringComparison.Ordinal);
        return at > 0 && at < 60 ? new GeneralPoint(line[..at], line[(at + 2)..]) : new GeneralPoint("", line);
    }
}

/// <summary>
/// The Supported mods tab: the bundled <see cref="SupportedModList"/>, installed mods first, kept in step with the Mods
/// tab's list as it is rescanned.
/// </summary>
public sealed class SupportedModsViewModel
{
    private readonly ObservableCollection<ModRow> _installed;

    public SupportedModsViewModel(ObservableCollection<ModRow> installed) : this(installed, Load()) { }

    internal SupportedModsViewModel(ObservableCollection<ModRow> installed, SupportedModList list)
    {
        _installed = installed;
        General = list.General.Select(GeneralPoint.From).ToList();
        foreach (var m in list.Mods) Rows.Add(new SupportedModRow(m));
        Refresh();
        installed.CollectionChanged += OnInstalledChanged;
    }

    public IReadOnlyList<GeneralPoint> General { get; }
    public ObservableCollection<SupportedModRow> Rows { get; } = new();

    private static SupportedModList Load()
    {
        try { return SupportedModList.Bundled(); }
        catch (Exception) { return new SupportedModList(); }
    }

    private void OnInstalledChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Move) Refresh();
    }

    /// <summary>Marks what is installed and puts those first, keeping the list's own order otherwise.</summary>
    internal void Refresh()
    {
        var ids = new HashSet<string>(_installed.Where(r => !r.IsMissing).Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows) row.Installed = row.Mod.ModuleIds.Any(ids.Contains);
        var ordered = Rows.Select((r, i) => (r, i)).OrderByDescending(x => x.r.Installed).ThenBy(x => x.i).Select(x => x.r).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var at = Rows.IndexOf(ordered[i]);
            if (at != i) Rows.Move(at, i);
        }
    }
}
