using System.Runtime.CompilerServices;
using HarmonyLib;
using ModderLords.CompatSync.Coop;

namespace ModderLords.Core.Tests;

/// <summary>RelayGates with real Harmony: what a client does with a recipe's Relays, without a game.</summary>
public sealed class RelayGatesTests
{
    public sealed class Orders
    {
        public static int Runs;
        [MethodImpl(MethodImplOptions.NoInlining)] public void Patrol(string town, int size) => Runs++;
    }

    // Separate from Orders so each test arms its own methods, whichever runs first.
    public sealed class NestedOrders
    {
        [MethodImpl(MethodImplOptions.NoInlining)] public void Sell(string town) { }
        [MethodImpl(MethodImplOptions.NoInlining)] public void OrderAndSell(string town) => Sell(town);
        [MethodImpl(MethodImplOptions.NoInlining)] public void Throws(string town) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void Only_the_outermost_relayed_call_is_sent_and_a_throwing_one_does_not_block_later_sends()
    {
        var sent = new List<string>();
        RelayGates.Configure(() => true, (id, _) => sent.Add(id));
        var harmony = new Harmony("test.relay.nested." + Guid.NewGuid().ToString("N"));
        var t = typeof(NestedOrders).FullName + "::";
        Assert.Equal((3, 0), RelayGates.Apply(harmony, [t + "Sell", t + "OrderAndSell", t + "Throws"], _ => { }));

        new NestedOrders().OrderAndSell("town_V1");
        Assert.Equal([t + "OrderAndSell"], sent);            // Sell ran inside it and was not sent again

        Assert.Throws<InvalidOperationException>(() => new NestedOrders().Throws("town_V1"));
        new NestedOrders().Sell("town_V1");
        Assert.Equal([t + "OrderAndSell", t + "Throws", t + "Sell"], sent);   // the throw unwound the nesting count
    }

    [Fact]
    public void A_client_sends_the_call_and_still_runs_it_the_server_and_a_relayed_run_do_not_send()
    {
        var sent = new List<(string id, object?[] args)>();
        var client = true;
        RelayGates.Configure(() => client, (id, args) => sent.Add((id, args)));
        var harmony = new Harmony("test.relay." + Guid.NewGuid().ToString("N"));
        var id = typeof(Orders).FullName + "::Patrol";
        var warnings = new List<string>();

        var (applied, missing) = RelayGates.Apply(harmony, [id, "No.Such.Type::Patrol"], warnings.Add);
        Assert.Equal(1, applied);
        Assert.Equal(1, missing);
        Assert.Single(warnings, w => w.Contains("No.Such.Type::Patrol"));

        Orders.Runs = 0;
        new Orders().Patrol("town_V1", 3);
        Assert.Equal(1, Orders.Runs);                          // the player's own game still runs it
        var s = Assert.Single(sent);
        Assert.Equal(id, s.id);
        Assert.Equal(new object?[] { "town_V1", 3 }, s.args);

        client = false;
        new Orders().Patrol("town_V1", 3);
        Assert.Single(sent);                                   // the server never sends

        client = true;
        using (RelayGates.Relaying()) new Orders().Patrol("town_V1", 3);
        Assert.Single(sent);                                   // nor does a call the relay itself is running
        Assert.Equal(3, Orders.Runs);
        Assert.Contains("Patrol=1", RelayGates.CountsSummary());
    }
}
