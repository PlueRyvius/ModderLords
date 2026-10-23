using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Common;
using Common.Messaging;
using Common.Network;
using GameInterface;
using GameInterface.Services.Players;
using HarmonyLib;
using ModderLords.CompatSync;
using ModderLords.CompatSync.Coop;
using ModderLords.CompatSync.Coop.Admin;
using ModderLords.CompatSync.Messages;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.Library;

namespace ModderLords.CompatSync.Coop.Admin
{
    /// <summary>
    /// The editor's family actions, on the server. Each goes through the game's own code where it has some:
    /// MarriageAction (which checks the game's marriage rules and moves the clan), Hero.Spouse (whose setter clears both
    /// sides and records the ex-spouse), Hero.Father / Mother, and the clan change MarriageAction itself makes
    /// (governor, party, army and home settlements). Coop syncs Spouse, Father, Mother and Clan to every client.
    /// <para>
    /// The game's Father / Mother setters add the hero to the new parent's children but never take them off the old
    /// parent's, so that is done here and, through <see cref="NetworkParentsChanged"/>, on every client.
    /// </para>
    /// </summary>
    internal static class FamilyAdmin
    {
        private static readonly FieldInfo? Children = AccessTools.Field(typeof(Hero), "_children");
        private static readonly MethodInfo? ClanChange = AccessTools.Method(typeof(MarriageAction), "HandleClanChangeAfterMarriageForHero");

        /// <summary>
        /// <c>modderlords.family &lt;req&gt; &lt;target&gt; &lt;action&gt; [argument]</c> (<see cref="FamilyWire"/>): marry, divorce,
        /// father, mother, clan or adopt. Replies with the hero as <c>modderlords.hero</c> does, after the change.
        /// </summary>
        [CommandLineFunctionality.CommandLineArgumentFunction("family", AdminWire.CommandGroup)]
        public static string FamilyCommand(List<string> args) => PlayerAdmin.Run("family", args, req =>
        {
            if (args.Count < 3) return AdminWire.Reply("family", req, "Usage: modderlords.family <request id> <steam id | hero:id> <action> [argument]");
            if (!FamilyWire.TryParse(args[2], args.Count > 3 ? args[3] : "", out var action, out var argument, out var usage))
                return AdminWire.Reply("family", req, usage);
            if (HeroAdmin.FindTarget(args[1], out var steamId, out var hero, out var party) is { } missing) return AdminWire.Reply("family", req, missing);
            var busy = PlayerAdmin.BusyReason(party);
            if (busy.Length > 0) return AdminWire.Reply("family", req, $"{hero!.Name} is {busy}. Change their family once it is over.");

            var result = Run(hero!, action, argument);
            if (result.Error is { } error) return AdminWire.Reply("family", req, error);
            Log.Info($"host family change for {hero!.Name} ({args[1]}): {result.Change}");
            var data = HeroAdmin.Describe(steamId, hero!, hero!.PartyBelongedTo);
            data["changes"] = new List<object?> { result.Change };
            data["notApplied"] = new List<object?>();
            return AdminWire.Reply("family", req, null, data);
        });

        private static (string? Error, string Change) Run(Hero hero, FamilyAction action, string argument)
        {
            Hero? other = null;
            if (action is FamilyAction.Marry or FamilyAction.Father or FamilyAction.Mother or FamilyAction.Adopt && argument != FamilyWire.None)
            {
                if (!HeroTargetWire.IsHero(argument, out var otherId) || (other = Hero.Find(otherId)) is null) return ($"No hero \"{argument}\" is in this world.", "");
                if (other == hero) return ($"{hero.Name} cannot be their own relative.", "");
            }

            switch (action)
            {
                case FamilyAction.Marry:
                {
                    if (!hero.IsAlive || !other!.IsAlive) return ("Only the living can marry.", "");
                    if (hero.Spouse is { } s1) return ($"{hero.Name} is married to {s1.Name}. Divorce first.", "");
                    if (other.Spouse is { } s2) return ($"{other.Name} is married to {s2.Name}. Divorce them first.", "");
                    if (!Campaign.Current.Models.MarriageModel.IsCoupleSuitableForMarriage(hero, other))
                        return ($"The game does not allow {hero.Name} and {other.Name} to marry (too young, the same sex, close relatives, or in clans that cannot marry).", "");
                    MarriageAction.Apply(hero, other, showNotification: true);
                    return hero.Spouse == other ? (null, $"Married {hero.Name} and {other.Name}; the family is clan {hero.Clan?.Name}") : ("The game did not marry them.", "");
                }
                case FamilyAction.Divorce:
                {
                    if (hero.Spouse is not { } spouse) return ($"{hero.Name} is not married.", "");
                    // The setter clears the other side too and records each as the other's ex-spouse.
                    hero.Spouse = null;
                    return (null, $"Divorced {hero.Name} and {spouse.Name}");
                }
                case FamilyAction.Father:
                case FamilyAction.Mother:
                {
                    var father = action == FamilyAction.Father;
                    if (other is not null)
                    {
                        if (other.IsFemale == father) return ($"{other.Name} cannot be {hero.Name}'s {(father ? "father" : "mother")}.", "");
                        if (IsAncestor(hero, other)) return ($"{other.Name} is {hero.Name}'s descendant.", "");
                    }
                    var before = father ? hero.Father : hero.Mother;
                    SetParent(hero, other, father);
                    var role = father ? "Father" : "Mother";
                    return (null, $"{role} {before?.Name?.ToString() ?? "none"} -> {other?.Name?.ToString() ?? "none"}");
                }
                case FamilyAction.Clan:
                {
                    var clan = Clan.All.FirstOrDefault(c => c.StringId == argument);
                    if (clan is null) return ($"No clan \"{argument}\" is in this world.", "");
                    if (MoveRefusal(hero, clan) is { } refused) return (refused, "");
                    var before = hero.Clan;
                    MoveToClan(hero, clan);
                    return (null, $"Clan {before?.Name?.ToString() ?? "none"} -> {clan.Name}");
                }
                case FamilyAction.Adopt:
                {
                    if (other is null) return ("Adopt needs the parent.", "");
                    if (!other.IsAlive) return ($"{other.Name} is dead.", "");
                    if (IsAncestor(hero, other)) return ($"{other.Name} is {hero.Name}'s descendant.", "");
                    var clan = other.Clan;
                    if (clan is not null && hero.Clan != clan && MoveRefusal(hero, clan) is { } refused) return (refused, "");
                    SetParent(hero, other, father: !other.IsFemale);
                    if (clan is not null && hero.Clan != clan) MoveToClan(hero, clan);
                    return (null, $"{other.Name} adopted {hero.Name}" + (clan is not null ? $" into clan {clan.Name}" : ""));
                }
                default:
                    return ($"Unknown family action {action}.", "");
            }
        }

        /// <summary>True when <paramref name="who"/> descends from <paramref name="hero"/>, which would make a loop.</summary>
        private static bool IsAncestor(Hero hero, Hero who)
        {
            var seen = new HashSet<Hero>();
            var walk = new Stack<Hero>();
            walk.Push(who);
            while (walk.Count > 0)
            {
                var h = walk.Pop();
                if (!seen.Add(h)) continue;
                if (h == hero) return true;
                if (h.Father is { } f) walk.Push(f);
                if (h.Mother is { } m) walk.Push(m);
            }
            return false;
        }

        private static void SetParent(Hero hero, Hero? parent, bool father)
        {
            var before = father ? hero.Father : hero.Mother;
            if (before == parent) return;
            if (father) hero.Father = parent; else hero.Mother = parent;
            // The Father / Mother setters only add to the new parent's children, so the old parent still lists them.
            RemoveChild(before, hero);
            if (before is not null)
                global::Coop.Core.Server.Services.ModderLordsCompat.Handlers.FamilyServerHandler.Current?.SendParentsChanged(hero.StringId, before.StringId);
        }

        private static readonly List<(string Hero, string OldParent, int Tries)> Pending = new();

        /// <summary>Client: a parent change the server made, to clean up once Coop's sync of the new parent has landed.</summary>
        internal static void QueueParentCleanup(string heroId, string oldParentId)
        {
            lock (Pending) Pending.Add((heroId, oldParentId, 0));
        }

        /// <summary>From the adapter tick (game thread): retries each queued cleanup for about a minute.</summary>
        public static void ClientTick()
        {
            List<(string Hero, string OldParent, int Tries)> work;
            lock (Pending) { if (Pending.Count == 0) return; work = Pending.ToList(); Pending.Clear(); }
            if (Campaign.Current is null) return;
            foreach (var (heroId, oldId, tries) in work)
            {
                if (Hero.Find(heroId) is not { } hero || Hero.Find(oldId) is not { } old) continue;
                if (hero.Father != old && hero.Mother != old) RemoveChild(old, hero);
                else if (tries < 20) lock (Pending) Pending.Add((heroId, oldId, tries + 1));
            }
        }

        /// <summary>Takes <paramref name="child"/> off <paramref name="parent"/>'s children when they are no longer theirs.</summary>
        internal static void RemoveChild(Hero? parent, Hero child)
        {
            if (parent is null || child.Father == parent || child.Mother == parent) return;
            if (Children?.GetValue(parent) is List<Hero> list) list.RemoveAll(h => h == child);
        }

        private static string? MoveRefusal(Hero hero, Clan clan)
        {
            if (!hero.IsAlive) return $"{hero.Name} is dead. Revive them first.";
            if (hero.Clan == clan) return $"{hero.Name} is already in clan {clan.Name}.";
            if (clan.IsEliminated) return $"Clan {clan.Name} no longer exists.";
            if (hero.Clan?.Leader == hero) return $"{hero.Name} leads clan {hero.Clan.Name}, and a clan cannot lose its leader this way.";
            if (hero.CompanionOf is not null) return $"{hero.Name} is a companion. Companions join a clan through the game's own hiring.";
            if (ContainerProvider.TryResolve<IPlayerManager>(out var players) && HeroAdmin.PlayerLeads(players).Contains(hero))
                return $"{hero.Name} is a player's hero.";
            if (ClanChange is null) return "This game version has no clan change to use.";
            return null;
        }

        /// <summary>The clan change the game's own MarriageAction makes: governor, party, army and home settlements included.</summary>
        private static void MoveToClan(Hero hero, Clan clan) => ClanChange!.Invoke(null, new object[] { hero, clan });

        /// <summary>
        /// <c>modderlords.findheroes &lt;req&gt; &lt;hex text&gt;</c>: up to 40 heroes, living first, whose name contains the
        /// text, for the editor's hero picker.
        /// </summary>
        [CommandLineFunctionality.CommandLineArgumentFunction("findheroes", AdminWire.CommandGroup)]
        public static string FindHeroesCommand(List<string> args) => PlayerAdmin.Run("findheroes", args, req =>
        {
            var text = args.Count > 1 && HeroNameWire.TryUnhex(args[1], out var t) ? t.Trim() : "";
            if (text.Length < 2) return AdminWire.Reply("findheroes", req, "Type at least two letters of the name.");
            var found = Hero.AllAliveHeroes.Concat(Hero.DeadOrDisabledHeroes)
                .Where(h => h.Name?.ToString().IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0 && h.CharacterObject is not null && !h.IsNotable)
                .OrderByDescending(h => h.IsAlive).ThenBy(h => h.Name.ToString(), StringComparer.CurrentCultureIgnoreCase)
                .Take(40)
                .Select(h => (object?)new Dictionary<string, object?>
                {
                    ["heroId"] = h.StringId, ["name"] = h.Name?.ToString() ?? "", ["female"] = h.IsFemale, ["age"] = (int)h.Age,
                    ["alive"] = h.IsAlive, ["clan"] = h.Clan?.Name?.ToString() ?? "",
                }).ToList();
            return AdminWire.Reply("findheroes", req, null, new Dictionary<string, object?> { ["list"] = found });
        });

        /// <summary><c>modderlords.findclans &lt;req&gt; &lt;hex text&gt;</c>: clans still in the world whose name contains the text (empty: all).</summary>
        [CommandLineFunctionality.CommandLineArgumentFunction("findclans", AdminWire.CommandGroup)]
        public static string FindClansCommand(List<string> args) => PlayerAdmin.Run("findclans", args, req =>
        {
            var text = args.Count > 1 && HeroNameWire.TryUnhex(args[1], out var t) ? t.Trim() : "";
            var found = Clan.All.Where(c => !c.IsEliminated && (text.Length == 0 || c.Name?.ToString().IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0))
                .OrderBy(c => c.Name.ToString(), StringComparer.CurrentCultureIgnoreCase).Take(60)
                .Select(c => (object?)new Dictionary<string, object?>
                {
                    ["id"] = c.StringId, ["name"] = c.Name?.ToString() ?? "", ["kingdom"] = c.Kingdom?.Name?.ToString() ?? "",
                    ["leader"] = c.Leader?.Name?.ToString() ?? "", ["player"] = false,
                }).ToList();
            return AdminWire.Reply("findclans", req, null, new Dictionary<string, object?> { ["clans"] = found });
        });
    }
}

// Coop discovers handlers by namespace prefix; see ServerSettingsHandler.
namespace Coop.Core.Server.Services.ModderLordsCompat.Handlers
{
    /// <summary>Server: tells every client when a hero's parent changed, so each takes them off the old parent's children.</summary>
    public sealed class FamilyServerHandler : IHandler
    {
        public const int ProtocolVersion = 1;
        private readonly INetwork network;

        public static FamilyServerHandler? Current { get; private set; }

        public FamilyServerHandler(INetwork network)
        {
            this.network = network;
            if (!CoopProbe.Present) return;
            Current = this;
        }

        public void SendParentsChanged(string heroId, string oldParentId) =>
            network.SendAll(new NetworkParentsChanged { HeroId = heroId, OldParentId = oldParentId, ProtocolVersion = ProtocolVersion });

        public void Dispose()
        {
            if (ReferenceEquals(Current, this)) Current = null;
        }
    }
}

namespace Coop.Core.Client.Services.ModderLordsCompat.Handlers
{
    /// <summary>Client: takes a hero off their old parent's children once Coop has synced the new parent.</summary>
    public sealed class FamilyClientHandler : IHandler
    {
        private readonly IMessageBroker broker;
        private readonly bool active;

        public FamilyClientHandler(IMessageBroker broker)
        {
            this.broker = broker;
            if (!CoopProbe.Present) return;
            active = true;
            broker.Subscribe<NetworkParentsChanged>(Handle);
        }

        public void Dispose()
        {
            if (active) broker.Unsubscribe<NetworkParentsChanged>(Handle);
        }

        private static void Handle(MessagePayload<NetworkParentsChanged> payload)
        {
            if (payload.What.OldParentId.Length == 0) return;
            // Done from the adapter tick, on the game thread, once Coop's sync of the new parent has landed.
            FamilyAdmin.QueueParentCleanup(payload.What.HeroId, payload.What.OldParentId);
        }
    }
}
