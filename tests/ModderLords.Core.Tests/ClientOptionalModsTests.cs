using ModderLords.Core.Overlay;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>How the launcher names the "Client optional" mods to the server, and the role's place among the others.</summary>
public sealed class ClientOptionalModsTests
{
    [Fact]
    public void Ids_survive_the_trip_to_the_server_and_compare_without_case()
    {
        var sent = ClientOptionalMods.Format(["BetterCamera", " Heal on Kill ", "RTSCamera.CommandSystem", "bettercamera", ""]);
        Assert.Equal("BetterCamera|Heal on Kill|RTSCamera.CommandSystem", sent);

        var read = ClientOptionalMods.Parse(sent);
        Assert.Equal(3, read.Count);
        Assert.Contains("heal on kill", read);
        Assert.Contains("RTSCAMERA.COMMANDSYSTEM", read);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("||")]
    public void Nothing_named_is_nothing_relaxed(string? value) => Assert.Empty(ClientOptionalMods.Parse(value));

    [Fact]
    public void An_id_that_could_be_read_as_two_is_not_sent() =>
        Assert.Equal("Good", ClientOptionalMods.Format(["Good", "Bad|Worse"]));

    [Fact]
    public void The_role_has_its_own_label_and_round_trips_through_the_drop_down()
    {
        Assert.Equal("Client optional", RoleLabels.For(ServerRole.ClientOptional));
        Assert.Equal(RoleChoice.ClientOptional, RoleLabels.Choice(ServerRole.ClientOptional, serverOnlyLogic: false, experimental: false));
        Assert.Equal(RoleChoice.ClientOptional, RoleLabels.Choice(ServerRole.ClientOptional, serverOnlyLogic: true, experimental: true));
        Assert.Equal(ServerRole.ClientOptional, RoleLabels.Role(RoleChoice.ClientOptional));
    }

    [Fact]
    public void The_stored_values_of_the_older_roles_did_not_move()
    {
        Assert.Equal(0, (int)ServerRole.Run);
        Assert.Equal(1, (int)ServerRole.DependencyOnly);
        Assert.Equal(2, (int)ServerRole.AsShipped);
        Assert.Equal(3, (int)ServerRole.ClientOptional);
    }
}
