using System;
using System.Reflection;
using HarmonyLib;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>
/// Methods to patch, looked up the way Harmony needs them. Free of game types so the tests can check it.
/// </summary>
public static class FbPatchTargets
{
    /// <summary>
    /// The setter as declared: Harmony patches only the type that declares a method, and a property reached through a
    /// derived type (Town.FoodStocks, declared on Fief) is refused. Only players' games patch these setters, so a
    /// server-only self-test never sees the refusal. Null when there is no such writable property.
    /// </summary>
    public static MethodInfo? DeclaredSetter(Type type, string property)
        => AccessTools.Property(type, property) is { } p ? AccessTools.DeclaredPropertySetter(p.DeclaringType, property) : null;
}
