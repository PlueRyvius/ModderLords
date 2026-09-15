using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.Core;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Server: keeps Coop from opening a field battle on a scene that ships without its terrain shader cache. Coop 0.1.5
/// already builds the mission record once on the server and sends it, scene name included, to every client
/// (<c>NetworkStartAttackMission</c>), so the server's choice is the only one that matters. Its
/// <c>FieldBattleMissionInitializer.Create</c> ends in a local random pick among the scenes mapped to the map patch;
/// this postfix swaps a listed scene for another candidate of the same tier, chosen by the battle's terrain seed.
/// The list comes from recipes.json (<c>ExcludedBattleScenes</c>), computed by the launcher from the game's and the
/// mods' SceneObj folders (the server install ships no scene content).
/// </summary>
public static class BattleScenePick
{
    private static readonly Harmony Harmony = new Harmony("ModderLords.Compat.BattleScenePick");
    private static readonly HashSet<string> Excluded = new HashSet<string>(StringComparer.Ordinal);
    private static bool _installTried;
    private static int _logged;

    /// <summary>Reads Mods-independent <c>ExcludedBattleScenes</c> from the recipe. Returns a one-line report.</summary>
    public static string Load(string recipesJson)
    {
        try
        {
            Excluded.Clear();
            foreach (var t in JObject.Parse(recipesJson)["ExcludedBattleScenes"] as JArray ?? new JArray()) Excluded.Add(t.ToString());
        }
        catch (Exception ex) { return "recipe parse failed: " + ex.Message; }
        return Excluded.Count == 0 ? "none excluded (switch off, or every scene has its shader cache)" : $"{Excluded.Count} scene(s) without a terrain shader cache will not be chosen";
    }

    /// <summary>From the adapter tick on the server: installs the postfix once Coop's type is loaded. No-op on clients and when nothing is excluded.</summary>
    public static void EnsureInstalled()
    {
        if (_installTried || Excluded.Count == 0) return;
        try { if (Common.ModInformation.IsClient) return; } catch { return; }
        var type = AccessTools.TypeByName("GameInterface.Services.MapEvents.FieldBattleMissionInitializer");
        if (type is null) return;   // Coop not loaded yet; try again next tick
        _installTried = true;
        var create = AccessTools.Method(type, "Create");
        if (create is null) { Log.Warn("battle scene pick: FieldBattleMissionInitializer.Create not found; excluded scenes can still be chosen"); return; }
        try
        {
            Harmony.Patch(create, postfix: new HarmonyMethod(typeof(BattleScenePick), nameof(CreatePostfix)));
            Log.Info($"battle scene pick: installed; {Excluded.Count} scene(s) excluded");
        }
        catch (Exception ex) { Log.Warn("battle scene pick: could not patch Create: " + ex.GetBaseException().Message); }
    }

    public static void CreatePostfix(MapEvent battle, int randomTerrainSeed, ref MissionInitializerRecord __result)
    {
        try
        {
            var picked = __result.SceneName;
            var replacement = BattleScenePolicy.Replace(picked, Tiers(battle), Excluded, randomTerrainSeed);
            var id = battle?.ToString() ?? "?";
            if (replacement is null)
            {
                if (_logged++ < 50) Log.Info($"battle scene for {id}: {picked}");
                return;
            }
            if (replacement == picked) { Log.Warn($"battle scene for {id}: {picked} has no terrain shader cache and no other candidate has one; kept"); return; }
            __result.SceneName = replacement;
            Log.Info($"battle scene for {id}: {picked} has no terrain shader cache; {replacement} chosen instead (seed {randomTerrainSeed})");
        }
        catch (Exception ex) { Log.Warn("battle scene pick failed, Coop's choice kept: " + ex.GetBaseException().Message); }
    }

    /// <summary>Candidate tiers in the order the game narrows them: scenes mapped to the map patch, then same terrain, then same water/land, then everything.</summary>
    private static IEnumerable<IReadOnlyList<string>> Tiers(MapEvent battle)
    {
        var all = GameSceneDataManager.Instance?.SingleplayerBattleScenes;
        if (all is null || battle is null || Campaign.Current?.MapSceneWrapper is null) yield break;
        var naval = battle.IsNavalMapEvent;
        var position = battle.Position;
        var patch = Campaign.Current.MapSceneWrapper.GetMapPatchAtPosition(in position);
        Campaign.Current.MapSceneWrapper.GetEnvironmentTerrainTypesCount(in position, out var terrain);
        yield return all.Where(s => s.IsNaval == naval && s.MapIndices.Contains(patch.sceneIndex)).Select(s => s.SceneID).ToList();
        yield return all.Where(s => s.IsNaval == naval && s.Terrain == terrain).Select(s => s.SceneID).ToList();
        yield return all.Where(s => s.IsNaval == naval).Select(s => s.SceneID).ToList();
        yield return all.Select(s => s.SceneID).ToList();
    }
}
