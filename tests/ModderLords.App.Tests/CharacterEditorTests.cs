using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using ModderLords.App.ViewModels;
using ModderLords.Coop.Admin;
using Xunit;

namespace ModderLords.App.Tests;

/// <summary>
/// The editor against a fake server that answers on the console channel the way the module does: load, change values,
/// and only the changed ones are sent, as new values.
/// </summary>
public class CharacterEditorTests
{
    private const string Hero = """
        "steamId":"7656","heroId":"Hero_Player","name":"Aldric","culture":"Vlandia","age":27,"female":false,"level":12,
        "gold":1000,"hp":90,"maxHp":110,"unspentAttr":1,"unspentFocus":4,"maxAttribute":10,"maxFocus":5,"maxSkill":330,
        "busy":"","perks":7,
        "attributes":[{"id":"vigor","name":"Vigor","value":4},{"id":"control","name":"Control","value":3}],
        "skills":[{"id":"OneHanded","name":"One Handed","attribute":"vigor","value":120,"focus":3},{"id":"Bow","name":"Bow","attribute":"control","value":10,"focus":0}],
        "traits":[{"id":"Mercy","name":"Mercy","value":0,"min":-2,"max":2,"personality":true},{"id":"Surgery","name":"Surgery","value":0,"min":0,"max":20,"personality":false}]
        """;

    /// <summary>A server that answers hero/edit requests with the hero above and records what was sent.</summary>
    private sealed class FakeServer
    {
        public List<string> Sent { get; } = [];
        public AdminClient Client { get; }

        public FakeServer() => Client = new AdminClient(line =>
        {
            Sent.Add(line);
            var m = Regex.Match(line, @"^modderlords\.(\w+) (\S+)");
            var extra = m.Groups[1].Value == "edit" ? ",\"changes\":[\"Gold 1000 -> 5000\"],\"notApplied\":[]" : "";
            Client!.Observe($"@ML@{{\"ev\":\"{m.Groups[1].Value}\",\"req\":\"{m.Groups[2].Value}\",\"ok\":true,{Hero}{extra}}}");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Only_the_changed_values_are_sent_as_new_values() => Sta(() =>
    {
        var server = new FakeServer();
        var vm = new CharacterEditorViewModel(server.Client, "7656", "Aldric", () => true);
        vm.ReloadCommand.ExecuteAsync(null).Pump();

        Assert.Equal("Edit Aldric", vm.Title);
        Assert.Equal(["Vigor", "Control"], vm.Attributes.Select(a => a.Name));
        Assert.Equal("Vigor", vm.Skills[0].Attribute);
        // Personality traits show; bookkeeping ones wait behind the toggle.
        Assert.Equal(["Mercy"], vm.TraitsView.Cast<EditValue>().Select(t => t.Name));
        vm.ShowHiddenTraits = true;
        Assert.Equal(2, vm.TraitsView.Cast<EditValue>().Count());
        Assert.Empty(vm.Tokens());

        vm.Values.Single(v => v.Id == "gold").Value = 5000;
        vm.Attributes.Single(a => a.Id == "vigor").Value = 6;
        vm.Skills.Single(s => s.Id == "Bow").Focus = 2;
        vm.Traits.Single(t => t.Id == "Mercy").Value = -1;
        Assert.Equal(["gold=5000", "attr.vigor=6", "focus.Bow=2", "trait.Mercy=-1"], vm.Tokens());

        vm.ApplyCommand.ExecuteAsync(null).Pump();
        Assert.Equal("modderlords.edit r2 7656 gold=5000 attr.vigor=6 focus.Bow=2 trait.Mercy=-1", server.Sent[^1]);
        Assert.StartsWith("Applied 1 change(s)", vm.Status);
    });

    [Fact]
    public void A_value_out_of_range_is_not_sent() => Sta(() =>
    {
        var server = new FakeServer();
        var vm = new CharacterEditorViewModel(server.Client, "7656", "Aldric", () => true);
        vm.ReloadCommand.ExecuteAsync(null).Pump();
        vm.Attributes.Single(a => a.Id == "vigor").Value = 11;
        vm.Skills.Single(s => s.Id == "OneHanded").Value = 400;

        vm.ApplyCommand.ExecuteAsync(null).Pump();
        Assert.Single(server.Sent); // just the load
        Assert.Contains("Vigor must be 0 to 10", vm.Status);
        Assert.Contains("One Handed: skill 0 to 330", vm.Status);
    });

    [Fact]
    public void Nothing_is_sent_while_the_server_is_down() => Sta(() =>
    {
        var server = new FakeServer();
        var vm = new CharacterEditorViewModel(server.Client, "7656", "Aldric", () => false);
        vm.ReloadCommand.ExecuteAsync(null).Pump();
        Assert.Empty(server.Sent);
        Assert.Equal("The server is not running.", vm.Status);
    });

    /// <summary>Runs on an STA thread with a dispatcher, as the app does, so awaits come back to the thread that owns the views.</summary>
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { action(); } catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "UI test did not finish");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

internal static class DispatcherPump
{
    /// <summary>Waits for the task while the dispatcher keeps running, the way the app's message loop would.</summary>
    public static void Pump(this Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}
