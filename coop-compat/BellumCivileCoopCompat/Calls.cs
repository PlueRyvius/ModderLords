using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BellumCivileCoopCompat
{
    // Player agency, calls (docs/BELLUM-COOP-COVERAGE.md, stage 3, A1): Bellum's calls to "the player" in a civil war,
    // put to each player they concern.
    //
    // - A call to a civil-war side. When a rebellion opens, Bellum asks the player whether to join it (they're a direct
    //   vassal of a rebel, or were in the rebel faction). On the server it builds that call for the server's own
    //   placeholder player, so nobody was asked. Now, after the rebellion opens, each connected player in the realm (not
    //   the ruler, not the faction's leader) is asked in turn, as that player, with Bellum's own call and answers. A
    //   player in the faction is first taken out of it, as Bellum does for the player, so they choose. No answer: nothing
    //   happens (the player stays where they are).
    // - Treason solidarity. When an accused lord rebels, Bellum appeals to the player if they're close to the accused
    //   (relation 60, a marriage alliance, or their direct vassal). Now each connected player in the realm (not the ruler,
    //   not the accused) gets Bellum's own appeal, and Bellum's own check decides whether it applies to them. No answer:
    //   nothing happens.
    // - A succession opportunity. After a ruler dies, a faction the player leads gets Bellum's "strike now?" question;
    //   any other faction's lord issues the ultimatum himself. Bellum's test "the player leads it" now means "a player
    //   leads it" (SuccessionClan), so a player-led faction is never sent into an ultimatum by Bellum's AI, and the
    //   question goes to that player (Prompts). An absent player: "Not yet".
    //
    // The calls are asked on the next tick, outside the rebellion Bellum is still setting up and whatever player's
    // action led to it.
    public static class Calls
    {
        private static readonly Prompts.Site CivilWarCall = new Prompts.Site("", "", null, Prompts.Fallback.Nothing, "a call to a civil-war side");
        private static readonly Prompts.Site TreasonAppeal = new Prompts.Site("", "", null, Prompts.Fallback.Nothing, "an appeal from an accused lord");

        private static MethodInfo _build, _showCall, _showTreason, _removeMember, _addMember;
        private static int _calls, _appeals, _successions;

        [ThreadStatic] private static int _inUltimatum;
        [ThreadStatic] private static Hero _successionPlayer;

        /// <summary>The player whose faction Bellum's succession loop is looking at now (server), or null.</summary>
        public static Hero SuccessionPlayer => _successionPlayer;

        public static string Summary()
        {
            var parts = new List<string>();
            if (_calls > 0) parts.Add("civil-war calls " + _calls);
            if (_appeals > 0) parts.Add("treason appeals " + _appeals);
            if (_successions > 0) parts.Add("succession opportunities " + _successions);
            return parts.Count == 0 ? "" : "; put to players: " + string.Join(", ", parts);
        }

        public static void Install(Harmony harmony)
        {
            var missing = new List<string>();
            var faction = AccessTools.TypeByName("BellumCivile.FactionObject");
            var helper = AccessTools.TypeByName("BellumCivile.CivilWarSolidarityHelper");
            var notifications = AccessTools.TypeByName("BellumCivile.NotificationHelper");
            _build = helper == null ? null : AccessTools.Method(helper, "BuildPlayerCallContext");
            _showCall = notifications == null ? null : AccessTools.Method(notifications, "ShowCivilWarSolidarityChoiceToPlayer");
            _showTreason = notifications == null ? null : AccessTools.Method(notifications, "ShowTreasonSolidarityChoiceToPlayer");
            _removeMember = faction == null ? null : AccessTools.Method(faction, "RemoveMember", new[] { typeof(Clan) });
            _addMember = faction == null ? null : AccessTools.Method(faction, "AddMember", new[] { typeof(Clan) });

            var start = faction == null ? null : AccessTools.Method(faction, "StartOpenRebellion");
            var ultimatum = faction == null ? null : AccessTools.Method(faction, "RefuseDemandsAndRebel");
            if (start == null || _build == null || _showCall == null || _removeMember == null || _addMember == null)
                missing.Add("the civil-war call (FactionObject.StartOpenRebellion, CivilWarSolidarityHelper.BuildPlayerCallContext, NotificationHelper.ShowCivilWarSolidarityChoiceToPlayer)");
            else
            {
                harmony.Patch(start, prefix: new HarmonyMethod(typeof(Calls), nameof(RebellionPrefix)), postfix: new HarmonyMethod(typeof(Calls), nameof(RebellionPostfix)));
                if (ultimatum != null)
                    harmony.Patch(ultimatum, prefix: new HarmonyMethod(typeof(Calls), nameof(UltimatumEnter)), finalizer: new HarmonyMethod(typeof(Calls), nameof(UltimatumExit)));
            }

            if (_showTreason == null)
                missing.Add("NotificationHelper.ShowTreasonSolidarityChoiceToPlayer");
            else
                harmony.Patch(_showTreason, prefix: new HarmonyMethod(typeof(Calls), nameof(TreasonPrefix)) { priority = Priority.First });

            var manager = AccessTools.TypeByName("BellumCivile.Behaviors.FactionManagerBehavior");
            var succession = manager == null ? null : AccessTools.Method(manager, "ProcessSuccessionOpportunities");
            if (succession == null)
                missing.Add("FactionManagerBehavior.ProcessSuccessionOpportunities");
            else
            {
                harmony.Patch(succession, transpiler: new HarmonyMethod(typeof(Calls), nameof(SuccessionTranspiler)));
                if (_successionRewrites != 1)
                    missing.Add("the player test in ProcessSuccessionOpportunities (found " + _successionRewrites + ")");
            }

            SubModule.Log("Calls: Bellum's civil-war calls, treason appeals and succession opportunities go to the players they concern.");
            if (missing.Count > 0)
                SubModule.Log("Calls: not found in this Bellum version: " + string.Join("; ", missing));
        }

        // ---- a call to a civil-war side ----

        private static void UltimatumEnter() => _inUltimatum++;

        private static Exception UltimatumExit(Exception __exception)
        {
            if (_inUltimatum > 0) _inUltimatum--;
            return __exception;
        }

        // A refused ultimatum (RefuseDemandsAndRebel): Bellum takes the player out of the faction before the rebellion, so they choose. On the
        // server that's the placeholder; do it for each connected player in the faction.
        private static void RebellionPrefix(object __instance, out List<Clan> __state)
        {
            __state = null;
            if (!Coop.OnServer || _inUltimatum == 0)
                return;
            var leader = Get(__instance, "Leader") as Clan;
            var parent = Get(__instance, "ParentKingdom") as Kingdom;
            if (!(Get(__instance, "Members") is IEnumerable members))
                return;
            foreach (var clan in members.OfType<Clan>().ToList())
            {
                if (clan == leader || clan.Kingdom != parent || !Prompts.CanAsk(clan.Leader))
                    continue;
                try { _removeMember.Invoke(__instance, new object[] { clan }); }
                catch (Exception ex) { SubModule.Log("Calls: could not take " + clan.StringId + " out of the faction to ask them: " + ex.GetBaseException().Message); continue; }
                (__state ?? (__state = new List<Clan>())).Add(clan);
            }
        }

        private static void RebellionPostfix(object __instance, bool __result, List<Clan> __state)
        {
            if (!Coop.OnServer)
                return;
            if (!__result)
            {
                foreach (var clan in __state ?? new List<Clan>())
                {
                    try { _addMember.Invoke(__instance, new object[] { clan }); }
                    catch { }
                }
                return;
            }
            var parent = Get(__instance, "ParentKingdom") as Kingdom;
            var leader = Get(__instance, "Leader") as Clan;
            if (parent == null)
                return;
            var acting = AsPlayer.Current;   // a player whose own action opened the rebellion: Bellum asked them itself
            foreach (var clan in parent.Clans.ToList())
            {
                var hero = clan.Leader;
                if (clan == parent.RulingClan || clan == leader || ReferenceEquals(hero, acting) || !Prompts.CanAsk(hero))
                    continue;
                var wasMember = __state?.Contains(clan) == true;
                Prompts.Later(() => AskCall(__instance, hero, wasMember));
            }
        }

        private static void AskCall(object faction, Hero hero, bool wasMember)
        {
            var parent = Get(faction, "ParentKingdom") as Kingdom;
            if (parent == null || hero.Clan?.Kingdom != parent || !Prompts.CanAsk(hero))
                return;
            Prompts.InFrame(hero, CivilWarCall, () =>
            {
                var context = _build.Invoke(null, new object[] { faction, wasMember, null });
                if (context == null)
                    return;
                _calls++;
                _showCall.Invoke(null, new[] { faction, context });
            });
        }

        // ---- treason solidarity ----

        // Server: Bellum's appeal, for the placeholder player, becomes the same appeal to each connected player in the
        // realm, on the next tick. As that player (AsPlayer.Current set), Bellum's own method runs: its own check
        // decides whether the appeal applies to them.
        private static bool TreasonPrefix(object[] __args)
        {
            if (!Coop.OnServer || AsPlayer.Current != null || __args == null || __args.Length < 3)
                return true;
            var faction = __args[0];
            var parent = __args[1] as Kingdom;
            var accused = __args[2] as Clan;
            if (faction == null || parent == null || accused == null)
                return false;
            foreach (var clan in parent.Clans.ToList())
            {
                var hero = clan.Leader;
                if (clan == accused || clan == parent.RulingClan || !Prompts.CanAsk(hero))
                    continue;
                Prompts.Later(() =>
                {
                    if (!Prompts.CanAsk(hero) || hero.Clan?.Kingdom != parent)
                        return;
                    _appeals++;
                    Prompts.InFrame(hero, TreasonAppeal, () => _showTreason.Invoke(null, new[] { faction, parent, accused }));
                });
            }
            return false;   // the server's own placeholder player is never asked
        }

        // ---- a succession opportunity ----

        private static int _successionRewrites;

        // faction.Leader == Clan.PlayerClan  ->  faction.Leader == SuccessionClan(faction.Leader)
        private static IEnumerable<CodeInstruction> SuccessionTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var playerClan = AccessTools.PropertyGetter(typeof(Clan), nameof(Clan.PlayerClan));
            CodeInstruction previous = null;
            foreach (var code in instructions)
            {
                if (code.operand is MethodInfo called && called == playerClan
                    && previous?.operand is MethodInfo before && before.Name == "get_Leader" && before.ReturnType == typeof(Clan))
                {
                    _successionRewrites++;
                    var dup = new CodeInstruction(OpCodes.Dup);
                    dup.labels.AddRange(code.labels);
                    dup.blocks.AddRange(code.blocks);
                    yield return dup;
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Calls), nameof(SuccessionClan)));
                    previous = null;
                    continue;
                }
                previous = code;
                yield return code;
            }
        }

        /// <summary>The faction's leading clan itself when a player leads it (on the server), else the game's player clan.</summary>
        private static Clan SuccessionClan(Clan leader)
        {
            _successionPlayer = null;
            if (Coop.OnServer && leader != null && Coop.IsPlayerHero(leader.Leader))
            {
                _successionPlayer = leader.Leader;
                _successions++;
                return leader;
            }
            return Clan.PlayerClan;
        }

        private static object Get(object instance, string member)
        {
            if (instance == null) return null;
            var type = instance.GetType();
            var property = AccessTools.Property(type, member);
            return property != null ? property.GetValue(instance, null) : AccessTools.Field(type, member)?.GetValue(instance);
        }
    }
}
