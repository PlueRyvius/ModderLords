using ModderLords.CompatSync.Coop;

namespace ModderLords.Core.Tests;

public sealed class RelayCoalescerTests
{
    private static readonly DateTime T0 = new(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_dragged_slider_sends_only_its_last_value_once_it_settles()
    {
        var c = new RelayCoalescer(TimeSpan.FromMilliseconds(300));
        // The 2026-09-14 log: SetTownMaxUpgradeTier 8, 7, 6 … 1 within a quarter of a second.
        for (var v = 8; v >= 1; v--)
            c.Add("TrainingSettings::SetTownMaxUpgradeTier", ["Town", "i"], ["town_ES1", v.ToString()], T0.AddMilliseconds(30 * (8 - v)));

        Assert.Empty(c.Due(T0.AddMilliseconds(250)));        // still being dragged
        var due = Assert.Single(c.Due(T0.AddMilliseconds(600)));
        Assert.Equal(["town_ES1", "1"], due.values);
        Assert.Equal(0, c.PendingCount);
    }

    [Fact]
    public void Different_actions_and_towns_are_kept_apart_in_the_order_they_were_first_touched()
    {
        var c = new RelayCoalescer(TimeSpan.FromMilliseconds(300));
        c.Add("MobileGarrisonSettings::OrderMobileGarrisonToPatrol", ["Town"], ["town_A"], T0);
        c.Add("TrainingSettings::SetTownMaxUpgradeTier", ["Town", "i"], ["town_A", "3"], T0.AddMilliseconds(10));
        c.Add("TrainingSettings::SetTownMaxUpgradeTier", ["Town", "i"], ["town_B", "4"], T0.AddMilliseconds(20));
        c.Add("TrainingSettings::SetTownMaxUpgradeTier", ["Town", "i"], ["town_A", "5"], T0.AddMilliseconds(30));

        var due = c.Due(T0.AddSeconds(1));
        Assert.Equal(["MobileGarrisonSettings::OrderMobileGarrisonToPatrol", "TrainingSettings::SetTownMaxUpgradeTier", "TrainingSettings::SetTownMaxUpgradeTier"],
            due.Select(d => d.method));
        Assert.Equal(["town_A", "5"], due[1].values);
        Assert.Equal(["town_B", "4"], due[2].values);
    }
}
