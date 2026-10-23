using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Election;

namespace BellumCivileCoopCompat
{
    // Optional extras for a launcher that has its own way to reach players. Nothing here is needed:
    // without it the module works on its own. A launcher finds this class by name
    // ("BellumCivileCoopCompat.Hooks") and checks Version before using anything.
    //
    // ModderLords uses it to tell a realm's players a treaty's terms in chat, and to say when a
    // player ruler let a treaty vote run out, so the realm's council decides instead of the vote.
    public static class Hooks
    {
        /// <summary>Goes up when anything below changes shape.</summary>
        public const int Version = 1;

        /// <summary>
        /// Server: shows a line of text to every connected player in the realm. Null (the default)
        /// means nobody is told; the vote itself still shows in the kingdom screen with its tribute.
        /// </summary>
        public static Action<Kingdom, string> TellRealm { get; set; }

        /// <summary>
        /// Server: a launcher that runs the vote round itself calls this when the round runs out.
        /// True when the vote is one of this module's treaty votes; the launcher should then leave its
        /// outcome alone. Without a launcher, whatever Coop's round decided for a ruler who didn't vote stands.
        /// </summary>
        public static bool OnVoteDeadline(KingdomDecision decision, bool rulerVoted) => TreatyVote.OnDeadline(decision, rulerVoted);

        /// <summary>A short line of what the module did this session, for a launcher's log.</summary>
        public static string Summary() => PeaceInCoop.Summary() + VotePeace.Summary() + TreatyVote.Summary();
    }
}
