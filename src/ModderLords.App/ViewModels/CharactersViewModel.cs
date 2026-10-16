using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
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

/// <summary>One row of the Companions list: a hero of a player's clan who is not a player.</summary>
public sealed record CompanionRow(AdminCompanion Companion)
{
    public string Name => Companion.Name;
    public string Kind => Companion.Kind == "companion" ? "Companion" : "Family";
    public string Owner => Companion.Owner;
    public string Clan => Companion.Clan;
    public int Level => Companion.Level;
    public int Age => Companion.Age;
    public string Where => Companion.Busy.Length > 0 ? $"{Companion.Where} ({Companion.Busy})" : Companion.Where;
    public string HeroId => Companion.HeroId;
}

/// <summary>One row of the Holdings list: a town or castle and its owner.</summary>
public sealed record HoldingRow(Holding Holding)
{
    public string Name => Holding.Name;
    public string Type => Holding.Type == "town" ? "Town" : "Castle";
    public string Clan => Holding.Clan;
    public string Kingdom => Holding.Kingdom;
    public int Villages => Holding.Villages;
    public string State => Holding.Busy;
    public string Id => Holding.Id;
}

/// <summary>A clan in the Holdings tab's "new owner" list. Players' clans come first.</summary>
public sealed record ClanChoice(HoldingClan Clan)
{
    public string Id => Clan.Id;
    public string Display => Clan.Name
        + (Clan.Kingdom.Length > 0 ? $" ({Clan.Kingdom})" : " (no kingdom)")
        + (Clan.Player ? ", player" : "");
    public override string ToString() => Display;
}

/// <summary>
/// The Characters tab: the players registered on the running server, their companions and family, who owns each town
/// and castle, and the ban list. Kick and the list go through
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
    public ObservableCollection<CompanionRow> Companions { get; } = new();
    public ObservableCollection<HoldingRow> Holdings { get; } = new();
    public ObservableCollection<ClanChoice> Clans { get; } = new();
    public ObservableCollection<BanRecord> Bans { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCompanionCommand))]
    private CompanionRow? _selectedCompanion;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetOwnerCommand))]
    private HoldingRow? _selectedHolding;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetOwnerCommand))]
    private ClanChoice? _newOwner;

    [ObservableProperty] private string _companionStatus = "";
    [ObservableProperty] private string _holdingStatus = "";

    /// <summary>Asks before a town or castle changes hands. Replaced in tests, which have no message box.</summary>
    public Func<string, bool> ConfirmOwner { get; set; } = message =>
        MessageBox.Show(message, "Change owner", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(KickCommand))]
    [NotifyCanExecuteChangedFor(nameof(BanCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
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
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetOwnerCommand))]
    private bool _isBusy;

    private bool Serving => _host.IsServing;

    private bool CanRefresh() => Serving && !IsBusy;

    /// <summary>Reads all three lists: players, their companions and family, and the holdings.</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task Refresh() => RefreshLists(holdings: true);

    /// <summary>
    /// Players and companions change when someone joins or leaves; the holdings list is long and changes with the
    /// campaign, so it is read only when asked for, when the server comes up, and after an owner is changed here.
    /// </summary>
    private async Task RefreshLists(bool holdings)
    {
        // The commands live in the sync module; a server launched without it would only answer "no such command".
        if (_host.ClientTarget is { } target && !HostViewModel.ServerCarriesSyncModule(target)) { Headline = AdminClient.NotLoaded; return; }
        IsBusy = true;
        try
        {
            var reply = await _admin.RequestAsync("players", [], ReplyTimeout);
            if (!reply.Ok) { Headline = "The server could not list its players: " + reply.Error; return; }
            ShowPlayers(AdminClient.Players(reply));

            reply = await _admin.RequestAsync("companions", [], ReplyTimeout);
            if (reply.Ok) ShowCompanions(AdminCompanion.From(reply));
            else CompanionStatus = "The server could not list companions: " + reply.Error;

            if (!holdings) return;
            reply = await _admin.RequestAsync("holdings", [], ReplyTimeout);
            if (reply.Ok) ShowHoldings(HoldingsList.From(reply));
            else HoldingStatus = "The server could not list the holdings: " + reply.Error;
        }
        catch (Exception ex) { Headline = ex.Message; }
        finally { IsBusy = false; }
    }

    private bool CanEditCompanion() => Serving && SelectedCompanion is not null;

    /// <summary>Opens the same editor as for a player, on a companion or family member.</summary>
    [RelayCommand(CanExecute = nameof(CanEditCompanion))]
    private void EditCompanion()
    {
        if (SelectedCompanion is not { } row) return;
        var editor = new CharacterEditorViewModel(_admin, HeroTargets.Hero(row.HeroId), row.Name, () => Serving);
        var window = new CharacterEditorWindow(editor) { Owner = Application.Current.MainWindow };
        window.Closed += async (_, _) => await RefreshIfServing();
        window.Show();
    }

    private bool CanSetOwner() => Serving && !IsBusy && SelectedHolding is not null && NewOwner is not null && SelectedHolding.Holding.ClanId != NewOwner.Id;

    /// <summary>Gives the selected town or castle, and the villages bound to it, to the chosen clan.</summary>
    [RelayCommand(CanExecute = nameof(CanSetOwner))]
    private async Task SetOwner()
    {
        if (SelectedHolding is not { } row || NewOwner is not { } clan) return;
        if (!ConfirmOwner($"Give {row.Name} to {clan.Display}?\n\nIt belongs to {row.Clan}{(row.Kingdom.Length > 0 ? $" ({row.Kingdom})" : "")} now. "
                + (row.Villages > 0 ? $"Its {row.Villages} village(s) go with it. " : "")
                + "The game replaces the garrison's owner and removes the governor, as it does when a fief is granted.")) return;
        IsBusy = true;
        try
        {
            var reply = await _admin.RequestAsync("setowner", [row.Id, clan.Id], ReplyTimeout);
            if (!reply.Ok) { HoldingStatus = $"{row.Name} was not changed: {reply.Error}"; return; }
            var holdings = HoldingsList.From(reply);
            ShowHoldings(holdings);
            HoldingStatus = holdings.Changed.Length > 0 ? "Changed " + holdings.Changed + "." : $"{row.Name} already belonged to {clan.Clan.Name}.";
        }
        catch (Exception ex) { HoldingStatus = ex.Message; }
        finally { IsBusy = false; }
    }

    internal void ShowCompanions(IReadOnlyList<AdminCompanion> companions)
    {
        var selected = SelectedCompanion?.HeroId;
        Companions.Clear();
        foreach (var c in companions.OrderBy(c => c.Owner, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Kind).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            Companions.Add(new CompanionRow(c));
        SelectedCompanion = Companions.FirstOrDefault(r => r.HeroId == selected);
        CompanionStatus = $"{companions.Count(c => c.Kind == "companion")} companion(s) and {companions.Count(c => c.Kind != "companion")} family member(s) in the players' clans. Updated {DateTime.Now:HH:mm:ss}.";
    }

    internal void ShowHoldings(HoldingsList holdings)
    {
        var selected = SelectedHolding?.Id;
        var owner = NewOwner?.Id;
        Holdings.Clear();
        foreach (var s in holdings.Settlements.OrderBy(s => s.Kingdom, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.Clan, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
            Holdings.Add(new HoldingRow(s));
        Clans.Clear();
        foreach (var c in holdings.Clans.OrderByDescending(c => c.Player).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            Clans.Add(new ClanChoice(c));
        SelectedHolding = Holdings.FirstOrDefault(r => r.Id == selected);
        NewOwner = Clans.FirstOrDefault(c => c.Id == owner);
        HoldingStatus = $"{holdings.Settlements.Count(s => s.Type == "town")} town(s) and {holdings.Settlements.Count(s => s.Type != "town")} castle(s). Updated {DateTime.Now:HH:mm:ss}.";
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

    private bool CanTransfer() => Serving && !IsBusy && SelectedPlayer is not null;

    /// <summary>Saves the selected player's character to a file: everything that makes up the hero, by game id.</summary>
    [RelayCommand(CanExecute = nameof(CanTransfer))]
    private async Task Export()
    {
        if (SelectedPlayer is not { } row || Transfer() is not { } transfer) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export character",
            Filter = $"ModderLords character (*{CharacterTransferClient.FileExtension})|*{CharacterTransferClient.FileExtension}",
            FileName = SafeFileName(row.Name) + CharacterTransferClient.FileExtension,
        };
        if (dialog.ShowDialog() != true) return;
        IsBusy = true;
        try
        {
            var text = await transfer.ExportAsync(row.SteamId);
            await File.WriteAllTextAsync(dialog.FileName, text);
            Headline = $"Exported {row.Name} to {dialog.FileName}.";
        }
        catch (Exception ex) { Headline = $"Could not export {row.Name}: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Imports a character file onto the selected player's hero: the server checks it against this world first and
    /// the host sees what will and will not come across, then picks the parts to apply.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTransfer))]
    private async Task Import()
    {
        if (SelectedPlayer is not { } row || Transfer() is not { } transfer) return;
        var dialog = new OpenFileDialog
        {
            Title = $"Import a character onto {row.Name}",
            Filter = $"ModderLords character (*{CharacterTransferClient.FileExtension})|*{CharacterTransferClient.FileExtension}|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;
        string? token = null;
        IsBusy = true;
        try
        {
            token = transfer.Stage(await File.ReadAllTextAsync(dialog.FileName));
            var check = await transfer.CheckAsync(row.SteamId, token);
            var confirm = new ImportCharacterWindow(check, row.Name) { Owner = Application.Current.MainWindow };
            if (confirm.ShowDialog() != true) { transfer.Discard(token); Headline = "Import cancelled; nothing was changed."; return; }
            var done = await transfer.ApplyAsync(row.SteamId, token, confirm.SelectedParts);
            token = null; // the server deletes it once applied
            Headline = $"Imported {done.SourceName} onto {row.Name}: {string.Join(", ", done.Applied)}."
                + (done.Notes.Count > 0 ? $" {done.Notes.Count} thing(s) did not come across as they were; see the check." : "");
        }
        catch (Exception ex) { Headline = $"Could not import onto {row.Name}: {ex.Message}"; }
        finally
        {
            if (token is not null) transfer.Discard(token);
            IsBusy = false;
        }
        await RefreshIfServing();
    }

    private CharacterTransferClient? Transfer()
    {
        try { return new CharacterTransferClient(_admin, LaunchSession.ResolvePaths(_host.ClientProfile).DataDir); }
        catch (Exception ex) { Headline = ex.Message; return null; }
    }

    private static string SafeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length > 0 ? clean : "character";
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

    private Task RefreshIfServing() => Serving ? RefreshLists(holdings: false) : Task.CompletedTask;

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
            if (CanRefresh()) await RefreshLists(holdings: false);
        });
    }

    private void OnHostChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(HostViewModel.IsServing)) return;
        RefreshCommand.NotifyCanExecuteChanged();
        KickCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
        EditCompanionCommand.NotifyCanExecuteChanged();
        SetOwnerCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        ImportCommand.NotifyCanExecuteChanged();
        if (Serving) _ = Refresh();
    }

    private void OnServerExited()
    {
        _admin.Abandon("The server stopped.");
        var last = Players.Select(r => r.Player with { Online = false, PeerId = -1, Ip = "", Busy = "" }).ToList();
        Players.Clear();
        foreach (var p in last) Players.Add(new PlayerRow(p));
        ListStatus = "The server stopped. The list is as it was last seen; bans can still be managed.";
        CompanionStatus = HoldingStatus = "The server stopped. The list is as it was last seen.";
    }
}
