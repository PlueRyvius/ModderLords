using System.Reflection;
using Mono.Cecil;
using ModderLords.Analysis;
using ModderLords.Operations;

namespace ModderLords.Core.Tests;

/// <summary>
/// The whole scheme rests on one claim: the hash the maintainer captures offline with Cecil is the hash the game
/// computes at runtime with reflection. Two readers, two libraries, one format — so they are held to each other over
/// a real assembly rather than a hand-built sample. If a future edit changes one reader's output, this fails.
/// </summary>
public sealed class MethodSurfaceParityTests
{
    private static readonly Assembly Subject = typeof(MethodSurface).Assembly;

    [Fact]
    public void BothReadersAgreeOnEveryMethodOfARealAssembly()
    {
        using var cecil = AssemblyDefinition.ReadAssembly(Subject.Location);
        var mismatches = new List<string>();
        var compared = 0;

        foreach (var type in cecil.MainModule.GetTypes())
        {
            var runtime = Subject.GetType(type.FullName.Replace('/', '+'));
            if (runtime == null) continue;
            foreach (var method in type.Methods)
            {
                // Overloads must be paired by signature, not by name and arity, or the comparison reports a
                // difference between two genuinely different methods.
                var wanted = string.Join(",", method.Parameters.Select(p => MethodSurface.NormalizeTypeName(p.ParameterType.FullName)));
                var reflected = runtime
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Cast<MethodBase>()
                    .Concat(runtime.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    .FirstOrDefault(m => m.Name == method.Name && string.Join(",",
                        m.GetParameters().Select(p => MethodSurface.NormalizeTypeName(p.ParameterType.FullName ?? p.ParameterType.Name))) == wanted);
                if (reflected == null || reflected.IsGenericMethodDefinition || !method.HasBody) continue;

                compared++;
                var fromCecil = CecilSurface.Canonicalize(method);
                var fromReflection = MethodSurface.Canonicalize(reflected);
                if (fromCecil != fromReflection)
                    mismatches.Add($"{type.FullName}::{method.Name}\n--- cecil ---\n{fromCecil}\n--- reflection ---\n{fromReflection}");
            }
        }

        Assert.True(compared > 20, $"only {compared} methods compared; the parity check is not covering anything");
        Assert.True(mismatches.Count == 0, string.Join("\n\n", mismatches.Take(3)));
    }

    [Fact]
    public void AnUnrelatedChangeElsewhereInTheAssemblyDoesNotMoveATargetsHash()
    {
        // The point of the whole change: metadata tokens renumber when an assembly gains unrelated code, so a
        // file hash moves and an IL-token hash moves with it. The canonical form resolves tokens to names, so the
        // same source method hashes the same in two assemblies that differ everywhere else.
        var first = Build("class Other { public int A() { return 1; } }");
        var second = Build("class Other { public int A() { return 1; } public string B(string s) { return s + \"x\"; } class C { } }");
        Assert.NotEqual(AnalysisJson.FileHash(first), AnalysisJson.FileHash(second));

        using var one = AssemblyDefinition.ReadAssembly(first);
        using var two = AssemblyDefinition.ReadAssembly(second);
        Assert.Equal(CecilSurface.Read(one, "Other::A")!.BodyHash, CecilSurface.Read(two, "Other::A")!.BodyHash);
    }

    [Fact]
    public void ChangingTheTargetItselfMovesTheHashAndANewCallerIsCounted()
    {
        using var before = AssemblyDefinition.ReadAssembly(Build("class Other { public int A() { return 1; } }"));
        using var changed = AssemblyDefinition.ReadAssembly(Build("class Other { public int A() { return 2; } }"));
        Assert.NotEqual(CecilSurface.Read(before, "Other::A")!.BodyHash, CecilSurface.Read(changed, "Other::A")!.BodyHash);

        Assert.Equal(0, CecilSurface.Read(before, "Other::A")!.Callers);
        using var called = AssemblyDefinition.ReadAssembly(Build("class Other { public int A() { return 1; } public int B() { return A(); } }"));
        Assert.Equal(1, CecilSurface.Read(called, "Other::A")!.Callers);
    }

    /// <summary>Compiles a scrap of C# to a real DLL so the assertions run against genuine IL, not a mock.</summary>
    private static string Build(string source)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "surface-parity", Guid.NewGuid().ToString("N"))).FullName;
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source);
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(a.Location))
            .ToArray();
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("Scrap", [tree], references,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
        var path = Path.Combine(dir, "Scrap.dll");
        using var file = File.Create(path);
        var result = compilation.Emit(file);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        return path;
    }
}
