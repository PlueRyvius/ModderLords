using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ModderLords.Coop.Launch;
using Xunit;
using Xunit.Abstractions;

namespace ModderLords.Core.Tests;

/// <summary>
/// Diagnostic for Phase 1a: can the dedicated server create its own world, instead of only ever copying the pre-baked
/// vanilla default_new_game.sav?
///
/// Metadata only — this reads the shipped assemblies, it never loads or invokes anything. The question it answers is
/// the cheap half of the go/no-go: what the character-creation entry points actually look like, and what they drag in.
/// If the skip path is private, instance-bound on a screen, or reaches the Gauntlet UI stack, a Win64_Shipping_Server
/// build cannot drive it and the answer is 1b (seed the server from a client-made save) rather than more patching.
/// </summary>
public class WorldGenerationProbe
{
    private readonly ITestOutputHelper _out;
    public WorldGenerationProbe(ITestOutputHelper o) => _out = o;

    private static readonly string[] Interesting =
    {
        "SkipCharacterCreation", "LaunchSandboxCharacterCreation", "SandBoxGameManager",
        "OnNewGameCreated", "CharacterCreation", "NewGameCreationResult", "SaveHandler", "SaveManager",
    };

    /// <summary>UI the headless server has no way to drive. Any of these on the path is a hard stop.</summary>
    private static readonly string[] UiTypes = { "Gauntlet", "Screen", "MissionView", "Movie", "Widget" };

    [Fact]
    public void Report_world_creation_entry_points()
    {
        var root = ServerPaths.FindWorkshopDedicatedServerRoots().FirstOrDefault();
        if (root is null) { _out.WriteLine("no DedicatedServer package found; skipped"); return; }
        var paths = ServerPaths.Create(root);
        var engine = Path.Combine(paths.DedicatedServerRoot, "engine");

        foreach (var dll in new[]
                 {
                     Path.Combine(engine, "Modules", "Coop", "bin", "Win64_Shipping_Server", "GameInterface.dll"),
                     Path.Combine(engine, "bin", "Win64_Shipping_Server", "DedicatedServer.Core.dll"),
                     Path.Combine(engine, "Modules", "Coop", "bin", "Win64_Shipping_Server", "SandBox.dll"),
                 })
        {
            _out.WriteLine($"===== {Path.GetFileName(dll)} =====");
            if (!File.Exists(dll)) { _out.WriteLine("  not present"); continue; }
            Report(dll);
        }
    }

    private void Report(string dll)
    {
        using var fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();

        // What this assembly references at all: the fastest way to see whether the UI stack is on the path.
        var refs = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).OrderBy(x => x).ToList();
        _out.WriteLine("  references: " + string.Join(", ", refs));
        var ui = refs.Where(r => UiTypes.Any(u => r.Contains(u, StringComparison.OrdinalIgnoreCase))).ToList();
        _out.WriteLine("  UI-stack references: " + (ui.Count == 0 ? "(none)" : string.Join(", ", ui)));

        foreach (var th in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(th);
            var name = md.GetString(t.Name);
            var typeMatches = Interesting.Any(i => name.Contains(i, StringComparison.OrdinalIgnoreCase));
            var header = $"  type {md.GetString(t.Namespace)}.{name} [{t.Attributes & TypeAttributes.VisibilityMask}]";
            if (typeMatches) _out.WriteLine(header);

            foreach (var mh in t.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                var mn = md.GetString(m.Name);
                // Search every type's methods, not only the interestingly-named types: the entry point that actually
                // creates a campaign need not live on a type whose name says so.
                if (!Interesting.Any(i => mn.Contains(i, StringComparison.OrdinalIgnoreCase))) continue;
                if (!typeMatches) { _out.WriteLine(header); typeMatches = true; }
                var attrs = m.Attributes;
                var vis = attrs & MethodAttributes.MemberAccessMask;
                var isStatic = (attrs & MethodAttributes.Static) != 0;
                _out.WriteLine($"    method {mn} [{vis}{(isStatic ? ", Static" : ", Instance")}]");
            }
        }
    }
}
