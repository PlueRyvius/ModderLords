namespace ModderLords.CompatSync.Coop;

/// <summary>Bellum Civile's Coop compat: ModderLords.Compat calls <see cref="Register"/> once, when this assembly loads.</summary>
public static class CoopCompatPlugin
{
    public static void Register()
    {
        CompatPlugins.Ticks.Add(("BellumThreadSafety.EnsureInstalled", BellumThreadSafety.EnsureInstalled));
        CompatPlugins.Ticks.Add(("BellumPeaceInCoop.EnsureInstalled", BellumPeaceInCoop.EnsureInstalled));
        CompatPlugins.Ticks.Add(("BellumVotePeace.EnsureInstalled", BellumVotePeace.EnsureInstalled));
        CompatPlugins.Ticks.Add(("BellumTreatyVote.EnsureInstalled", BellumTreatyVote.EnsureInstalled));
        CompatPlugins.Ticks.Add(("BellumTreatyVote.ServerTick", BellumTreatyVote.ServerTick));
        CompatPlugins.Ticks.Add(("BellumClientKingdoms.EnsureInstalled", BellumClientKingdoms.EnsureInstalled));
        CompatPlugins.Ticks.Add(("BellumClientKingdoms.ClientTick", BellumClientKingdoms.ClientTick));
        CompatPlugins.VoteDeadline.Add((decision, rulerVoted) => BellumTreatyVote.Installed && BellumTreatyVote.OnDeadline(decision, rulerVoted));
        CompatPlugins.Summaries.Add(() =>
            (BellumPeaceInCoop.Answered > 0 ? $"; Bellum parley steps answered {BellumPeaceInCoop.Answered}" : "")
            + (BellumVotePeace.Marked > 0 ? $"; Bellum vote peace applied {BellumVotePeace.Marked}" : "") + BellumTreatyVote.Summary());
    }
}
