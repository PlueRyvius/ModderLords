using System.Collections.Generic;
using System.Linq;
using GameInterface;
using GameInterface.Services.Players;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace ModderLords.CompatSync.Coop.Admin;

/// <summary>
/// Server only: who owns each town and castle, for the Characters tab's Holdings list, and handing one to another clan.
/// <para>
/// The change is the game's own ChangeOwnerOfSettlementAction, the action a gift or a kingdom decision ends in. Coop
/// intercepts it and tells every client, and the action's side effects (garrison, governor, patrols) replicate through
/// Coop's own patches. Villages follow the town or castle they are bound to.
/// </para>
/// </summary>
internal static class HoldingsAdmin
{
    /// <summary><c>modderlords.holdings &lt;req&gt;</c>: every town and castle with its owner, and every clan that can own one.</summary>
    [CommandLineFunctionality.CommandLineArgumentFunction("holdings", AdminWire.CommandGroup)]
    public static string HoldingsCommand(List<string> args) => PlayerAdmin.Run("holdings", args, req => AdminWire.Reply("holdings", req, null, Describe()));

    /// <summary>
    /// <c>modderlords.setowner &lt;req&gt; &lt;settlement id&gt; &lt;clan id&gt;</c>: gives that town or castle to that clan.
    /// Refused during a siege or a battle there, where the outcome decides the owner.
    /// </summary>
    [CommandLineFunctionality.CommandLineArgumentFunction("setowner", AdminWire.CommandGroup)]
    public static string SetOwnerCommand(List<string> args) => PlayerAdmin.Run("setowner", args, req =>
    {
        if (args.Count < 3) return AdminWire.Reply("setowner", req, "Usage: modderlords.setowner <request id> <settlement id> <clan id>");
        var settlement = Settlement.All.FirstOrDefault(s => s.StringId == args[1]);
        if (settlement is null) return AdminWire.Reply("setowner", req, $"This world has no settlement \"{args[1]}\".");
        if (!settlement.IsFortification) return AdminWire.Reply("setowner", req, $"{settlement.Name} is not a town or a castle. A village follows the town or castle it belongs to.");
        var clan = Clan.All.FirstOrDefault(c => c.StringId == args[2]);
        if (clan is null) return AdminWire.Reply("setowner", req, $"This world has no clan \"{args[2]}\".");
        if (!CanOwn(clan)) return AdminWire.Reply("setowner", req, $"{clan.Name} cannot own a settlement: it is destroyed or has no living leader.");
        var busy = BusyReason(settlement);
        if (busy.Length > 0) return AdminWire.Reply("setowner", req, $"{settlement.Name} is {busy}. Change its owner once that is over.");
        // Coop's clients look the previous owner up by its leader and skip the change when there is none.
        if (settlement.OwnerClan?.Leader is null)
            return AdminWire.Reply("setowner", req, $"{settlement.Name} has no owner with a leader now, so the players' games could not be told about the change.");

        var before = settlement.OwnerClan;
        var data = new Dictionary<string, object?>();
        if (ReferenceEquals(before, clan)) data["changed"] = "";
        else
        {
            ChangeOwnerOfSettlementAction.ApplyByDefault(clan.Leader, settlement);
            if (!ReferenceEquals(settlement.OwnerClan, clan))
                return AdminWire.Reply("setowner", req, $"The game did not hand {settlement.Name} to {clan.Name}; it still belongs to {settlement.OwnerClan?.Name}.");
            data["changed"] = $"{settlement.Name}: {before.Name} -> {clan.Name}";
            Log.Info($"host gave {settlement.Name} ({settlement.StringId}) to {clan.Name} ({clan.StringId}); it was {before.Name}'s");
        }
        foreach (var kv in Describe()) data[kv.Key] = kv.Value;
        return AdminWire.Reply("setowner", req, null, data);
    });

    private static bool CanOwn(Clan clan) => !clan.IsEliminated && !clan.IsBanditFaction && clan.Leader is { IsAlive: true };

    private static string BusyReason(Settlement settlement)
    {
        if (settlement.IsUnderSiege) return "under siege";
        if (settlement.Party?.MapEvent is not null) return "in a battle";
        return "";
    }

    private static Dictionary<string, object?> Describe()
    {
        var playerClans = new HashSet<Clan>();
        if (ContainerProvider.TryResolve<IPlayerManager>(out var players))
            foreach (var lead in HeroAdmin.PlayerLeads(players))
                if (lead.Clan is not null) playerClans.Add(lead.Clan);

        return new Dictionary<string, object?>
        {
            ["settlements"] = Settlement.All.Where(s => s.IsFortification).Select(s => (object?)new Dictionary<string, object?>
            {
                ["id"] = s.StringId,
                ["name"] = s.Name?.ToString() ?? s.StringId,
                ["type"] = s.IsTown ? "town" : "castle",
                ["clanId"] = s.OwnerClan?.StringId ?? "",
                ["clan"] = s.OwnerClan?.Name?.ToString() ?? "",
                ["kingdom"] = s.OwnerClan?.Kingdom?.Name?.ToString() ?? "",
                ["villages"] = s.BoundVillages?.Count ?? 0,
                ["busy"] = BusyReason(s),
            }).ToList(),
            ["clans"] = Clan.All.Where(CanOwn).Select(c => (object?)new Dictionary<string, object?>
            {
                ["id"] = c.StringId,
                ["name"] = c.Name?.ToString() ?? c.StringId,
                ["kingdom"] = c.Kingdom?.Name?.ToString() ?? "",
                ["leader"] = c.Leader?.Name?.ToString() ?? "",
                ["player"] = playerClans.Contains(c),
            }).ToList(),
        };
    }
}
