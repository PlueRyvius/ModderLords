using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace ModderLords.Compat;

/// <summary>
/// Shared plumbing for the terrain spikes: find the headless map scene implementation, and patch the two entry points
/// a terrain query can arrive through.
///
/// <para><c>SandBox.MapScene</c> declares these methods non-virtual and <em>also</em> carries sealed explicit
/// <c>IMapScene</c> implementations of the same signatures. The host's scene (<c>A.G</c> in DedicatedServer.Core)
/// derives from MapScene and therefore cannot override either. So a spike patches both declarations and keeps its
/// postfixes idempotent: whichever path a caller takes, and if the explicit one simply forwards to the public one and
/// both fire, the result is the same.</para>
/// </summary>
internal static class MapSceneTarget
{
    private const string InterfacePrefix = "TaleWorlds.CampaignSystem.Map.IMapScene.";

    /// <summary>SandBox.MapScene, the type that declares the terrain queries.</summary>
    internal static Type Base =>
        AccessTools.TypeByName("SandBox.MapScene") ?? throw new MissingMemberException("SandBox.MapScene");

    /// <summary>
    /// The host's own MapScene subclass. Spikes guard on this so they cannot alter a scene that is not the stripped
    /// one — belt and braces on a server, where no other scene exists.
    /// </summary>
    internal static Type Headless()
    {
        var headless = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name == "DedicatedServer.Core")
            .Select(a => a.GetType("A.G"))
            .FirstOrDefault(t => t != null);
        if (headless == null || !Base.IsAssignableFrom(headless))
            throw new MissingMemberException("Unsupported DedicatedServer.Core headless MapScene implementation");
        return headless;
    }

    /// <summary>
    /// Both declarations of one query: the public method and the explicit interface method. Throws when neither is
    /// found, because a spike that silently patches nothing reads as a disproved hypothesis.
    /// </summary>
    internal static IReadOnlyList<System.Reflection.MethodInfo> Both(string name, Type[] signature)
    {
        var found = new List<System.Reflection.MethodInfo>();
        var pub = AccessTools.DeclaredMethod(Base, name, signature);
        if (pub != null) found.Add(pub);
        var explicitly = AccessTools.DeclaredMethod(Base, InterfacePrefix + name, signature);
        if (explicitly != null) found.Add(explicitly);
        if (found.Count == 0) throw new MissingMethodException("SandBox.MapScene." + name);
        return found;
    }
}
