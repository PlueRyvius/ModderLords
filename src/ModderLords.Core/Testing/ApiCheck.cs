using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace ModderLords.Core.Testing;

/// <summary>
/// Finds the calls a mod's DLL makes into the game that the installed game no longer has, before the game starts. A
/// game update that removes or changes a method otherwise shows only as a MissingMethodException when the call first
/// runs, often inside the engine where it crashes the game (Bannerlord v1.5.7 changed Campaign's constructor, and
/// making a world crashed with nothing but the watchdog's dialog).
///
/// Reads metadata only, never loads code: every type, method and field the game's assemblies define (with base
/// types, across assemblies), then each reference a mod DLL makes into the game's namespaces, matched by type, name
/// and parameter count. Overloads that differ only in parameter types are not told apart.
/// </summary>
public static class ApiCheck
{
    /// <summary>The game's own namespaces: references into these must resolve against the installed game.</summary>
    public static readonly IReadOnlyList<string> GameNamespaces = ["TaleWorlds.", "SandBox.", "StoryMode.", "Helpers."];

    /// <summary>What <paramref name="dll"/> references in <paramref name="namespaces"/> that <paramref name="game"/> lacks, sorted.</summary>
    public static IReadOnlyList<string> Missing(string dll, ApiIndex game, IReadOnlyList<string>? namespaces = null)
    {
        namespaces ??= GameNamespaces;
        bool Ours(string type) => namespaces.Any(n => type.StartsWith(n, StringComparison.Ordinal));
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        using var pe = new PEReader(File.OpenRead(dll));
        if (!pe.HasMetadata) return [];
        var md = pe.GetMetadataReader();

        foreach (var h in md.TypeReferences)
        {
            var name = ApiIndex.TypeName(md, h);
            if (name.Length > 0 && Ours(name) && !game.HasType(name)) missing.Add("type " + name);
        }
        foreach (var h in md.MemberReferences)
        {
            var mr = md.GetMemberReference(h);
            var parent = ApiIndex.TypeName(md, mr.Parent);
            if (parent.Length == 0 || !Ours(parent) || !game.HasType(parent)) continue;   // a missing type is listed once, above
            var name = md.GetString(mr.Name);
            var reader = md.GetBlobReader(mr.Signature);
            var header = reader.ReadSignatureHeader();
            if (header.Kind == SignatureKind.Field)
            {
                if (!game.HasField(parent, name)) missing.Add($"field {parent}::{name}");
                continue;
            }
            if (header.IsGeneric) reader.ReadCompressedInteger();
            var count = reader.ReadCompressedInteger();
            if (!game.HasMethod(parent, name, count))
                missing.Add($"method {parent}::{name} ({count} parameter{(count == 1 ? "" : "s")})");
        }
        return missing.ToList();
    }

    /// <summary>
    /// Every DLL of <paramref name="dlls"/> checked against <paramref name="game"/>: the ones with something missing,
    /// each with what. A DLL that is not a .NET assembly is skipped; one that cannot be read is reported.
    /// </summary>
    public static IReadOnlyList<(string Dll, IReadOnlyList<string> Missing)> CheckAll(IEnumerable<string> dlls, ApiIndex game)
    {
        var found = new List<(string, IReadOnlyList<string>)>();
        foreach (var dll in dlls.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyList<string> missing;
            try { missing = Cached(dll, game); }
            catch (BadImageFormatException) { continue; }
            catch (IOException ex) { missing = ["could not be read: " + ex.Message]; }
            if (missing.Count > 0) found.Add((dll, missing));
        }
        return found;
    }

    /// <summary>
    /// The DLLs in <paramref name="bin"/> the game loads for a module: those its SubModule.xml names
    /// (<paramref name="named"/>), and every DLL beside them that they reference, and so on. A DLL that is there but
    /// that nothing references (a framework's build for another game version, loaded by name at runtime) is not.
    /// </summary>
    public static IReadOnlyList<string> LoadedDlls(string bin, IEnumerable<string> named)
    {
        if (!Directory.Exists(bin)) return [];
        var byName = Directory.EnumerateFiles(bin, "*.dll").ToDictionary(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();
        var queue = new Queue<string>(named.Where(n => !string.IsNullOrEmpty(n)).Select(n => Path.GetFileNameWithoutExtension(n)));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!seen.Add(name) || !byName.TryGetValue(name, out var file)) continue;
            found.Add(file);
            try
            {
                using var pe = new PEReader(File.OpenRead(file));
                if (!pe.HasMetadata) continue;
                var md = pe.GetMetadataReader();
                foreach (var r in md.AssemblyReferences) queue.Enqueue(md.GetString(md.GetAssemblyReference(r).Name));
            }
            catch (BadImageFormatException) { }
            catch (IOException) { }
        }
        return found;
    }

    // Per DLL content and game: a run checks the same mods against the same game again and again.
    private static readonly ConcurrentDictionary<(string, string), IReadOnlyList<string>> Cache = new();

    private static IReadOnlyList<string> Cached(string dll, ApiIndex game)
    {
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll)));
        return Cache.GetOrAdd((hash, game.Key), _ => Missing(dll, game));
    }
}

/// <summary>The types, methods and fields a set of assemblies defines (the installed game's), by full name.</summary>
public sealed class ApiIndex
{
    private sealed class TypeEntry
    {
        public string? BaseType;
        public readonly HashSet<(string Name, int Count)> Methods = new();
        public readonly HashSet<string> Fields = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, TypeEntry> _types = new(StringComparer.Ordinal);

    /// <summary>Identifies this index's assemblies by name, size and time, for caching results against it.</summary>
    public string Key { get; private set; } = "";

    public int TypeCount => _types.Count;

    /// <summary>Indexes every .NET assembly among <paramref name="dlls"/>; others are skipped.</summary>
    public static ApiIndex Of(IEnumerable<string> dlls)
    {
        var index = new ApiIndex();
        var key = new List<string>();
        foreach (var dll in dlls.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var pe = new PEReader(File.OpenRead(dll));
                if (!pe.HasMetadata) continue;
                index.Add(pe.GetMetadataReader());
                var info = new FileInfo(dll);
                key.Add($"{info.Name}:{info.Length}:{info.LastWriteTimeUtc.Ticks}");
            }
            catch (BadImageFormatException) { }
            catch (IOException) { }
        }
        key.Sort(StringComparer.Ordinal);
        index.Key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", key))));
        return index;
    }

    /// <summary>The DLLs in each of <paramref name="folders"/> (not their subfolders) that exist.</summary>
    public static IEnumerable<string> DllsIn(IEnumerable<string> folders) =>
        folders.Where(Directory.Exists).SelectMany(f => Directory.EnumerateFiles(f, "*.dll"));

    private void Add(MetadataReader md)
    {
        foreach (var h in md.TypeDefinitions)
        {
            var def = md.GetTypeDefinition(h);
            var name = DefinitionName(md, h);
            if (!_types.TryGetValue(name, out var entry)) _types[name] = entry = new TypeEntry();
            if (!def.BaseType.IsNil) entry.BaseType ??= TypeName(md, def.BaseType);
            foreach (var mh in def.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                var reader = md.GetBlobReader(m.Signature);
                var header = reader.ReadSignatureHeader();
                if (header.IsGeneric) reader.ReadCompressedInteger();
                entry.Methods.Add((md.GetString(m.Name), reader.ReadCompressedInteger()));
            }
            foreach (var fh in def.GetFields()) entry.Fields.Add(md.GetString(md.GetFieldDefinition(fh).Name));
        }
        // An assembly can forward a type it no longer defines to another one; the type still exists.
        foreach (var h in md.ExportedTypes)
        {
            var e = md.GetExportedType(h);
            var ns = md.GetString(e.Namespace);
            var name = ns.Length > 0 ? ns + "." + md.GetString(e.Name) : md.GetString(e.Name);
            if (!_types.ContainsKey(name)) _types[name] = new TypeEntry { BaseType = "<forwarded>" };
        }
    }

    public bool HasType(string type) => _types.ContainsKey(type);

    public bool HasMethod(string type, string name, int count) =>
        Chain(type).Any(t => t.Methods.Contains((name, count))) || Forwarded(type);

    public bool HasField(string type, string name) => Chain(type).Any(t => t.Fields.Contains(name)) || Forwarded(type);

    // A forwarded type's members live in the assembly it forwards to, which may not be indexed: give it the benefit.
    private bool Forwarded(string type) => _types.TryGetValue(type, out var t) && t.BaseType == "<forwarded>";

    /// <summary>The type and its base types that this index knows, nearest first.</summary>
    private IEnumerable<TypeEntry> Chain(string type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var name = type; name is not null && seen.Add(name) && _types.TryGetValue(name, out var entry); name = entry.BaseType)
            yield return entry;
    }

    private static string DefinitionName(MetadataReader md, TypeDefinitionHandle h)
    {
        var def = md.GetTypeDefinition(h);
        var name = md.GetString(def.Name);
        if (def.GetDeclaringType() is { IsNil: false } outer) return DefinitionName(md, outer) + "/" + name;
        var ns = md.GetString(def.Namespace);
        return ns.Length > 0 ? ns + "." + name : name;
    }

    /// <summary>A type reference's full name ("Ns.Outer/Inner"); for a generic instance, its generic type's. Empty for others.</summary>
    internal static string TypeName(MetadataReader md, EntityHandle h)
    {
        switch (h.Kind)
        {
            case HandleKind.TypeReference:
            {
                var tr = md.GetTypeReference((TypeReferenceHandle)h);
                var name = md.GetString(tr.Name);
                if (tr.ResolutionScope.Kind == HandleKind.TypeReference) return TypeName(md, (EntityHandle)tr.ResolutionScope) + "/" + name;
                var ns = md.GetString(tr.Namespace);
                return ns.Length > 0 ? ns + "." + name : name;
            }
            case HandleKind.TypeDefinition:
                return DefinitionName(md, (TypeDefinitionHandle)h);
            case HandleKind.TypeSpecification:
            {
                var reader = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)h).Signature);
                if (reader.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return "";
                reader.ReadSignatureTypeCode();   // class or value type
                return TypeName(md, reader.ReadTypeHandle());
            }
            default:
                return "";
        }
    }
}
