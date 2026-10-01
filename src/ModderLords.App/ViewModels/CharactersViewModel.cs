using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModderLords.Coop.Admin;
using ModderLords.Coop.Launch;
using ModderLords.Core.Smoke;

namespace ModderLords.App.ViewModels;

/// <summary>One row of the Characters tab's player list, as shown.</summary>
public sealed record PlayerRow(AdminPlayer Player)
{
    public string Name => Player.Name.Length > 0 ? Player.Name : "(no hero)";
    public string Clan => Player.Clan;
    public int Level => Player.Level;
    public int Gold => Player.Gold;
    public string State => !Player.Online ? "offline" : Player.Busy.Length > 0 ? "online, " + Player.Busy : "online";
    public string SteamId => Player.SteamId;
    public string Ip => Player.Ip;
}

/// <summary>
/// The Characters tab: the players registered on the running server, and the ban list. Kick and the list go through
/// the server's admin commands (<see cref="AdminClient"/>); bans are a file the server rereads
/// (<see cref="BanStore"/>), so they can be managed while it is stopped too.
/// </summary>
public partial class CharactersViewModel : ObservableObject
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);

    private readonly HostViewModel _host;
    private readonly AdminClient _admin;
    private int _refreshQueued;

    public CharactersViewModel(HostViewModel host)
    {
        _host = host;
        // Through the Console tab like a typed command, so each request and its @ML@ reply sit together there.
        _admin = new AdminClient(command => Application.Current.Dispatcher.Invoke(() => _host.SendServerCommandAsync(command)));
        host.ServerLineObserved += OnServerLine;
        host.ServerExited += _ => Application.Current.Dispatcher.BeginInvoke(OnServerExited);
        host.PropertyChanged += OnHostChanged;
        LoadBans();
    }

    public ObservableCollection<PlayerRow> Players { get; } = new();
    public ObservableCollection<BanRecord> Bans { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(KickCommand))]
    [NotifyCanExecuteChangedFor(nameof(BanCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditCommand))]
    private PlayerRow? _selectedPlayer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnbanCommand))]
    private BanRecord? _selectedBan;

    [ObservableProperty] private string _banReason = "";
    /// <summary>What the last action did. Kept apart from <see cref="ListStatus"/> so the refresh after a kick does not hide it.</summary>
    [ObservableProperty] private string _headline = "Start the server to see its players. Bans can be managed at any time.";
    [ObservableProperty] private string _listStatus = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(KickCommand))]
    private bool _isBusy;

    private bool Serving => _host.IsServing;

    private bool CanRefresh() => Serving && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task Refresh()
    {
        IsBusy = true;
        try
        {
            var reply = await _admin.RequestAsync("players", [], ReplyTimeout);
            if (!reply.Ok) { Headline = "The server could not list its players: " + reply.Error; return; }
            ShowPlayers(AdminClient.Players(reply));
        }
        catch (Exception ex) { Headline = ex.Message; }
        finally { IsBusy = false; }
    }

    private bool CanKick() => Serving && !IsBusy && SelectedPlayer?.Player.Online == true;

    [RelayCommand(CanExecute = nameof(CanKick))]
    private async Task Kick()
    {
        if (SelectedPlayer is not { } row) return;
        IsBusy = true;
        try
        {
            var reply = await _admin.RequestAsync("kick", [row.SteamId], ReplyTimeout);
            Headline = reply.Ok
                ? $"Kicked {row.Name}. Their character stays in the world, and they can rejoin unless banned."
                : $"Could not kick {row.Name}: {reply.Error}";
        }
        catch (Exception ex) { Headline = ex.Message; }
        finally { IsBusy = false; }
        await RefreshIfServing();
    }

    private bool CanEdit() => Serving && SelectedPlayer is not null;

    /// <summary>Opens the editor for the selected player's hero. Not modal, so the list stays usable; it refreshes on close.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Edit()
    {
        if (SelectedPlayer is not { } row) return;
        var editor = new CharacterEditorViewModel(_admin, row.SteamId, row.Name, () => Serving);
        var window = new CharacterEditorWindow(editor) { Owner = Application.Current.MainWindow };
        window.Closed += async (_, _) => await RefreshIfServing();
        window.Show();
    }

    private bool CanBan() => SelectedPlayer is not null;

    [RelayCommand(CanExecute = nameof(CanBan))]
    private async Task Ban()
    {
        if (SelectedPlayer is not { } row || Store() is not { } store) return;
        var by = string.Join(" and ", new[]
        {
            BanStore.IsPersonalId(row.SteamId) ? "Steam id " + row.SteamId : null,
            BanStore.IsRemoteIp(row.Ip) ? "address " + BanStore.NormalizeIp(row.Ip) : null,
        }.Where(s => s is not null));
        if (by.Length == 0)
        {
            Headline = $"{row.Name} has no Steam id of their own and no outside address (they may be playing on this PC), so there is nothing to ban them by.";
            return;
        }
        if (MessageBox.Show($"Ban {row.Name} by {by}?\n\nThey are disconnected now if online, and refused whenever they try to join."
                + (BanStore.IsRemoteIp(row.Ip) ? " The address ban also refuses anyone else who joins from the same network." : ""),
                "Ban player", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            store.Add(row.SteamId, row.Ip, row.Name, BanReason);
            BanReason = "";
            LoadBans();
            Headline = $"Banned {row.Name} by {by}.";
        }
        catch (Exception ex) { Headline = "Could not save the ban: " + ex.Message; return; }
        await ApplyBansNow();
    }

    private bool CanUnban() => SelectedBan is not null;

    [RelayCommand(CanExecute = nameof(CanUnban))]
    private async Task Unban()
    {
        if (SelectedBan is not { } ban || Store() is not { } store) return;
        try
        {
            store.Remove(ban);
            LoadBans();
            Headline = $"Unbanned {(ban.Name.Length > 0 ? ban.Name : ban.SteamId.Length > 0 ? ban.SteamId : ban.Ip)}.";
        }
        catch (Exception ex) { Headline = "Could not save the ban list: " + ex.Message; return; }
        await ApplyBansNow();
    }

    /// <summary>The server rereads the file within seconds anyway; asking makes it immediate and confirms it took.</summary>
    private async Task ApplyBansNow()
    {
        if (!Serving) return;
        try
        {
            var reply = await _admin.RequestAsync("bans", [], ReplyTimeout);
            if (!reply.Ok) { Headline += " The server did not apply it: " + reply.Error; return; }
            if (reply.Root.TryGetProperty("disconnected", out var gone) && gone.GetArrayLength() > 0)
                Headline += " Disconnected: " + string.Join(", ", gone.EnumerateArray().Select(n => n.GetString()));
        }
        catch (Exception ex) { Headline += " " + ex.Message; }
        await RefreshIfServing();
    }

    private void LoadBans()
    {
        Bans.Clear();
        try
        {
            if (Store() is { } store)
                foreach (var b in store.Load().OrderByDescending(b => b.At)) Bans.Add(b);
        }
        catch (Exception ex) { Headline = "The ban list could not be read: " + ex.Message; }
    }

    /// <summary>The ban file for the server this profile hosts, or null (and says why) when there is no server folder.</summary>
    private BanStore? Store()
    {
        try { return new BanStore(LaunchSession.ResolvePaths(_host.ClientProfile).DataDir); }
        catch (Exception ex) { Headline = ex.Message; return null; }
    }

    private void ShowPlayers(IReadOnlyList<AdminPlayer> players)
    {
        var selected = SelectedPlayer?.SteamId;
        Players.Clear();
        foreach (var p in players.OrderByDescending(p => p.Online).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            Players.Add(new PlayerRow(p));
        SelectedPlayer = Players.FirstOrDefault(r => r.SteamId == selected);
        var online = players.Count(p => p.Online);
        ListStatus = $"{players.Count} player(s) registered, {online} online. Updated {DateTime.Now:HH:mm:ss}.";
    }

    private Task RefreshIfServing() => Serving ? Refresh() : Task.CompletedTask;

    /// <summary>Stream-reader thread. A join or leave (the server's own players event) refreshes the list once.</summary>
    private void OnServerLine(string line)
    {
        if (_admin.Observe(line) is not null) return;
        if (!line.Contains(SmokeSignals.ServerEventMarker + "{\"ev\":\"players\"", StringComparison.Ordinal)) return;
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            // Coop registers a joining player after the connection event, so give it a moment.
            await Task.Delay(TimeSpan.FromSeconds(2));
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (CanRefresh()) await Refresh();
        });
    }

    private void OnHostChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(HostViewModel.IsServing)) return;
        RefreshCommand.NotifyCanExecuteChanged();
        KickCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
        if (Serving) _ = Refresh();
    }

    private void OnServerExited()
    {
        _admin.Abandon("The server stopped.");
        var last = Players.Select(r => r.Player with { Online = false, PeerId = -1, Ip = "", Busy = "" }).ToList();
        Players.Clear();
        foreach (var p in last) Players.Add(new PlayerRow(p));
        ListStatus = "The server stopped. The list is as it was last seen; bans can still be managed.";
    }
}
