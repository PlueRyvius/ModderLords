using System.Xml;
using ModularCoop.Core.Export;
using ModularCoop.Core.Modules;
using Xunit;

namespace ModularCoop.Core.Tests;

/// <summary>
/// Everything here runs on temp copies of a LauncherData.xml shaped exactly like the real one (both mod lists plus
/// the launcher's DLL allow-list), so no game install is needed and the real file is never touched.
/// </summary>
public class LauncherDataSyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mc-lds-" + Guid.NewGuid().ToString("N"));

    public LauncherDataSyncTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp dir */ } }

    private string BackupRoot => Path.Combine(_dir, "backups");

    private static string Mod(string id, string version, bool selected) =>
        $"    <UserModData>\n      <Id>{id}</Id>\n      <LastKnownVersion>{version}</LastKnownVersion>\n      <IsSelected>{(selected ? "true" : "false")}</IsSelected>\n    </UserModData>";

    /// <summary>The real file's shape: GameType, the singleplayer list, a separate multiplayer list, and DLLCheckData.</summary>
    private string WriteFile(params string[] mods)
    {
        var path = Path.Combine(_dir, "LauncherData.xml");
        File.WriteAllText(path,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<UserData xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n" +
            "  <GameType>Singleplayer</GameType>\n" +
            "  <SingleplayerData>\n    <ModDatas>\n" + string.Join("\n", mods) + "\n    </ModDatas>\n  </SingleplayerData>\n" +
            "  <MultiplayerData>\n    <ModDatas>\n" + Mod("Native", "v1.4.8.119303", true) + "\n" + Mod("Multiplayer", "v1.4.8.119303", true) + "\n    </ModDatas>\n  </MultiplayerData>\n" +
            "  <DLLCheckData>\n    <DLLData>\n      <DLLCheckData>\n        <DLLName>Coop.dll</DLLName>\n        <DLLVerifyInformation />\n        <LatestSizeInBytes>123</LatestSizeInBytes>\n        <IsDangerous>true</IsDangerous>\n      </DLLCheckData>\n    </DLLData>\n  </DLLCheckData>\n" +
            "</UserData>\n");
        return path;
    }

    private static ClientManifest.Entry E(string id, string version) => new(id, version, null);
    private static LoadOrder.Result Order(params string[] ids) => new(ids, []);

    private static IReadOnlyList<(string Id, bool Selected)> Read(string path)
    {
        var doc = new XmlDocument();
        doc.Load(path);
        var list = new List<(string, bool)>();
        foreach (XmlElement m in doc.SelectNodes("/UserData/SingleplayerData/ModDatas/UserModData")!)
            list.Add((m.SelectSingleNode("Id")!.InnerText, m.SelectSingleNode("IsSelected")!.InnerText == "true"));
        return list;
    }

    private static string Section(string path, string xpath)
    {
        var doc = new XmlDocument();
        doc.Load(path);
        return doc.SelectSingleNode(xpath)!.OuterXml;
    }

    [Fact]
    public void Ticks_a_server_mod_and_unticks_an_extra()
    {
        var path = WriteFile(Mod("Native", "v1.4.8", true), Mod("CoopNightly", "v0.1.4", true),
                             Mod("ImprovedGarrisons", "v1.2.3", false), Mod("BetterPatrols", "v2.0", true));
        var plan = LauncherDataSync.ComputePlan([E("ImprovedGarrisons", "v1.2.3")], Order("ImprovedGarrisons"), path);

        Assert.Contains(plan.Changes, c => c.Id == "ImprovedGarrisons" && c.Action == LauncherDataSync.SyncAction.Enable);
        Assert.Contains(plan.Changes, c => c.Id == "BetterPatrols" && c.Action == LauncherDataSync.SyncAction.Disable);

        var result = LauncherDataSync.Apply(plan, path, BackupRoot);
        Assert.NotNull(result);
        Assert.Equal(1, result!.Enabled);
        Assert.Equal(1, result.Disabled);
        var after = Read(path).ToDictionary(x => x.Id, x => x.Selected);
        Assert.True(after["ImprovedGarrisons"]);
        Assert.False(after["BetterPatrols"]);
        Assert.True(after["Native"]);
        Assert.True(after["CoopNightly"]);
    }

    [Fact]
    public void Officials_are_never_touched_even_when_the_server_lists_them()
    {
        var path = WriteFile(Mod("Native", "v1.4.8", true), Mod("StoryMode", "v1.4.8", true),
                             Mod("BirthAndDeath", "v1.4.8", false), Mod("CoopNightly", "v0.1.4", true));
        var plan = LauncherDataSync.ComputePlan([E("Native", "v1.4.8"), E("SandBox", "v1.4.8")], Order("Native"), path);
        Assert.False(plan.HasChanges);
        Assert.DoesNotContain(plan.Changes, c => c.Id is "Native" or "StoryMode" or "BirthAndDeath");
    }

    [Fact]
    public void CoopModPatch_is_an_ordinary_mod_not_the_coop_module()
    {
        var path = WriteFile(Mod("CoopNightly", "v0.1.4", true), Mod("CoopModPatch", "v1.0", true));
        var plan = LauncherDataSync.ComputePlan([], Order(), path);
        // The prefix "Coop" must not exempt it: the server is not running it, so it has to come off.
        Assert.Contains(plan.Changes, c => c.Id == "CoopModPatch" && c.Action == LauncherDataSync.SyncAction.Disable);
        Assert.DoesNotContain(plan.Changes, c => c.Id == "CoopNightly");
    }

    [Fact]
    public void Coop_module_is_enabled_when_present_but_off()
    {
        var path = WriteFile(Mod("Coop", "v0.1.4", false));
        var plan = LauncherDataSync.ComputePlan([], Order(), path);
        Assert.Contains(plan.Changes, c => c.Id == "Coop" && c.Action == LauncherDataSync.SyncAction.Enable);
        LauncherDataSync.Apply(plan, path, BackupRoot);
        Assert.True(Read(path).Single(x => x.Id == "Coop").Selected);
    }

    [Fact]
    public void Two_coop_modules_are_a_warning_and_nothing_is_touched()
    {
        var path = WriteFile(Mod("Coop", "v0.1.4", false), Mod("CoopNightly", "v0.1.4", true));
        var plan = LauncherDataSync.ComputePlan([], Order(), path);
        Assert.Contains(plan.Changes, c => c.Action == LauncherDataSync.SyncAction.Warning);
        Assert.False(plan.HasChanges);
        Assert.Null(LauncherDataSync.Apply(plan, path, BackupRoot));
    }

    [Fact]
    public void Missing_coop_module_is_unfixable()
    {
        var path = WriteFile(Mod("Native", "v1.4.8", true));
        var plan = LauncherDataSync.ComputePlan([], Order(), path);
        Assert.Contains(plan.Blockers, c => c.Id == "Coop" && c.Action == LauncherDataSync.SyncAction.Unfixable);
    }

    [Fact]
    public void A_version_mismatch_is_reported_and_left_alone()
    {
        var path = WriteFile(Mod("CoopNightly", "v0.1.4", true), Mod("ModularSmithing2", "v0.9.27", false));
        var plan = LauncherDataSync.ComputePlan([E("ModularSmithing2", "v0.9.28")], Order("ModularSmithing2"), path);
        Assert.Contains(plan.Blockers, c => c.Id == "ModularSmithing2" && c.Detail.Contains("0.9.28"));
        // Ticking the wrong version would satisfy nothing, so the entry keeps the state the player gave it.
        Assert.DoesNotContain(plan.Changes, c => c.Id == "ModularSmithing2" && c.Action == LauncherDataSync.SyncAction.Enable);
    }

    [Fact]
    public void Four_part_and_three_part_versions_count_as_equal()
    {
        var path = WriteFile(Mod("CoopNightly", "v0.1.4", true), Mod("HealOnKill", "v1.2.0.0", false));
        var plan = LauncherDataSync.ComputePlan([E("HealOnKill", "v1.2.0")], Order("HealOnKill"), path);
        Assert.Contains(plan.Changes, c => c.Id == "HealOnKill" && c.Action == LauncherDataSync.SyncAction.Enable);
        Assert.Empty(plan.Blockers.Where(b => b.Id == "HealOnKill"));
    }

    [Fact]
    public void A_mod_missing_from_this_pc_is_reported()
    {
        var path = WriteFile(Mod("CoopNightly", "v0.1.4", true));
        var plan = LauncherDataSync.ComputePlan([E("ImprovedGarrisons", "v1.2.3")], Order("ImprovedGarrisons"), path);
        Assert.Contains(plan.Blockers, c => c.Id == "ImprovedGarrisons" && c.Detail.Contains("not installed"));
    }

    [Fact]
    public void Server_only_modules_are_ignored()
    {
        var path = WriteFile(Mod("CoopNightly", "v0.1.4", true));
        var plan = LauncherDataSync.ComputePlan(
            [E("DedicatedServer.ModularCoopCompat", "v0.1.0"), E("DedicatedServer.Windows", "v1.4.8")],
            Order("DedicatedServer.ModularCoopCompat"), path);
        Assert.Empty(plan.Changes);   // not "missing on this PC": they only ever exist on the server
    }

    [Fact]
    public void Reorder_puts_enabled_community_mods_in_the_servers_order()
    {
        var path = WriteFile(Mod("CoopNightly", "v0.1.4", true), Mod("C", "v1", true), Mod("B", "v1", true), Mod("A", "v1", true));
        var plan = LauncherDataSync.ComputePlan([E("A", "v1"), E("B", "v1"), E("C", "v1")], Order("A", "B", "C"), path);
        // C B A -> A B C moves the outer two; B already sits at index 1.
        Assert.Equal(2, plan.Changes.Count(c => c.Action == LauncherDataSync.SyncAction.Move));

        LauncherDataSync.Apply(plan, path, BackupRoot);
        Assert.Equal(["CoopNightly", "A", "B", "C"], Read(path).Select(x => x.Id));
    }

    [Fact]
    public void Reorder_keeps_an_unmoved_mod_in_its_place_relative_to_the_moved_ones()
    {
        // C B A -> A B C: B keeps index 1, so it is not reported as a move, but it still has to end up between them.
        var path = WriteFile(Mod("CoopNightly", "v0.1.4", true), Mod("C", "v1", true), Mod("B", "v1", true), Mod("A", "v1", true));
        var plan = LauncherDataSync.ComputePlan([E("A", "v1"), E("B", "v1"), E("C", "v1")], Order("A", "B", "C"), path);
        Assert.DoesNotContain(plan.Changes, c => c.Id == "B" && c.Action == LauncherDataSync.SyncAction.Move);
        LauncherDataSync.Apply(plan, path, BackupRoot);
        Assert.Equal(["CoopNightly", "A", "B", "C"], Read(path).Select(x => x.Id));
    }

    [Fact]
    public void Disabled_mods_and_officials_keep_their_positions_when_others_move()
    {
        var path = WriteFile(Mod("Native", "v1.4.8", true), Mod("B", "v1", true), Mod("Unused", "v1", false),
                             Mod("A", "v1", true), Mod("CoopNightly", "v0.1.4", true));
        var plan = LauncherDataSync.ComputePlan([E("A", "v1"), E("B", "v1")], Order("A", "B"), path);
        LauncherDataSync.Apply(plan, path, BackupRoot);
        var ids = Read(path).Select(x => x.Id).ToList();
        Assert.Equal(0, ids.IndexOf("Native"));
        Assert.Equal(2, ids.IndexOf("Unused"));
        Assert.True(ids.IndexOf("A") < ids.IndexOf("B"));
    }

    [Fact]
    public void A_matching_file_produces_no_changes_and_is_not_written()
    {
        var path = WriteFile(Mod("Native", "v1.4.8", true), Mod("CoopNightly", "v0.1.4", true), Mod("HealOnKill", "v1.2", true));
        var before = File.ReadAllBytes(path);
        var plan = LauncherDataSync.ComputePlan([E("HealOnKill", "v1.2")], Order("HealOnKill"), path);

        Assert.False(plan.HasChanges);
        Assert.Null(LauncherDataSync.Apply(plan, path, BackupRoot));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(BackupRoot));
    }

    [Fact]
    public void The_multiplayer_list_and_dll_check_data_survive_a_sync()
    {
        var path = WriteFile(Mod("CoopNightly", "v0.1.4", true), Mod("BetterPatrols", "v2.0", true));
        var mpBefore = Section(path, "/UserData/MultiplayerData");
        var dllBefore = Section(path, "/UserData/DLLCheckData");

        var plan = LauncherDataSync.ComputePlan([], Order(), path);
        Assert.NotNull(LauncherDataSync.Apply(plan, path, BackupRoot));

        Assert.Equal(mpBefore, Section(path, "/UserData/MultiplayerData"));
        Assert.Equal(dllBefore, Section(path, "/UserData/DLLCheckData"));
        Assert.Equal("Singleplayer", Section(path, "/UserData/GameType").Replace("<GameType>", "").Replace("</GameType>", ""));
    }

    [Fact]
    public void Applying_backs_the_file_up_first()
    {
        var path = WriteFile(Mod("CoopNightly", "v0.1.4", true), Mod("BetterPatrols", "v2.0", true));
        var before = File.ReadAllText(path);
        var result = LauncherDataSync.Apply(LauncherDataSync.ComputePlan([], Order(), path), path, BackupRoot);

        Assert.NotNull(result);
        Assert.True(File.Exists(result!.BackupPath));
        Assert.Equal(before, File.ReadAllText(result.BackupPath));
        Assert.NotEqual(before, File.ReadAllText(path));
    }

    [Fact]
    public void A_mod_the_launcher_has_never_listed_is_added_not_called_missing()
    {
        // Freshly subscribed: on disk where the client loads from, but absent from the file because the launcher
        // has not run since. Reporting "not installed" was wrong and left the player with nothing to do.
        var path = WriteFile(Mod("CoopNightly", "v0.1.4", true));
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MyLittleWarband" };
        var plan = LauncherDataSync.ComputePlan([E("MyLittleWarband", "e1.4.6")], Order("MyLittleWarband"), path, installed);

        Assert.Contains(plan.Changes, c => c.Id == "MyLittleWarband" && c.Action == LauncherDataSync.SyncAction.Add);
        Assert.Empty(plan.Blockers);

        var result = LauncherDataSync.Apply(plan, path, BackupRoot);
        Assert.Equal(1, result!.Added);
        var after = Read(path).Single(x => x.Id == "MyLittleWarband");
        Assert.True(after.Selected);
    }

    [Fact]
    public void Without_an_installed_set_an_absent_entry_is_still_reported_as_missing()
    {
        var path = WriteFile(Mod("CoopNightly", "v0.1.4", true));
        var plan = LauncherDataSync.ComputePlan([E("MyLittleWarband", "e1.4.6")], Order("MyLittleWarband"), path);
        Assert.Contains(plan.Blockers, c => c.Id == "MyLittleWarband" && c.Action == LauncherDataSync.SyncAction.Unfixable);
    }

    [Fact]
    public void An_added_entry_is_ordered_in_the_same_pass()
    {
        var path = WriteFile(Mod("CoopNightly", "v0.1.4", true), Mod("B", "v1", true));
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "B" };
        var plan = LauncherDataSync.ComputePlan([E("A", "v1"), E("B", "v1")], Order("A", "B"), path, installed);

        LauncherDataSync.Apply(plan, path, BackupRoot);
        var ids = Read(path).Select(x => x.Id).ToList();
        Assert.True(ids.IndexOf("A") < ids.IndexOf("B"));   // converges in one run, not two
    }

    [Fact]
    public void A_ticked_mod_whose_folder_is_gone_is_flagged()
    {
        var path = WriteFile(Mod("CoopNightly", "v0.1.4", true), Mod("ModularCoop.Compat", "v0.1.0", true));
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // nothing on disk any more
        var plan = LauncherDataSync.ComputePlan([E("ModularCoop.Compat", "v0.1.0")], Order("ModularCoop.Compat"), path, installed);
        Assert.Contains(plan.Blockers, c => c.Id == "ModularCoop.Compat" && c.Detail.Contains("not on this PC any more"));
    }

    [Fact]
    public void A_mod_listed_twice_loses_the_spare_entry_and_keeps_the_enabled_one()
    {
        // Seen for real: our Add wrote an entry, then the Bannerlord launcher rescanned and wrote its own, and the
        // launcher then flagged the duplicated mod.
        var path = WriteFile(Mod("CoopNightly", "v0.1.4", true), Mod("ModularSmithing2", "v0.9.30", true),
                             Mod("ModularSmithing2", "v0.9.30", false));
        var plan = LauncherDataSync.ComputePlan([E("ModularSmithing2", "v0.9.30")], Order("ModularSmithing2"), path);

        Assert.Contains(plan.Changes, c => c.Id == "ModularSmithing2" && c.Action == LauncherDataSync.SyncAction.RemoveDuplicate);

        var result = LauncherDataSync.Apply(plan, path, BackupRoot);
        Assert.Equal(1, result!.DuplicatesRemoved);
        var rows = Read(path).Where(x => x.Id == "ModularSmithing2").ToList();
        Assert.Single(rows);
        Assert.True(rows[0].Selected);   // the surviving entry is the one that was on
    }

    [Fact]
    public void A_list_with_no_duplicates_plans_no_removals()
    {
        var path = WriteFile(Mod("CoopNightly", "v0.1.4", true), Mod("HealOnKill", "v1.2", true));
        var plan = LauncherDataSync.ComputePlan([E("HealOnKill", "v1.2")], Order("HealOnKill"), path);
        Assert.DoesNotContain(plan.Changes, c => c.Action == LauncherDataSync.SyncAction.RemoveDuplicate);
        Assert.False(plan.HasChanges);
    }

    [Fact]
    public void A_duplicate_entry_does_not_silently_disable_reordering()
    {
        // The bug this pins: one mod listed twice made the counts disagree, so the whole reorder pass bailed out
        // without a word. Andy reordered mods in the launcher and the app kept insisting nothing had changed.
        var path = WriteFile(Mod("CoopNightly", "v0.1.4", true), Mod("C", "v1", true), Mod("B", "v1", true),
                             Mod("A", "v1", true), Mod("C", "v1", false));
        var plan = LauncherDataSync.ComputePlan([E("A", "v1"), E("B", "v1"), E("C", "v1")], Order("A", "B", "C"), path);

        Assert.Contains(plan.Changes, c => c.Action == LauncherDataSync.SyncAction.RemoveDuplicate);
        Assert.Contains(plan.Changes, c => c.Action == LauncherDataSync.SyncAction.Move);

        LauncherDataSync.Apply(plan, path, BackupRoot);
        Assert.Equal(["CoopNightly", "A", "B", "C"], Read(path).Select(x => x.Id));
    }

    [Fact]
    public void A_server_mod_the_client_cannot_place_does_not_abandon_the_whole_reorder()
    {
        // B is missing on this PC. That is worth reporting, but the mods that ARE here should still be ordered.
        var path = WriteFile(Mod("CoopNightly", "v0.1.4", true), Mod("C", "v1", true), Mod("A", "v1", true));
        var plan = LauncherDataSync.ComputePlan([E("A", "v1"), E("B", "v1"), E("C", "v1")], Order("A", "B", "C"), path);

        Assert.Contains(plan.Blockers, c => c.Id == "B");
        Assert.Contains(plan.Changes, c => c.Action == LauncherDataSync.SyncAction.Move);
        LauncherDataSync.Apply(plan, path, BackupRoot);
        var ids = Read(path).Select(x => x.Id).ToList();
        Assert.True(ids.IndexOf("A") < ids.IndexOf("C"));
    }

    [Fact]
    public void A_missing_file_is_reported_and_never_created()
    {
        var path = Path.Combine(_dir, "nope", "LauncherData.xml");
        var plan = LauncherDataSync.ComputePlan([E("HealOnKill", "v1.2")], Order("HealOnKill"), path);

        Assert.Contains(plan.Blockers, c => c.Detail.Contains("run the Bannerlord launcher"));
        Assert.False(plan.HasChanges);
        Assert.Null(LauncherDataSync.Apply(plan, path, BackupRoot));
        Assert.False(File.Exists(path));
    }
}
