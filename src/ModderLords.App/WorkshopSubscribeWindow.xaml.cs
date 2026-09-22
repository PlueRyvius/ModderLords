using System.Diagnostics;
using System.Windows;
using ModderLords.Core.Workshop;

namespace ModderLords.App;

/// <summary>
/// Subscribes to the missing mods that have a Workshop id and shows each one's progress. Whatever does not end up
/// installed (Steam not running, an item removed from the Workshop, a stalled download) can be opened on the Workshop
/// instead, which is also what this window offers straight away when auto-subscribe is off.
/// </summary>
public partial class WorkshopSubscribeWindow : Window
{
    private readonly Dictionary<ulong, string> _modIdFor = new();
    private readonly Dictionary<ulong, string> _status = new();
    private readonly List<string> _manual;
    private Process? _helper;

    /// <summary>True once at least one item has finished installing, so the caller knows a rescan is worth it.</summary>
    public bool AnyInstalled { get; private set; }

    /// <param name="workshop">Missing mods with a Workshop id, and the module id each one is expected to provide.</param>
    /// <param name="manual">Missing mods nothing here can fetch, with whatever link the list gave.</param>
    /// <param name="gameRoot">Null, or <paramref name="subscribe"/> false, skips Steam and offers the Workshop pages.</param>
    public WorkshopSubscribeWindow(IReadOnlyList<(ulong WorkshopId, string ModId)> workshop, IReadOnlyList<(string ModId, string? Link)> manual,
                                   string? gameRoot, bool subscribe)
    {
        InitializeComponent();
        foreach (var (wid, mod) in workshop) { _modIdFor[wid] = mod; _status[wid] = subscribe ? "waiting" : "not subscribed"; }
        _manual = manual.Select(m => $"{m.ModId,-32} download manually{(m.Link is null ? " (no link in the list)" : "  " + m.Link)}").ToList();
        Render();

        if (!subscribe || gameRoot is null || workshop.Count == 0)
        {
            Header.Text = workshop.Count == 0
                ? "None of the missing mods are on the Steam Workshop."
                : gameRoot is null ? "Bannerlord's install was not found, so Steam cannot be asked directly." : $"{workshop.Count} missing mods are on the Workshop.";
            Note.Text = "Open them on the Workshop and press Subscribe on each; ModderLords picks them up when the downloads finish.";
            Finish();
            return;
        }

        Header.Text = $"Subscribing to {workshop.Count} mods through Steam…";
        try
        {
            _helper = WorkshopHelper.Start(gameRoot, workshop.Select(w => w.WorkshopId).ToList(), e => Dispatcher.BeginInvoke(() => OnEvent(e)));
            _helper.Exited += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                Header.Text = _status.Values.All(s => s == "installed") ? "All done." : "Finished — some mods did not install.";
                Finish();
            });
        }
        catch (Exception ex)
        {
            Header.Text = "Could not start the Steam helper: " + ex.Message;
            Finish();
        }
    }

    private void OnEvent(WorkshopEvent e)
    {
        if (e.Kind == WorkshopEventKind.SteamUnavailable)
        {
            Header.Text = "Steam could not be used: " + e.Detail;
            Note.Text = "Open the mods on the Workshop instead, or start Steam and try again.";
            return;
        }
        if (!_modIdFor.ContainsKey(e.Id)) _modIdFor[e.Id] = e.Parent is { } p ? $"(required by {Label(p)})" : "(extra)";
        _status[e.Id] = e.Kind switch
        {
            WorkshopEventKind.Subscribed => "subscribed",
            WorkshopEventKind.Downloading => $"downloading {e.Progress:P0}",
            WorkshopEventKind.Installed => "installed",
            _ => "failed: " + e.Detail,
        };
        if (e.Kind == WorkshopEventKind.Installed) AnyInstalled = true;
        Render();
    }

    private string Label(ulong id) => _modIdFor.TryGetValue(id, out var m) ? m : id.ToString();

    private void Render()
    {
        Items.ItemsSource = _status.Select(kv => $"{Label(kv.Key),-32} {kv.Key,-12} {kv.Value}").Concat(_manual).ToList();
    }

    private IEnumerable<ulong> NotInstalled => _status.Where(kv => kv.Value != "installed").Select(kv => kv.Key);

    private void Finish()
    {
        CloseButton.Content = "Close";
        var left = NotInstalled.Count();
        OpenButton.Visibility = left > 0 ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.Content = $"Open {left} on Workshop";
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        OpenButton.IsEnabled = false;
        await WorkshopHelper.OpenOnWorkshopAsync(NotInstalled.ToList());
        OpenButton.IsEnabled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        // Subscriptions already made stay made; Steam keeps downloading without us.
        try { if (_helper is { HasExited: false }) _helper.Kill(); } catch (Exception) { }
        base.OnClosed(e);
    }
}
