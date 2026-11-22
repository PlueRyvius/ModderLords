using ModderLords.CompatSync;

namespace ModderLords.Core.Tests;

/// <summary>
/// The settings scan read Retinues' WSettlement.Current (the settlement the player is in) every 3 s as a possible
/// settings object. On the map it threw inside Retinues, which logged the error and returned null, so the scan never
/// stopped (2026-11-22).
/// </summary>
public sealed class SettingsSingletonRulesTests
{
    [Theory]
    [InlineData("Instance")]
    [InlineData("Settings")]
    [InlineData("Config")]
    public void A_settings_like_singleton_name_is_evidence_by_itself(string name) =>
        Assert.True(SettingsSingletonRules.IsEvidenceByItself(name));

    [Theory]
    [InlineData("Current")]
    [InlineData("Default")]
    public void Current_and_Default_are_not_evidence_by_themselves(string name)
    {
        Assert.False(SettingsSingletonRules.IsEvidenceByItself(name));
        // A class whose name looks like settings is still reached through them.
        Assert.Contains(name, SettingsSingletonRules.SingletonNames);
    }

    [Fact]
    public void A_singleton_that_stays_null_is_given_up_after_two_minutes_of_ticks()
    {
        Assert.False(SettingsSingletonRules.GiveUp(SettingsSingletonRules.MaxNullReads - 1));
        Assert.True(SettingsSingletonRules.GiveUp(SettingsSingletonRules.MaxNullReads));
        Assert.Equal(40, SettingsSingletonRules.MaxNullReads);   // 40 reads at the 3 s settings tick
    }
}
