using System.IO;
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
    public void A_new_name_is_sent_as_its_own_command_after_the_values() => Sta(() =>
    {
        var server = new FakeServer();
        var vm = new CharacterEditorViewModel(server.Client, "7656", "Aldric", () => true);
        vm.ReloadCommand.ExecuteAsync(null).Pump();
        Assert.Equal("Aldric", vm.Name);
        Assert.False(vm.IsNameChanged);

        vm.Name = " Aldric the Bold ";
        vm.Values.Single(v => v.Id == "gold").Value = 5000;
        vm.ApplyCommand.ExecuteAsync(null).Pump();

        Assert.Equal("modderlords.edit r2 7656 gold=5000", server.Sent[^2]);
        Assert.Equal("modderlords.rename r3 7656 " + HeroNames.Encode("Aldric the Bold"), server.Sent[^1]);
    });

    [Fact]
    public void A_name_the_game_could_not_show_is_not_sent() => Sta(() =>
    {
        var server = new FakeServer();
        var vm = new CharacterEditorViewModel(server.Client, "7656", "Aldric", () => true);
        vm.ReloadCommand.ExecuteAsync(null).Pump();

        vm.Name = "   ";
        vm.ApplyCommand.ExecuteAsync(null).Pump();
        Assert.Single(server.Sent); // just the load
        Assert.Contains("cannot be empty", vm.Status);
    });

    [Fact]
    public void Perks_are_reset_only_after_the_host_confirms_and_a_companion_is_named_by_hero_id() => Sta(() =>
    {
        var server = new FakeServer();
        var vm = new CharacterEditorViewModel(server.Client, HeroTargets.Hero("wanderer_3"), "Bera", () => true);
        vm.ReloadCommand.ExecuteAsync(null).Pump();
        Assert.Equal("modderlords.hero r1 hero:wanderer_3", server.Sent[^1]);
        Assert.Equal(7, vm.PerkCount);

        vm.Confirm = _ => false;
        vm.ResetPerksCommand.ExecuteAsync(null).Pump();
        Assert.Single(server.Sent);

        vm.Confirm = _ => true;
        vm.ResetPerksCommand.ExecuteAsync(null).Pump();
        Assert.Equal("modderlords.resetperks r2 hero:wanderer_3", server.Sent[^1]);
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

    private const string Dna = "<BodyProperties version=\\\"4\\\" age=\\\"27\\\" weight=\\\"0.5\\\" build=\\\"0.5\\\" key=\\\"00AA\\\" />";

    /// <summary>A server that knows two heroes, each the other's spouse, and answers for whichever is asked.</summary>
    private sealed class FamilyServer
    {
        public List<string> Sent { get; } = [];
        public AdminClient Client { get; }

        private static string HeroJson(string id, string name, bool female, string spouseId, string spouseName) =>
            $"\"steamId\":\"\",\"heroId\":\"{id}\",\"name\":\"{name}\",\"female\":{(female ? "true" : "false")},\"age\":30,\"level\":5,\"clan\":\"Hemphill\","
            + $"\"alive\":true,\"body\":\"{Dna}\",\"perks\":0,\"attributes\":[],\"skills\":[],\"traits\":[],"
            + $"\"spouse\":{{\"heroId\":\"{spouseId}\",\"name\":\"{spouseName}\",\"female\":{(female ? "false" : "true")},\"age\":31,\"alive\":true,\"clan\":\"Hemphill\"}},"
            + "\"children\":[],\"siblings\":[]";

        public FamilyServer() => Client = new AdminClient(line =>
        {
            Sent.Add(line);
            var m = Regex.Match(line, @"^modderlords\.(\w+) (\S+) (\S+)");
            var hero = m.Groups[3].Value == "hero:mira" ? HeroJson("mira", "Mira", true, "aldric", "Aldric") : HeroJson("aldric", "Aldric", false, "mira", "Mira");
            Client!.Observe($"@ML@{{\"ev\":\"{m.Groups[1].Value}\",\"req\":\"{m.Groups[2].Value}\",\"ok\":true,{hero},\"changes\":[\"x\"],\"notApplied\":[]}}");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Gender_and_dna_are_sent_and_a_relative_opens_in_the_same_editor() => Sta(() =>
    {
        var server = new FamilyServer();
        var vm = new CharacterEditorViewModel(server.Client, HeroTargets.Hero("aldric"), "Aldric", () => true);
        vm.ReloadCommand.ExecuteAsync(null).Pump();
        Assert.Equal(0, vm.GenderIndex);
        Assert.False(vm.IsBodyChanged);
        Assert.Equal("Mira", Assert.Single(vm.Spouses).Name);
        Assert.Equal("hero:mira", Assert.Single(vm.FaceSources).Target);

        vm.GenderIndex = 1;
        vm.ReadClipboard = () => "<BodyProperties version=\"4\" age=\"20\"\r\n weight=\"0.1\" build=\"0.9\" key=\"00BB\" />";
        vm.PasteBodyCommand.Execute(null);
        Assert.True(vm.IsBodyChanged);
        Assert.Equal(["female=1"], vm.Tokens());

        vm.ApplyCommand.ExecuteAsync(null).Pump();
        Assert.Equal("modderlords.edit r2 hero:aldric female=1", server.Sent[^2]);
        Assert.Equal("modderlords.body r3 hero:aldric " + HeroBodies.Encode("<BodyProperties version=\"4\" age=\"20\" weight=\"0.1\" build=\"0.9\" key=\"00BB\" />"), server.Sent[^1]);

        // Unapplied changes are only thrown away once the host says so.
        vm.Name = "Aldric the Bold";
        vm.Confirm = _ => false;
        vm.OpenRelativeCommand.ExecuteAsync(vm.Spouses[0]).Pump();
        Assert.Equal("hero:aldric", vm.Target);
        vm.Confirm = _ => true;
        vm.OpenRelativeCommand.ExecuteAsync(vm.Spouses[0]).Pump();
        Assert.Equal("hero:mira", vm.Target);
        Assert.Equal("Mira", vm.OriginalName);
        Assert.Equal(1, vm.GenderIndex);
        Assert.True(vm.BackCommand.CanExecute(null));
        vm.BackCommand.ExecuteAsync(null).Pump();
        Assert.Equal("hero:aldric", vm.Target);
        Assert.False(vm.BackCommand.CanExecute(null));
    });

    [Fact]
    public void Text_that_is_not_dna_is_not_used() => Sta(() =>
    {
        var server = new FamilyServer();
        var vm = new CharacterEditorViewModel(server.Client, HeroTargets.Hero("aldric"), "Aldric", () => true);
        vm.ReloadCommand.ExecuteAsync(null).Pump();
        vm.ReadClipboard = () => "just some text";
        vm.PasteBodyCommand.Execute(null);
        Assert.False(vm.IsBodyChanged);
        Assert.StartsWith("Not used", vm.Status);
    });

    [Fact]
    public void A_face_saved_to_the_library_can_be_put_on_another_hero() => Sta(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "ml-faces-" + Guid.NewGuid().ToString("N"));
        try
        {
            var server = new FamilyServer();
            var vm = new CharacterEditorViewModel(server.Client, HeroTargets.Hero("aldric"), "Aldric", () => true) { Library = new FaceLibrary(Path.Combine(dir, "faces.json")) };
            vm.ReloadCommand.ExecuteAsync(null).Pump();
            vm.LoadLibrary();
            Assert.Empty(vm.LibraryFaces);

            vm.SaveToLibraryCommand.Execute(null);
            Assert.StartsWith("Not saved", vm.Status);
            vm.LibraryName = "Aldric's face";
            vm.SaveToLibraryCommand.Execute(null);
            Assert.Equal("Aldric's face", vm.LibraryFace!.Name);
            Assert.False(vm.LibraryFace.Female);

            // Saving under the same name again asks first.
            vm.LibraryName = "aldric's FACE";
            vm.Confirm = _ => false;
            vm.SaveToLibraryCommand.Execute(null);
            Assert.Equal("Aldric's face", Assert.Single(vm.LibraryFaces).Name);

            // On Mira, the saved face goes in the box; Apply would send it.
            vm.Confirm = _ => true;
            vm.OpenRelativeCommand.ExecuteAsync(vm.Spouses[0]).Pump();
            vm.LoadLibrary();
            vm.LibraryFace = vm.LibraryFaces[0];
            vm.Body = "<BodyProperties version=\"4\" key=\"00CC\" />";
            vm.UseLibraryFaceCommand.Execute(null);
            Assert.Contains("00AA", vm.Body);
            Assert.Contains("set Gender to match", vm.Status);

            vm.DeleteLibraryFaceCommand.Execute(null);
            Assert.Empty(vm.LibraryFaces);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
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
