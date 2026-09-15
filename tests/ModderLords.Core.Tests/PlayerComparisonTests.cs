using System.Runtime.CompilerServices;
using HarmonyLib;
using ModderLords.CompatSync.Coop;
using ModderLords.Core.Compat.Authority;
using ModderLords.Core.Tests.PlayerFakes;
using TaleWorlds.CampaignSystem;

namespace ModderLords.Core.Tests.PlayerFakes
{
    // Shapes a single-player mod uses to ask "is this the player's?". Hero.MainHero is null in these fakes, so only the
    // rewritten versions are ever called.
    public static class PlayerCompareFixtures
    {
        [MethodImpl(MethodImplOptions.NoInlining)] public static bool Direct(Hero? h) => h == Hero.MainHero;
        [MethodImpl(MethodImplOptions.NoInlining)] public static bool NotDirect(Hero? h) => h != Hero.MainHero;
        [MethodImpl(MethodImplOptions.NoInlining)] public static bool ChainedClan(Clan? c) => c == Hero.MainHero!.Clan;
        [MethodImpl(MethodImplOptions.NoInlining)] public static bool EqualsCall(Clan? c) => c != null && c.Equals(Hero.MainHero!.Clan);
        [MethodImpl(MethodImplOptions.NoInlining)] public static int Branch(Hero? h) { if (h == Hero.MainHero) return 1; return 0; }
        [MethodImpl(MethodImplOptions.NoInlining)] public static bool PlayerClan(Clan? c) => c == Clan.PlayerClan;
        [MethodImpl(MethodImplOptions.NoInlining)] public static bool GetterFirst(Hero? h) => Hero.MainHero == h;
        [MethodImpl(MethodImplOptions.NoInlining)] public static bool NullCheck() => Hero.MainHero != null;
    }
}

namespace ModderLords.Core.Tests
{
    public sealed class PlayerComparisonTests
    {
        private static readonly string T = typeof(PlayerCompareFixtures).FullName + "::";
        private static readonly Lazy<ModCodeModel> Model = new(() => ModAnalysis.Analyse("Tests", [typeof(PlayerComparisonTests).Assembly.Location]));

        private static readonly Hero Player = new();
        private static readonly Clan PlayersClan = new();
        public static bool FakeIsPlayerHero(Hero? h) => ReferenceEquals(h, Player);
        public static bool FakeIsPlayerClan(Clan? c) => ReferenceEquals(c, PlayersClan);
        public static bool FakeIsPlayerParty(object? p) => false;
        public static bool FakeIsPlayerPartyBase(object? p) => false;

        public static readonly TheoryData<string, int> Shapes = new()
        {
            { "Direct", 1 }, { "NotDirect", 1 }, { "ChainedClan", 1 }, { "EqualsCall", 1 }, { "Branch", 1 }, { "PlayerClan", 1 },
            { "GetterFirst", 0 }, { "NullCheck", 0 },
        };

        [Theory]
        [MemberData(nameof(Shapes))]
        public void Analysis_counts_rewritable_comparisons(string method, int expected) =>
            Assert.Equal(expected, Model.Value.Methods[T + method].PlayerComparisons);

        [Fact]
        public void Server_rewrite_asks_about_any_player_and_matches_the_analysis()
        {
            var t = typeof(PlayerComparisonTests);
            PlayerComparisonRewriter.SetHelpers(t.GetMethod(nameof(FakeIsPlayerHero))!, t.GetMethod(nameof(FakeIsPlayerClan))!,
                t.GetMethod(nameof(FakeIsPlayerParty))!, t.GetMethod(nameof(FakeIsPlayerPartyBase))!);
            var harmony = new Harmony("test.playerchecks." + Guid.NewGuid().ToString("N"));
            var ids = Shapes.Select(row => T + (string)row[0]).ToList();
            var warnings = new List<string>();

            var (methods, comparisons, missing) = PlayerComparisonRewriter.Apply(harmony, ids, warnings.Add);

            Assert.Equal(ids.Count, methods);
            Assert.Equal(0, missing);
            Assert.Empty(warnings);
            foreach (var row in Shapes)
            {
                var name = (string)row[0];
                Assert.True((int)row[1] == PlayerComparisonRewriter.Rewritten(AccessTools.Method(typeof(PlayerCompareFixtures), name)), name);
            }
            Assert.Equal(6, comparisons);

            var other = new Hero();
            Assert.True(PlayerCompareFixtures.Direct(Player));
            Assert.False(PlayerCompareFixtures.Direct(other));
            Assert.False(PlayerCompareFixtures.NotDirect(Player));
            Assert.True(PlayerCompareFixtures.NotDirect(other));
            Assert.True(PlayerCompareFixtures.ChainedClan(PlayersClan));
            Assert.False(PlayerCompareFixtures.ChainedClan(new Clan()));
            Assert.True(PlayerCompareFixtures.EqualsCall(PlayersClan));
            Assert.Equal(1, PlayerCompareFixtures.Branch(Player));
            Assert.Equal(0, PlayerCompareFixtures.Branch(other));
            Assert.True(PlayerCompareFixtures.PlayerClan(PlayersClan));
            Assert.False(PlayerCompareFixtures.GetterFirst(Player));   // left alone: MainHero is null here, Player is not
            Assert.False(PlayerCompareFixtures.NullCheck());
        }
    }
}
