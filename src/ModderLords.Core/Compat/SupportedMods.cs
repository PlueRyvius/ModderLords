using System.Text.Json;

namespace ModderLords.Core.Compat;

/// <summary>One mod ModderLords does something specific for, as the Supported mods tab shows it.</summary>
public sealed record SupportedMod
{
    public string Name { get; init; } = "";
    /// <summary>The mod's Bannerlord module id(s); the tab marks a mod as installed when any of them is on this PC.</summary>
    public IReadOnlyList<string> ModuleIds { get; init; } = [];
    /// <summary>Works, Settings (works with Settings sync on), Partial or Broken.</summary>
    public string Status { get; init; } = "";
    public string Summary { get; init; } = "";
    /// <summary>What ModderLords does for it, one plain sentence each.</summary>
    public IReadOnlyList<string> Details { get; init; } = [];
    /// <summary>Players must have ModderLords.Compat (Settings sync) for the support to work.</summary>
    public bool NeedsCompat { get; init; }
    /// <summary>The ModderLords version the support first shipped in, when known.</summary>
    public string Since { get; init; } = "";
}

/// <summary>
/// The Supported mods tab's list: what ModderLords does for each mod it has specific support for, so nobody has to
/// read the patch notes. Kept in <c>supported-mods.json</c>, embedded in this assembly; <see cref="General"/> is the
/// support every mod gets.
/// </summary>
public sealed record SupportedModList
{
    public IReadOnlyList<string> General { get; init; } = [];
    public IReadOnlyList<SupportedMod> Mods { get; init; } = [];

    public const string ResourceName = "ModderLords.Core.supported-mods.json";

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static SupportedModList Parse(string json) => JsonSerializer.Deserialize<SupportedModList>(json, Options) ?? new SupportedModList();

    /// <summary>The list shipped with this build.</summary>
    public static SupportedModList Bundled()
    {
        using var stream = typeof(SupportedModList).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(ResourceName + " is not embedded in this build.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}
