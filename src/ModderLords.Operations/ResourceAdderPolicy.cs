using System;
using System.Collections.Generic;

namespace ModderLords.Operations;

public sealed class ResourceAdderPolicy
{
    private readonly HashSet<string> playerClans = new HashSet<string>(StringComparer.Ordinal);
    private object? awardedCampaign;
    private long awardedDay = long.MinValue;
    public bool OwnershipReady { get; private set; }
    public bool UpdateOwners(IEnumerable<string?>? registeredClans)
    {
        OwnershipReady = false; playerClans.Clear();
        if (registeredClans == null) return false;
        foreach (var clan in registeredClans)
        {
            if (string.IsNullOrEmpty(clan)) { playerClans.Clear(); return false; }
            playerClans.Add(clan!);
        }
        // An empty registry may be a world still loading. Never interpret it as "all AI".
        return OwnershipReady = playerClans.Count > 0;
    }
    public bool MayRun(bool coopSession, bool campaignAuthority) => !coopSession || campaignAuthority && OwnershipReady;
    public bool TryBeginAward(bool coopSession, bool campaignAuthority, object? campaign, long day)
    {
        if (!coopSession) return true;
        if (!MayRun(true, campaignAuthority) || campaign == null) return false;
        if (ReferenceEquals(awardedCampaign, campaign) && awardedDay == day) return false;
        // Reserve before entering third-party code. A partially failed award must not be retried automatically.
        awardedCampaign = campaign; awardedDay = day; return true;
    }
    public bool IsAiClan(string clanId, bool coopSession, bool originalResult)
        => !coopSession ? originalResult : OwnershipReady && !string.IsNullOrEmpty(clanId) && !playerClans.Contains(clanId);
}
