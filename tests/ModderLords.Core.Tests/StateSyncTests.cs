using ModderLords.CompatSync;
using ModderLords.Core.Compat.Authority;
using ModderLords.Coop.Compat;

namespace ModderLords.Core.Tests;

/// <summary>Static mod state from the server to clients: the classifier names the fields, the recipe carries them, the module round-trips them.</summary>
public sealed class StateSyncTests
{
    public enum Mood { Calm, Angry }

    public sealed class WorldState
    {
        public static int Momentum = 3;
        public static bool Winter;
        public static string Ruler = "none";
        public static Mood Temper = Mood.Calm;
        public static readonly int Fixed = 1;          // read-only: skipped
        public static List<int> Bag = new();           // unsupported kind: skipped
        public int NotStatic;
    }

    [Fact]
    public void RecipeStateSource_ResolvesStaticFields_CapturesAndApplies()
    {
        var src = new RecipeStateSource();
        var t = typeof(WorldState).FullName!;
        Assert.True(src.Add(t + ".Momentum"));
        Assert.False(src.Add(t + ".Momentum"));                                   // duplicates ignored
        Assert.True(src.Add(t + ".Winter")); Assert.True(src.Add(t + ".Ruler")); Assert.True(src.Add(t + ".Temper"));
        Assert.True(src.Add(t + ".Fixed")); Assert.True(src.Add(t + ".Bag")); Assert.True(src.Add(t + ".NotStatic"));
        Assert.True(src.Add("No.Such.Type.Field"));
        Assert.False(src.Add("nodot"));
        Assert.False(src.Present);

        src.Refresh();
        Assert.True(src.Present);
        Assert.Equal("4 field(s) in 1 type(s), 4 not found yet", src.Summary());   // Fixed, Bag, NotStatic, No.Such
        var id = RecipeStateSource.Prefix + t;
        Assert.True(src.Owns(id));
        Assert.False(src.Owns("static:" + t));

        WorldState.Momentum = 42; WorldState.Winter = true; WorldState.Ruler = "Derthert"; WorldState.Temper = Mood.Angry;
        var snap = Assert.Single(src.Capture());
        Assert.Equal(id, snap.SettingsId);
        Assert.Contains("Momentum\t42", snap.Payload);
        Assert.Contains("Temper\tAngry", snap.Payload);

        // A client applies the server's payload.
        WorldState.Momentum = 0; WorldState.Winter = false; WorldState.Ruler = ""; WorldState.Temper = Mood.Calm;
        var changed = src.Apply(id, ValueConverter.ParsePayload(snap.Payload), out var report);
        Assert.Equal(4, changed);
        Assert.Equal((42, true, "Derthert", Mood.Angry), (WorldState.Momentum, WorldState.Winter, WorldState.Ruler, WorldState.Temper));
        Assert.Equal(0, src.Apply("state:Nope", new Dictionary<string, string>(), out report));
        Assert.Equal("unknown state object", report);
        Assert.StartsWith("not persisted", src.Save(id));
        Assert.Empty(src.Describe());
    }

    [Fact]
    public void Classifier_NamesTheSharedFields_AndOnlyStaticSupportedOnesAreSyncable()
    {
        var (model, report) = AuthorityScanTests.Analysed;
        var momentum = report.Roots.Single(r => r.Root.Method.EndsWith("AuthBehavior::TickMomentum", StringComparison.Ordinal));
        Assert.Equal(AuthorityVerdict.NeedsStateSync, momentum.Verdict);
        var field = Assert.Single(momentum.SharedState!);
        Assert.EndsWith("AuthBehavior.Momentum", field);
        Assert.True(model.Fields[field].IsStatic);
        Assert.Equal("System.Int32", model.Fields[field].Type);
        Assert.Contains(field, report.SyncStateMembers);

        // The slider writes an instance property (AuthTownLimits.Max): named as shared state, not syncable.
        var slider = report.Roots.Single(r => r.Verdict == AuthorityVerdict.PlayerStateUnsynced && r.Root.Detail == "UI callback");
        Assert.Contains(slider.SharedState!, f => f.EndsWith("AuthTownLimits.<Max>k__BackingField", StringComparison.Ordinal));
        Assert.DoesNotContain(report.SyncStateMembers, f => f.Contains("AuthTownLimits", StringComparison.Ordinal));
        Assert.Null(report.Roots.First(r => r.Verdict == AuthorityVerdict.ServerOnly).SharedState);
    }

    [Fact]
    public void Recipe_CarriesSyncState_AndAModWithOnlyStateIsKept()
    {
        var report = new AuthorityReport
        {
            ModuleId = "Mod",
            Roots = [new RootVerdict(new AuthorityRoot("Mod.B::Tick", RootTrigger.Simulation, "test"), AuthorityVerdict.NeedsStateSync, "t", [], false) { SharedState = ["Mod.B.Count"] }],
            SyncStateMembers = ["Mod.B.Count"],
        };
        var scan = new ModderLords.Core.Compat.ScanResult("Mod", ModderLords.Core.Compat.ServerVerdict.ServerSafe, [], [], [], [], [], [], [], false);
        var set = RecipeSet.BuildDiagnostic([("Mod", scan, Array.Empty<string>())], "test", authority: new Dictionary<string, AuthorityReport> { ["Mod"] = report });
        var r = Assert.Single(set.Mods);
        Assert.Equal(["Mod.B.Count"], r.SyncState);
        Assert.Contains("1 static field(s) of mod state sent from the server to clients", r.Notes);
        Assert.Equal(["Mod.B.Count"], RecipeSet.FromJson(set.ToJson()).Mods[0].SyncState);
        Assert.Contains("\"SyncState\"", set.ToJson());
    }
}
