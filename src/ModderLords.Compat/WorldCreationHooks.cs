using System;
using System.Reflection;
using HarmonyLib;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.ObjectSystem;
using System.Xml;
using TaleWorlds.CampaignSystem.CharacterCreationContent;
using TaleWorlds.MountAndBlade;

namespace ModderLords.Compat;

/// <summary>Creation-only routing: retain the official server startup before replacing its save load.</summary>
internal static class WorldCreationHooks
{
    private static WorldCreator? _creator;
    private static MethodInfo? _startNewGame;
    private static bool _redirected;
    private static IDisposable? _originalsScope;
    private static bool _finalized;
    private static readonly Harmony Patches = new Harmony("ModderLords.WorldCreation");

    internal static void Install(WorldCreator creator)
    {
        if (!ServerDetect.IsDedicatedServer || !creator.IsEnabled)
            throw new InvalidOperationException("World creation hooks require a dedicated creation process.");
        var type = AccessTools.TypeByName("GameInterface.Services.GameState.Interfaces.GameStateInterface")
            ?? throw new MissingMemberException("GameStateInterface not found");
        var load = type.GetMethod("LoadGame", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
        _startNewGame = type.GetMethod("StartNewGame", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (load?.ReturnType != typeof(void) || _startNewGame?.ReturnType != typeof(void))
            throw new MissingMethodException("Expected LoadGame(string) and StartNewGame() returning void");
        _creator = creator;
        Patches.Patch(load, prefix: new HarmonyMethod(typeof(WorldCreationHooks), nameof(LoadPrefix)));
        var managerType = AccessTools.TypeByName("SandBox.SandBoxGameManager")
            ?? throw new MissingMemberException("SandBoxGameManager not found");
        var finished = managerType.GetMethod("OnLoadFinished", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (finished?.ReturnType != typeof(void)) throw new MissingMethodException("SandBoxGameManager.OnLoadFinished()");
        PatchManager(finished);
        var loadedSetter = AccessTools.PropertySetter(typeof(MBGameManager), "IsLoaded");
        if (loadedSetter == null || loadedSetter.GetParameters().Length != 1 || loadedSetter.GetParameters()[0].ParameterType != typeof(bool))
            throw new MissingMethodException("MBGameManager.IsLoaded setter");
        Patches.Patch(loadedSetter, prefix: new HarmonyMethod(typeof(WorldCreationHooks), nameof(KeepCreating)));
        Patches.Patch(AccessTools.Method(typeof(Campaign), "LoadMapScene"), postfix: new HarmonyMethod(typeof(WorldCreationHooks), nameof(MapLoaded)));
        Patches.Patch(AccessTools.Method(typeof(MBObjectManager), "LoadXml", new[] { typeof(XmlDocument), typeof(bool) }),
            finalizer: new HarmonyMethod(typeof(WorldCreationHooks), nameof(XmlFinished)));
        Log.Info("worldcreate: installed creation-only LoadGame -> StartNewGame route");
    }

    private static bool LoadPrefix(object __instance)
    {
        if (_creator == null) return true;
        if (_redirected) throw new InvalidOperationException("Unexpected second save-load request during world creation");
        _redirected = true;
        _creator.StartFromHost(__instance, _startNewGame!);
        return false;
    }

    internal static void AllowWorldInitialization()
    {
        // Coop otherwise prevents XML-assigned StringIds and routes object creation through live sync.
        // Use its own save-deserialization scope while generating the initial object graph.
        var policy = AccessTools.TypeByName("GameInterface.Policies.CallOriginalPolicy");
        var allow = policy?.GetMethod("AllowOriginalsOnAllThreads", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        if (allow == null || !typeof(IDisposable).IsAssignableFrom(allow.ReturnType))
            throw new MissingMethodException("CallOriginalPolicy.AllowOriginalsOnAllThreads() is unavailable");
        _originalsScope = (IDisposable)allow.Invoke(null, null)!;
        Log.Info("worldcreate: using Coop's original-call scope for initial world objects");
        // A fresh campaign deserializes faction banners before the server's late visual-loading setup.
        BannerManager.Initialize();
        if (BannerManager.Instance.BannerIconGroups.Count == 0) BannerManager.Instance.LoadBannerIcons();
        Log.Info("worldcreate: banner definitions initialized before faction XML");
    }

    internal static void Finish()
    {
        _originalsScope?.Dispose();
        _originalsScope = null;
    }

    private static bool FinishNewCampaign(MBGameManager __instance)
    {
        Log.Info("worldcreate: finalization entered manager=" + __instance.GetType().AssemblyQualifiedName);
        if (_creator == null) return true;
        if (_finalized) return false;
        _finalized = true;
        // This server has no player to watch an intro or use character-creation screens. Complete the
        // normal content finalization with a deterministic seed character, then enter the map state.
        var state = new CharacterCreationState();
        var manager = state.CharacterCreationManager;
        var content = manager.CharacterCreationContent;
        var cultures = content.GetCultures().ToList();
        var culture = cultures.FirstOrDefault(c => c.StringId == "empire") ?? cultures.FirstOrDefault()
            ?? throw new InvalidOperationException("No character-creation cultures are available");
        content.SetSelectedCulture(culture, manager);
        content.SetMainCharacterName("World Seed");
        content.SetMainClanBanner(Clan.PlayerClan.Banner);
        manager.ApplyFinalEffects();
        state.FinalizeCharacterCreationState();
        // Leave IsLoaded false: the official host must not enter its serving/autosaving phase.
        Log.Info($"worldcreate: character finalized; culture={culture.StringId}; activeState={Game.Current.GameStateManager.ActiveState?.GetType().Name}");
        return false;
    }

    private static void MapLoaded()
    {
        var manager = Game.Current.GameManager;
        Log.Info("worldcreate: actual manager=" + manager.GetType().AssemblyQualifiedName + " assembly=" + manager.GetType().Assembly.Location);
        var finished = manager.GetType().GetMethod("OnLoadFinished", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)
            ?? throw new MissingMethodException("Actual game manager OnLoadFinished()");
        PatchManager(finished);
        Log.Info($"worldcreate: map scene={Campaign.Current.MapSceneWrapper.GetType().FullName} clans={Clan.All.Count} settlements={Settlement.All.Count}");
        var xmlClans = MBObjectManager.Instance.GetObjectTypeList<Clan>();
        Log.Info($"worldcreate: XML clans={xmlClans.Count}: {string.Join(",", xmlClans.Take(10).Select(c => c.StringId))}");
        foreach (var c in Clan.All.Take(8)) Log.Info($"worldcreate: clan id={c.StringId} culture={c.Culture?.StringId} leader={c.Leader?.StringId}");
        var invalid = Settlement.All.Where(s => s.OwnerClan == null).ToList();
        Log.Info($"worldcreate: settlements missing owner clan={invalid.Count}: {string.Join(",", invalid.Take(8).Select(s => s.StringId))}");
        var template = MBObjectManager.Instance.GetObject<CharacterObject>("spc_ghilman_leader_0");
        Log.Info($"worldcreate: ghilman template culture={template?.Culture?.StringId} hero={template?.IsHero} battle={template?.Culture?.DefaultBattleEquipmentRoster?.StringId} stealth={template?.Culture?.DefaultStealthEquipmentRoster?.StringId}; registered default stealth={MBObjectManager.Instance.GetObject<MBEquipmentRoster>("default_stealth_equipment_roster")?.StringId}");
    }

    private static bool KeepCreating(bool value) => !value;

    private static void PatchManager(MethodInfo method)
    {
        var info = Harmony.GetPatchInfo(method);
        Log.Info("worldcreate: finish target=" + method.DeclaringType?.AssemblyQualifiedName + " patches=" +
            (info == null ? "none" : string.Join(",", info.Prefixes.Select(p => p.owner + ":" + p.PatchMethod.Name))));
        if (info?.Prefixes.Any(p => p.owner == Patches.Id) == true) return;
        Patches.Patch(method, prefix: new HarmonyMethod(typeof(WorldCreationHooks), nameof(FinishNewCampaign)) { priority = Priority.First });
    }

    private static Exception? XmlFinished(XmlDocument doc, bool __runOriginal, Exception? __exception)
    {
        if (__exception != null || doc.DocumentElement?.Name == "NPCCharacters")
        {
            var message = $"worldcreate: XML finished root={doc.DocumentElement?.Name} ranOriginal={__runOriginal} exception={__exception}";
            Log.Info(message);
            System.IO.File.AppendAllText(WorldCreator.SidecarPath() + ".xml-errors", message + Environment.NewLine);
        }
        return __exception;
    }

}
