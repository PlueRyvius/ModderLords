using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ModderLords.Compat;

/// <summary>
/// Finds the method the host's map scene <em>actually runs</em> for an <c>IMapScene</c> query.
///
/// <para>CORRECTION 2026-09-12: an earlier version patched the declarations on <c>SandBox.MapScene</c>, reasoning that
/// they are non-virtual and so the host's scene could not override them. It cannot override them — but it
/// re-implements the interface, which has the same effect and is not the same thing. <c>A.G</c> carries its own sealed
/// explicit <c>IMapScene.GetEnvironmentTerrainTypesCount</c>, <c>GetMapPatchAtPosition</c> and
/// <c>GetHeightAtPoint</c>, so the interface slots point at its code and patches on SandBox's copies never fire. A
/// spike ran for a full battle and served zero answers before this was noticed.</para>
///
/// <para>So resolve through the runtime interface map instead. That yields whichever method the type genuinely
/// dispatches to — A.G's own where it re-implements, SandBox's inherited one where it does not (which is the case for
/// exactly the queries measured to be correct) — and it does it without depending on a name, which matters because
/// DedicatedServer.Core is obfuscated down to <c>MapPatchData A(ref CampaignVec2)</c>.</para>
///
/// <para>The same obfuscation strips parameter names, so a patch on one of these must take its by-ref arguments
/// positionally (<c>__0</c>, <c>__1</c>) and never by name.</para>
/// </summary>
internal static class MapSceneTarget
{
    private const string MapSceneInterface = "TaleWorlds.CampaignSystem.Map.IMapScene, TaleWorlds.CampaignSystem";

    /// <summary>
    /// The host's own MapScene subclass. Spikes also guard on this at call time so they cannot alter a scene that is
    /// not the stripped one.
    /// </summary>
    internal static Type Headless()
    {
        var mapScene = AccessTools.TypeByName("SandBox.MapScene") ?? throw new MissingMemberException("SandBox.MapScene");
        var headless = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name == "DedicatedServer.Core")
            .Select(a => a.GetType("A.G"))
            .FirstOrDefault(t => t != null);
        if (headless == null || !mapScene.IsAssignableFrom(headless))
            throw new MissingMemberException("Unsupported DedicatedServer.Core headless MapScene implementation");
        return headless;
    }

    /// <summary>
    /// The method <paramref name="scene"/> dispatches to for <c>IMapScene.<paramref name="name"/></c>. Throws rather
    /// than returning null: a spike that patches nothing looks exactly like a disproved hypothesis, and that mistake
    /// has already cost one run.
    /// </summary>
    internal static IReadOnlyList<MethodInfo> Implementations(Type scene, string name, Type[] signature)
    {
        var contract = Type.GetType(MapSceneInterface, false) ?? throw new MissingMemberException(MapSceneInterface);
        var declared = contract.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, signature, null)
                       ?? throw new MissingMethodException("IMapScene." + name);
        var map = scene.GetInterfaceMap(contract);
        MethodInfo? target = null;
        for (var i = 0; i < map.InterfaceMethods.Length; i++)
            if (map.InterfaceMethods[i] == declared) { target = map.TargetMethods[i]; break; }
        if (target == null) throw new MissingMethodException(scene.FullName + " does not map IMapScene." + name);

        var targets = new List<MethodInfo> { target };
        // A.G's explicit implementations forward to its own obfuscated methods (IMapScene.GetMapPatchAtPosition next
        // to "MapPatchData A(ref CampaignVec2)"). Campaign code holds an IMapScene and so comes through the slot
        // above, but the host's own code can call the inner method directly. Patch any same-shaped sibling too;
        // every postfix here is idempotent, so more than one firing changes nothing.
        foreach (var sibling in scene.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (sibling == target || sibling.ReturnType != declared.ReturnType) continue;
            var parameters = sibling.GetParameters();
            if (parameters.Length != signature.Length) continue;
            if (parameters.Where((p, i) => p.ParameterType != signature[i]).Any()) continue;
            targets.Add(sibling);
        }
        return targets;
    }

    /// <summary>
    /// Every IMapScene member paired with the method <paramref name="scene"/> runs for it, keyed by the interface's
    /// own name. Lets a caller work from the readable contract while patching the obfuscated reality.
    /// </summary>
    internal static IEnumerable<KeyValuePair<string, MethodInfo>> AllImplementations(Type scene)
    {
        var contract = Type.GetType(MapSceneInterface, false) ?? throw new MissingMemberException(MapSceneInterface);
        var map = scene.GetInterfaceMap(contract);
        for (var i = 0; i < map.InterfaceMethods.Length; i++)
            yield return new KeyValuePair<string, MethodInfo>(map.InterfaceMethods[i].Name, map.TargetMethods[i]);
    }
}
