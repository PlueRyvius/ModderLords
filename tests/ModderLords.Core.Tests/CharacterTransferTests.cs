using System.Collections.Concurrent;
using ModderLords.CompatSync.Coop.Admin;
using ModderLords.Coop.Admin;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>
/// Character export/import without a server: the file format both ends read, the sanitizer that cleans a character
/// down to what another world holds, and the launcher's half (export text, staging a file, the import commands).
/// </summary>
public class CharacterTransferTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ml-chars-" + Guid.NewGuid().ToString("N"));

    public CharacterTransferTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp dir */ } }

    private static CharacterFile Aldric() => new()
    {
        ExportedAt = "2026-10-01T23:00:00.0000000Z",
        Modules = [new("Native", "v1.4.8"), new("RBM", "v4.1.0")],
        Name = "Aldric the Bold",
        FirstName = "Aldric",
        Female = false,
        Age = 27.5,
        Culture = "vlandia",
        Body = "<BodyProperties version=\"4\" age=\"27.5\" weight=\"0.4\" build=\"0.6\" key=\"0011\" />",
        Level = 14,
        Gold = 25300,
        UnspentAttributePoints = 1,
        UnspentFocusPoints = 2,
        Attributes = new() { ["vigor"] = 6, ["control"] = 4 },
        Skills = new() { ["OneHanded"] = 175, ["Bow"] = 40, ["RBM_Alchemy"] = 30 },
        Focus = new() { ["OneHanded"] = 5, ["Bow"] = 1, ["RBM_Alchemy"] = 2 },
        Traits = new() { ["Mercy"] = 1, ["Valor"] = 3 },
        Perks = ["OneHandedDuelist", "RBMPerk_Fury"],
        Battle =
        [
            new() { Slot = "Weapon0", Item = "vlandia_sword_1_t2", Modifier = "masterwork" },
            new() { Slot = "Weapon1", Item = "rbm_special_shield", Modifier = "" },
            new() { Slot = "Body", Item = "mail_shirt", Modifier = "rbm_reinforced" },
            new() { Slot = "Horse", Item = "", Modifier = "" },
        ],
        Civilian = [new() { Slot = "Body", Item = "tunic", Modifier = "" }],
    };

    /// <summary>A vanilla world: no RBM skill, perk, item or modifier, and the usual limits.</summary>
    private sealed class VanillaWorld : ICharacterWorld
    {
        public bool HasAttribute(string id) => id is "vigor" or "control";
        public bool HasSkill(string id) => id is "OneHanded" or "Bow";
        public (int Min, int Max)? Trait(string id) => id switch { "Mercy" => (-2, 2), "Valor" => (-2, 2), _ => null };
        public bool HasPerk(string id) => id == "OneHandedDuelist";
        public bool HasItem(string id) => id is "vlandia_sword_1_t2" or "mail_shirt" or "tunic";
        public bool HasItemModifier(string id) => id == "masterwork";
        public bool HasSlot(string slot) => slot is "Weapon0" or "Weapon1" or "Body" or "Horse";
        public bool HasCulture(string id) => id == "vlandia";
        public int MaxAttribute => 10;
        public int MaxFocus => 5;
        public int MaxSkill => 330;
    }

    [Fact]
    public void A_file_reads_back_as_it_was_written()
    {
        var back = CharacterFile.Parse(Aldric().ToJson());
        Assert.Equal("Aldric the Bold", back.Name);
        Assert.Equal(27.5, back.Age);
        Assert.Equal(Aldric().Body, back.Body);
        Assert.Equal(175, back.Skills["OneHanded"]);
        Assert.Equal(5, back.Focus["OneHanded"]);
        Assert.Equal(["OneHandedDuelist", "RBMPerk_Fury"], back.Perks);
        Assert.Equal("masterwork", back.Battle[0].Modifier);
        Assert.Equal("", back.Battle[3].Item);
        Assert.Equal(new KeyValuePair<string, string>("RBM", "v4.1.0"), back.Modules[1]);
    }

    [Theory]
    [InlineData("not json at all", "not valid JSON")]
    [InlineData("{\"kind\":\"something else\",\"schema\":1}", "not a ModderLords character file")]
    [InlineData("{\"kind\":\"ModderLords character\",\"schema\":2}", "Update ModderLords")]
    public void A_file_that_is_not_one_this_version_reads_says_why(string text, string reason)
    {
        var ex = Assert.Throws<FormatException>(() => CharacterFile.Parse(text));
        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public void A_character_from_a_modded_world_is_cleaned_down_to_what_a_vanilla_world_holds()
    {
        var notes = new List<string>();
        var clean = CharacterSanitizer.Sanitize(Aldric(), new VanillaWorld(), notes);

        // Kept: everything vanilla, unchanged.
        Assert.Equal(new Dictionary<string, int> { ["OneHanded"] = 175, ["Bow"] = 40 }, clean.Skills);
        Assert.Equal(new Dictionary<string, int> { ["OneHanded"] = 5, ["Bow"] = 1 }, clean.Focus);
        Assert.Equal(["OneHandedDuelist"], clean.Perks);
        Assert.Equal(25300, clean.Gold);
        Assert.Equal("vlandia", clean.Culture);

        // Gear: the RBM shield's slot is left alone, the RBM modifier falls back to the plain item, an empty slot stays empty.
        Assert.Equal(["Weapon0", "Body", "Horse"], clean.Battle.Select(s => s.Slot));
        Assert.Equal(("mail_shirt", ""), (clean.Battle[1].Item, clean.Battle[1].Modifier));
        Assert.Equal("masterwork", clean.Battle[0].Modifier);

        // And every change is said.
        Assert.Contains(notes, n => n.Contains("skill RBM_Alchemy") && n.Contains("dropped"));
        Assert.Contains(notes, n => n.Contains("RBMPerk_Fury"));
        Assert.Contains(notes, n => n.Contains("rbm_special_shield"));
        Assert.Contains(notes, n => n.Contains("modifier rbm_reinforced"));
        Assert.Contains(notes, n => n.Contains("trait Valor") && n.Contains("set to 2"));
        Assert.Equal(2, clean.Traits["Valor"]);
        Assert.Equal(5, notes.Count);
    }

    [Fact]
    public void Values_past_the_worlds_limits_are_brought_within_them()
    {
        var c = Aldric();
        c.Attributes["vigor"] = 14;
        c.Skills["OneHanded"] = 500;
        c.Focus["Bow"] = 9;
        c.Gold = -50;
        c.Age = 9;
        c.Name = "{PLAYER.NAME} Aldric";
        var notes = new List<string>();
        var clean = CharacterSanitizer.Sanitize(c, new VanillaWorld(), notes);

        Assert.Equal(10, clean.Attributes["vigor"]);
        Assert.Equal(330, clean.Skills["OneHanded"]);
        Assert.Equal(5, clean.Focus["Bow"]);
        Assert.Equal(0, clean.Gold);
        Assert.Equal(18, clean.Age);
        // Braces would make the name a text template the game expands.
        Assert.Equal("PLAYER.NAME Aldric", clean.Name);
        Assert.Contains(notes, n => n.StartsWith("name:"));
    }

    [Fact]
    public void A_culture_this_world_lacks_is_left_to_the_character()
    {
        var c = Aldric();
        c.Culture = "rbm_elves";
        var notes = new List<string>();
        Assert.Equal("", CharacterSanitizer.Sanitize(c, new VanillaWorld(), notes).Culture);
        Assert.Contains(notes, n => n.Contains("culture rbm_elves"));
    }

    // ---- the launcher's half ---------------------------------------------------------------------------------

    private sealed class FakeServer
    {
        public ConcurrentQueue<string> Sent { get; } = new();
        public Func<string, string> Answer { get; set; } = _ => "";
        public AdminClient Client { get; }

        public FakeServer() => Client = new AdminClient(line =>
        {
            Sent.Enqueue(line);
            var req = line.Split(' ')[1];
            Client!.Observe(Answer(req));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task An_exported_character_is_saved_as_a_file_the_server_reads_back()
    {
        var server = new FakeServer
        {
            Answer = req => AdminWire.Reply("export", req, null, new Dictionary<string, object?> { ["character"] = Aldric().ToJsonObject() }),
        };
        var text = await new CharacterTransferClient(server.Client, _dir).ExportAsync("7656");

        Assert.Contains("\n", text); // indented, for a person to read
        var back = CharacterFile.Parse(text);
        Assert.Equal("Aldric the Bold", back.Name);
        Assert.Equal("modderlords.export r1 7656", Assert.Single(server.Sent));
    }

    [Fact]
    public async Task An_import_is_staged_checked_and_applied_by_token()
    {
        var server = new FakeServer
        {
            Answer = req => AdminWire.Reply("import", req, null, new Dictionary<string, object?>
            {
                ["name"] = "Mira", ["level"] = 2,
                ["notes"] = new List<object?> { "skill RBM_Alchemy: not in this world, dropped" },
                ["applied"] = new List<object?>(),
                ["source"] = new Dictionary<string, object?>
                {
                    ["name"] = "Aldric the Bold", ["level"] = 14, ["exportedAt"] = "2026-10-01T23:00:00Z", ["skills"] = 2, ["perks"] = 1,
                    ["missingModules"] = new List<object?> { "RBM" },
                },
            }),
        };
        var transfer = new CharacterTransferClient(server.Client, _dir);
        var token = transfer.Stage(Aldric().ToJson());

        // The staged file is exactly what the server will read.
        var staged = Path.Combine(_dir, "ModderLords", "imports", token + ".json");
        Assert.Equal("Aldric the Bold", CharacterFile.Parse(File.ReadAllText(staged)).Name);

        var check = await transfer.CheckAsync("7656", token);
        Assert.Equal($"modderlords.import r1 7656 {token} check", server.Sent.Last());
        Assert.Equal("Aldric the Bold", check.SourceName);
        Assert.Equal(["RBM"], check.MissingModules);
        Assert.Single(check.Notes);
        Assert.Equal("Mira", check.Hero.Name);

        await transfer.ApplyAsync("7656", token, ["stats", "gear"]);
        Assert.Equal($"modderlords.import r2 7656 {token} stats,gear", server.Sent.Last());

        transfer.Discard(token);
        Assert.False(File.Exists(staged));
    }

    [Fact]
    public void A_file_that_is_not_a_character_is_never_staged()
    {
        var transfer = new CharacterTransferClient(new AdminClient(_ => Task.CompletedTask), _dir);
        Assert.Throws<InvalidOperationException>(() => transfer.Stage("{\"kind\":\"something else\",\"schema\":1}"));
        Assert.Throws<InvalidOperationException>(() => transfer.Stage("{\"kind\":\"ModderLords character\",\"schema\":2}"));
        Assert.Throws<InvalidOperationException>(() => transfer.Stage("not json"));
        Assert.False(Directory.Exists(Path.Combine(_dir, "ModderLords", "imports")) && Directory.EnumerateFiles(Path.Combine(_dir, "ModderLords", "imports")).Any());
    }

    [Fact]
    public async Task Importing_nothing_or_an_unknown_part_is_refused_before_sending()
    {
        var sent = 0;
        var transfer = new CharacterTransferClient(new AdminClient(_ => { sent++; return Task.CompletedTask; }), _dir);
        await Assert.ThrowsAsync<ArgumentException>(() => transfer.ApplyAsync("7656", "abc", []));
        await Assert.ThrowsAsync<ArgumentException>(() => transfer.ApplyAsync("7656", "abc", ["everything"]));
        Assert.Equal(0, sent);
    }

    [Fact]
    public void Both_ends_offer_the_same_parts_and_name_the_file_kind_the_same()
    {
        Assert.Equal(CharacterFile.Kind, CharacterTransferClient.FileKind);
        Assert.Equal(CharacterFile.CurrentSchema, CharacterTransferClient.SupportedSchema);
    }
}
