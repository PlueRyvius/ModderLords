using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace ModderLords.Core.Overlay;

/// <summary>
/// Builds the small simulation-only asset view a dedicated server can safely read from a client-packaged mod. A full
/// client TPAC contains render resources that have caused native access violations in the headless engine; this class
/// retains only skeleton, animation, animation-clip and physics-shape records, copying opaque metadata and compressed
/// payloads byte-for-byte. Outputs live only below the launcher's private overlay.
/// </summary>
public static class HeadlessAssetProjection
{
    private static readonly IReadOnlyDictionary<Guid, string> SimulationTypes = new Dictionary<Guid, string>
    {
        [Guid.Parse("c635a3d5-eabb-45dd-883e-aa57e4196113")] = "Skeleton",
        [Guid.Parse("bafab007-7e3f-453f-bac6-e7640043112b")] = "SkeletalAnimation",
        [Guid.Parse("506509c8-e563-4ca4-b166-a53b92e913a7")] = "AnimationClip",
        [Guid.Parse("e8528e0e-64b6-4e61-bae0-7569c0452aea")] = "PhysicsShape",
    };

    /// <summary>Texture records. Excluded wholesale — they are the render resources that crash a headless engine.</summary>
    private static readonly Guid TextureType = Guid.Parse("c974cbcb-5f1c-49f6-9a32-2b5b6c92c2e8");

    /// <summary>
    /// The exception to "no textures": a handful of world-map textures are not drawn, they are read as lookup
    /// grids that drive simulation decisions. <c>worldmap_battle_scene_grid</c> is the one that decides which
    /// battle scene a map position produces, so a server without it cannot tell a client which terrain to build.
    ///
    /// Measured 2026-09-12: with these absent, a field battle killed the client in
    /// <c>rglGPU_device::create_texture_array … CreateTexture2D … The parameter is incorrect</c> while the server
    /// carried on reporting a healthy mission. TAOM ships both inside a 908 MB pack that projected to nothing.
    ///
    /// Matched by name rather than by a per-mod offset table, so any mod that replaces the campaign map is covered
    /// by the same rule: these names come from the stock world-map convention, not from TAOM.
    /// </summary>
    private static readonly HashSet<string> SimulationGridTextures = new(StringComparer.OrdinalIgnoreCase)
    {
        "worldmap_battle_scene_grid", "worldmap_colorgrade_grid_custom", "worldmap_colorgrade_grid",
    };

    /// <summary>True for a record the server needs, whether it is a simulation type or a lookup-grid texture.</summary>
    private static bool IsWanted(Guid kind, string name)
        => SimulationTypes.ContainsKey(kind) || (kind == TextureType && SimulationGridTextures.Contains(name));

    private const string Marker = ".modderlords-headless-assets.json";

    /// <summary>One asset record as it exists in a package: what it is called and what type it is.</summary>
    public sealed record InventoryEntry(string Name, string Type, bool IsSimulation, string Package);

    /// <summary>
    /// Lists every asset record in every .tpac under <paramref name="root"/>, recursively.
    ///
    /// This exists to settle a specific class of question with evidence. A server run logs hundreds of
    /// "Could not find animation: X" warnings, and the obvious conclusion — that the projection stripped X — is
    /// testable only by asking which records actually exist. Measured 2026-09-11: all 116 distinct names a TAOM
    /// run complained about were absent from every installed package, client and server alike, so there was
    /// nothing to widen the projection to include; they are dangling references in a mod's action_sets.xml.
    ///
    /// A substring search over the raw bytes will NOT answer this: a name appears inside other records as a
    /// dependency reference, so it reads as "present" when no such record exists. Parse the table instead.
    /// </summary>
    public static IReadOnlyList<InventoryEntry> Inventory(string root)
    {
        var found = new List<InventoryEntry>();
        if (!Directory.Exists(root)) return found;
        foreach (var file in Directory.EnumerateFiles(root, "*.tpac", SearchOption.AllDirectories).OrderBy(p => p))
        {
            Package package;
            try { package = ReadPackage(file); }
            catch { continue; } // an unreadable package is not this diagnostic's problem to report
            foreach (var record in package.Records)
                found.Add(new InventoryEntry(record.Name,
                    SimulationTypes.TryGetValue(record.Kind, out var t) ? t
                        : record.Kind == TextureType ? "Texture" : record.Kind.ToString(),
                    IsWanted(record.Kind, record.Name), file));
        }
        return found;
    }

    public sealed record Result(string ModuleId, string OutputPath, int PackageCount, long Bytes, int AssetCount, bool Reused);
    private sealed record SourceStamp(string File, long Length, long LastWriteUtcTicks);
    private sealed record CacheManifest(string ModuleId, IReadOnlyList<SourceStamp> Sources, Result Result);
    private sealed record Segment(int Location, long Offset, long Stored);
    private sealed record AssetRecord(Guid Kind, string Name, byte[] Raw, IReadOnlyList<Segment> Segments);
    private sealed record Package(byte[] Header, int Version, IReadOnlyList<AssetRecord> Records);

    /// <summary>
    /// Returns null when the module has no client AssetPackages or contains no simulation records. Existing generated
    /// output is reused only when every source package's size and timestamp still match the cache manifest.
    /// </summary>
    public static Result? Prepare(string moduleId, string moduleFolder, string outputRoot)
    {
        var source = Path.Combine(moduleFolder, "AssetPackages");
        if (!Directory.Exists(source)) return null;
        var packages = Directory.EnumerateFiles(source, "*.tpac", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        if (packages.Count == 0) return null;

        var output = Path.Combine(outputRoot, moduleId, "DsAssetPackages");
        var manifestPath = Path.Combine(output, Marker);
        var stamps = packages.Select(Stamp).ToList();
        if (TryReuse(manifestPath, moduleId, stamps, output, out var reused)) return reused;

        RefuseUnexpectedDirectory(output);
        Directory.CreateDirectory(output);
        var results = new List<PackageResult>();
        var totalBytes = 0L;
        var assetCount = 0;
        foreach (var packagePath in packages)
        {
            var package = ReadPackage(packagePath);
            var selected = package.Records.Where(r => IsWanted(r.Kind, r.Name)).ToList();
            if (selected.Count == 0) continue;
            var destination = Path.Combine(output, Path.GetFileName(packagePath));
            var result = WriteSubset(packagePath, destination, package, selected);
            results.Add(result);
            totalBytes += result.Bytes;
            assetCount += result.AssetCount;
        }

        if (results.Count == 0)
        {
            DeleteOwnedDirectory(output);
            return null;
        }

        var final = new Result(moduleId, output, results.Count, totalBytes, assetCount, false);
        var manifest = new CacheManifest(moduleId, stamps, final);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        return final;
    }

    private static bool TryReuse(string manifestPath, string moduleId, IReadOnlyList<SourceStamp> stamps, string output,
        out Result? result)
    {
        result = null;
        try
        {
            if (!File.Exists(manifestPath)) return false;
            var manifest = JsonSerializer.Deserialize<CacheManifest>(File.ReadAllText(manifestPath));
            if (manifest is null || !manifest.ModuleId.Equals(moduleId, StringComparison.OrdinalIgnoreCase) ||
                manifest.Sources.Count != stamps.Count || !manifest.Sources.SequenceEqual(stamps) ||
                manifest.Result.PackageCount == 0 || !Directory.Exists(output)) return false;
            foreach (var file in Directory.EnumerateFiles(output, "*.tpac")) if (new FileInfo(file).Length == 0) return false;
            result = manifest.Result with { Reused = true, OutputPath = output };
            return true;
        }
        catch { return false; }
    }

    private static SourceStamp Stamp(string file)
    {
        var info = new FileInfo(file);
        return new SourceStamp(Path.GetFullPath(file), info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static void RefuseUnexpectedDirectory(string output)
    {
        if (!Directory.Exists(output)) return;
        if (!File.Exists(Path.Combine(output, Marker)))
            throw new IOException($"Refusing to overwrite an unexpected server asset folder: {output}");
        DeleteOwnedDirectory(output);
    }

    private static void DeleteOwnedDirectory(string output)
    {
        if (!Directory.Exists(output)) return;
        if (!File.Exists(Path.Combine(output, Marker)) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException($"Refusing to remove an unexpected server asset folder: {output}");
        Directory.Delete(output, recursive: true);
    }

    private sealed record PackageResult(string File, long Bytes, int AssetCount);

    private static PackageResult WriteSubset(string sourcePath, string destination, Package package, IReadOnlyList<AssetRecord> selected)
    {
        if (File.Exists(destination)) File.Delete(destination);
        var tableEnd = 36L + selected.Sum(x => (long)x.Raw.Length);
        var header = (byte[])package.Header.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24, 4), checked((uint)selected.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28, 4), checked((uint)(tableEnd - 36)));
        var payloads = new List<(long Offset, long Size, long Destination)>();
        var end = tableEnd;
        using (var source = File.OpenRead(sourcePath))
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            output.Write(header);
            foreach (var record in selected)
            {
                var raw = (byte[])record.Raw.Clone();
                foreach (var segment in record.Segments)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(segment.Location, 8), checked((ulong)end));
                    payloads.Add((segment.Offset, segment.Stored, end));
                    end = checked(end + segment.Stored);
                }
                output.Write(raw);
            }
            foreach (var payload in payloads)
            {
                if (output.Position != payload.Destination) throw new InvalidDataException("TPAC payload layout mismatch");
                source.Position = payload.Offset;
                CopyExactly(source, output, payload.Size);
            }
        }
        VerifySubset(sourcePath, destination, selected);
        return new PackageResult(Path.GetFileName(destination), new FileInfo(destination).Length, selected.Count);
    }

    private static void VerifySubset(string sourcePath, string destination, IReadOnlyList<AssetRecord> selected)
    {
        var output = ReadPackage(destination);
        if (output.Records.Count != selected.Count) throw new InvalidDataException("TPAC subset verification count mismatch");
        using var source = File.OpenRead(sourcePath);
        using var generated = File.OpenRead(destination);
        for (var i = 0; i < selected.Count; i++)
        {
            var old = selected[i]; var current = output.Records[i];
            if (old.Kind != current.Kind || !old.Raw.AsSpan().SequenceEqual(RestoreOffsets(current.Raw, old.Segments, current.Segments)))
                throw new InvalidDataException("TPAC subset changed asset metadata");
            for (var j = 0; j < old.Segments.Count; j++)
            {
                var a = old.Segments[j]; var b = current.Segments[j];
                if (a.Stored != b.Stored) throw new InvalidDataException("TPAC subset changed payload size");
                source.Position = a.Offset; generated.Position = b.Offset;
                if (!StreamsEqual(source, generated, a.Stored)) throw new InvalidDataException("TPAC subset changed payload bytes");
            }
        }
    }

    private static byte[] RestoreOffsets(byte[] raw, IReadOnlyList<Segment> original, IReadOnlyList<Segment> generated)
    {
        var restored = (byte[])raw.Clone();
        for (var i = 0; i < original.Count; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(restored.AsSpan(generated[i].Location, 8), checked((ulong)original[i].Offset));
        return restored;
    }

    private static bool StreamsEqual(Stream left, Stream right, long size)
    {
        var a = new byte[1024 * 1024]; var b = new byte[a.Length];
        while (size > 0)
        {
            var want = (int)Math.Min(size, a.Length);
            ReadExactly(left, a, want); ReadExactly(right, b, want);
            if (!a.AsSpan(0, want).SequenceEqual(b.AsSpan(0, want))) return false;
            size -= want;
        }
        return true;
    }

    private static Package ReadPackage(string path)
    {
        var length = new FileInfo(path).Length;
        using var stream = File.OpenRead(path);
        var header = ReadExactly(stream, 36);
        if (!header.AsSpan(0, 4).SequenceEqual("TPAC"u8)) throw new InvalidDataException($"Not a TPAC package: {path}");
        var version = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4)));
        var count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24, 4)));
        if (version is not (1 or 2) || count < 0 || count > 1_000_000)
            throw new NotSupportedException($"Unsupported TPAC version/count {version}/{count} in {path}");
        var records = new List<AssetRecord>(count);
        for (var i = 0; i < count; i++)
        {
            var start = stream.Position;
            var kind = new Guid(ReadExactly(stream, 16));
            _ = ReadExactly(stream, 16); // asset id, retained in the opaque raw record
            if (version == 2) _ = ReadExactly(stream, 4);
            var nameSize = ReadUInt32(stream);
            if (nameSize > 1_000_000) throw new InvalidDataException("Invalid TPAC name size");
            // Kept rather than discarded, so Inventory can answer "does this module actually supply asset X?" with
            // evidence. The bytes are still carried verbatim inside the opaque Raw copy; this only reads them.
            var name = System.Text.Encoding.UTF8.GetString(ReadExactly(stream, checked((int)nameSize))).TrimEnd('\0');
            var metadataSize = ReadUInt64(stream);
            if (metadataSize > (ulong)(length - stream.Position)) throw new InvalidDataException("Invalid TPAC metadata size");
            stream.Position += checked((long)metadataSize);
            _ = ReadExactly(stream, 8);
            var segmentCount = ReadUInt32(stream);
            if (segmentCount > 100_000) throw new InvalidDataException("Invalid TPAC segment count");
            var segments = new List<Segment>(checked((int)segmentCount));
            for (var j = 0; j < segmentCount; j++)
            {
                var location = checked((int)(stream.Position - start));
                var offset = ReadUInt64(stream); var actual = ReadUInt64(stream); var stored = ReadUInt64(stream);
                _ = actual;
                _ = ReadExactly(stream, 45);
                if (offset > (ulong)length || stored > (ulong)length - offset) throw new InvalidDataException("TPAC segment extends beyond source");
                segments.Add(new Segment(location, checked((long)offset), checked((long)stored)));
            }
            var dependencies = ReadUInt32(stream);
            if (dependencies > (ulong)(length - stream.Position) / 48) throw new InvalidDataException("Invalid TPAC dependency count");
            stream.Position += checked((long)dependencies * 48);
            var end = stream.Position;
            stream.Position = start;
            var raw = ReadExactly(stream, checked((int)(end - start)));
            records.Add(new AssetRecord(kind, name, raw, segments));
            stream.Position = end;
        }
        var tableEnd = stream.Position;
        foreach (var record in records)
            foreach (var segment in record.Segments)
                if (segment.Stored > 0 && segment.Offset < tableEnd) throw new InvalidDataException("TPAC payload overlaps resource table");
        return new Package(header, version, records);
    }

    private static uint ReadUInt32(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[4]; ReadExactly(stream, bytes); return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static ulong ReadUInt64(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[8]; ReadExactly(stream, bytes); return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static byte[] ReadExactly(Stream stream, int count)
    {
        var bytes = new byte[count]; ReadExactly(stream, bytes, count); return bytes;
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer[read..]);
            if (n <= 0) throw new InvalidDataException("Truncated TPAC");
            read += n;
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int count)
        => ReadExactly(stream, buffer.AsSpan(0, count));

    private static void CopyExactly(Stream source, Stream destination, long size)
    {
        var buffer = new byte[1024 * 1024];
        while (size > 0)
        {
            var want = (int)Math.Min(size, buffer.Length);
            ReadExactly(source, buffer, want);
            destination.Write(buffer, 0, want);
            size -= want;
        }
    }
}
