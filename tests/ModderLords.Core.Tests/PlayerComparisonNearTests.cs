using System.Runtime.CompilerServices;
using HarmonyLib;
using ModderLords.CompatSync.Coop;
using TaleWorlds.CampaignSystem;

namespace ModderLords.Core.Tests
{
    // Bellum's "noble house" test: minor factions are excluded except the player's clan, which the game itself defines as
    // a minor faction (spclans.xml player_faction is_minor_faction="true"). The fake Clan.PlayerClan is null.
    public static class MinorFactionFixtures
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsNobleHouse(Clan c) { if (c.IsMinorFaction && c != Clan.PlayerClan) return false; return true; }

        // A player check that means "the player at this game" elsewhere in the same method: must be left as written. In
        // Bellum 1.3.1 every such check is 16 or more IL instructions from an IsMinorFaction read.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Mixed(Clan c)
        {
            if (c == Clan.PlayerClan) return false;
            Spacer(); Spacer(); Spacer(); Spacer(); Spacer(); Spacer(); Spacer(); Spacer(); Spacer(); Spacer(); Spacer();
            if (c.IsMinorFaction && c != Clan.PlayerClan) return false;
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)] private static void Spacer() { }

        // The AI-only shape: the player comparison first, the getter after it.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsNpcClan(Clan c) => c != Clan.PlayerClan && !c.IsMinorFaction;
    }

    // PlayerComparisonRewriter's helpers are static: classes that set them must not run at the same time.
    [Collection("PlayerComparisonRewriter")]
    public sealed class PlayerComparisonNearTests
    {
        private static readonly Clan PlayersClan = Minor(new Clan());
        public static bool FakeIsPlayerHero(Hero? h) => false;
        public static bool FakeIsPlayerClan(Clan? c) => ReferenceEquals(c, PlayersClan);
        public static bool FakeIsPlayerParty(object? p) => false;
        public static bool FakeIsPlayerPartyBase(object? p) => false;

        private static Clan Minor(Clan c)
        {
            c.IsMinorFaction = true;
            return c;
        }

        [Fact]
        public void Only_the_comparison_next_to_the_getter_is_rewritten()
        {
            var t = typeof(PlayerComparisonNearTests);
            PlayerComparisonRewriter.SetHelpers(t.GetMethod(nameof(FakeIsPlayerHero))!, t.GetMethod(nameof(FakeIsPlayerClan))!,
                t.GetMethod(nameof(FakeIsPlayerParty))!, t.GetMethod(nameof(FakeIsPlayerPartyBase))!);
            var harmony = new Harmony("test.playerchecks.near." + Guid.NewGuid().ToString("N"));
            var f = typeof(MinorFactionFixtures).FullName + "::";
            var warnings = new List<string>();

            var (methods, comparisons, missing) = PlayerComparisonRewriter.Apply(harmony,
                new[] { f + nameof(MinorFactionFixtures.IsNobleHouse), f + nameof(MinorFactionFixtures.Mixed), f + nameof(MinorFactionFixtures.IsNpcClan) },
                warnings.Add, "get_IsMinorFaction");

            Assert.Equal((3, 3, 0), (methods, comparisons, missing));
            Assert.Empty(warnings);
            Assert.Equal(1, PlayerComparisonRewriter.Rewritten(AccessTools.Method(typeof(MinorFactionFixtures), nameof(MinorFactionFixtures.Mixed))));
            Assert.True(MinorFactionFixtures.IsNobleHouse(PlayersClan));     // a player's clan counts, as the player's does in single player
            Assert.False(MinorFactionFixtures.IsNobleHouse(Minor(new Clan())));   // a real minor faction still does not
            Assert.True(MinorFactionFixtures.IsNobleHouse(new Clan()));
            Assert.False(MinorFactionFixtures.IsNpcClan(PlayersClan));     // AI-only filters skip every player's clan
            Assert.True(MinorFactionFixtures.IsNpcClan(new Clan()));
        }
    }
}
