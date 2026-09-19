using System.Xml;

namespace ModderLords.Core.Overlay;

/// <summary>How a community module should participate in the dedicated server.</summary>
public enum ServerRole
{
    /// <summary>Load its submodule DLLs on the server. Headless-exclusion tags are stripped so the engine does not skip them.</summary>
    Run,
    /// <summary>Keep the module (id + version) in the load order for the Coop handshake, but load no code. For client-side frameworks such as UI extenders.</summary>
    DependencyOnly,
    /// <summary>Leave the manifest exactly as shipped; the engine decides (it will skip client-only submodules).</summary>
    AsShipped,
}

/// <summary>
/// Produces the SubModule.xml copy that lives in the shadow folder. Id and Version are never touched: Coop's
/// ModuleValidator compares exactly those between server and clients.
/// </summary>
public static class ManifestRewriter
{
    /// <summary>Tags the engine evaluates in Module.GetSubModuleValiditiy that exclude a submodule from a dedicated server.</summary>
    private static readonly HashSet<string> HeadlessExclusionTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "DedicatedServerType", "IsNoRenderModeElement",
    };

    public sealed record Result(string Xml, IReadOnlyList<string> Changes);

    /// <summary>The value the engine reads for "this submodule is for a dedicated server".</summary>
    private const string DedicatedServerTypeTag = "DedicatedServerType";

    private static string? TagValue(XmlElement sub, string key) =>
        sub.SelectNodes("Tags/Tag")?.Cast<XmlElement>()
            .FirstOrDefault(t => string.Equals(t.GetAttribute("key"), key, StringComparison.OrdinalIgnoreCase))
            ?.GetAttribute("value");

    /// <summary>A submodule the author built for a dedicated server ("custom", or any value that is not "none").</summary>
    private static bool HasServerSubModuleTag(XmlElement sub) =>
        TagValue(sub, DedicatedServerTypeTag) is { } v && !string.Equals(v, "none", StringComparison.OrdinalIgnoreCase);

    /// <summary>A submodule the author explicitly excluded from a dedicated server.</summary>
    private static bool IsClientOnlySubModule(XmlElement sub) =>
        string.Equals(TagValue(sub, DedicatedServerTypeTag), "none", StringComparison.OrdinalIgnoreCase);

    public static Result Rewrite(string originalXml, ServerRole role, IReadOnlyCollection<string>? keepSubModuleClassTypes = null)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(originalXml);
        var changes = new List<string>();
        var module = doc.SelectSingleNode("Module") ?? throw new InvalidDataException("SubModule.xml has no <Module> root");

        if (role == ServerRole.AsShipped) return new Result(originalXml, changes);

        var subModules = module.SelectNodes("SubModules/SubModule")?.Cast<XmlElement>().ToList() ?? [];

        // Does the author already ship a server variant? A DedicatedServerType of anything but "none" means this
        // submodule is meant to run headless, which in turn means the "none" ones are its deliberate client-side
        // counterparts, not a mislabel.
        var hasServerVariant = role == ServerRole.Run && subModules.Any(HasServerSubModuleTag);

        foreach (var sub in subModules)
        {
            var name = sub.SelectSingleNode("Name")?.Attributes?["value"]?.Value ?? "?";
            var classType = sub.SelectSingleNode("SubModuleClassType")?.Attributes?["value"]?.Value ?? "";

            if (role == ServerRole.DependencyOnly && !(keepSubModuleClassTypes?.Contains(classType) ?? false))
            {
                sub.ParentNode!.RemoveChild(sub);
                changes.Add($"removed submodule '{name}' ({classType})");
                continue;
            }

            // Run, on a module that split itself into client and server halves: honour the split instead of
            // flattening it. Stripping every exclusion tag made the server load the CLIENT submodule alongside the
            // server one - which is how FamilyAppearanceEditor took the server down with a native access violation
            // on 2026-09-19, patching barber screens in a headless process. Run exists to override a mod that
            // wrongly says "not on a server"; it was never meant to override a mod that said "use THIS half".
            if (hasServerVariant && IsClientOnlySubModule(sub))
            {
                sub.ParentNode!.RemoveChild(sub);
                changes.Add($"removed client-only submodule '{name}' ({classType}); the module ships a dedicated-server submodule");
                continue;
            }

            // Run (or an allow-listed DependencyOnly submodule): strip the tags that make the server skip it.
            var tags = sub.SelectNodes("Tags/Tag")?.Cast<XmlElement>().ToList() ?? [];
            foreach (var tag in tags)
            {
                var key = tag.GetAttribute("key");
                if (!HeadlessExclusionTags.Contains(key)) continue;
                tag.ParentNode!.RemoveChild(tag);
                changes.Add($"stripped tag {key}={tag.GetAttribute("value")} from submodule '{name}'");
            }
        }

        // Write through a UTF-8 stream so the XML declaration says utf-8 (a StringWriter would stamp utf-16).
        using var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false, Encoding = new System.Text.UTF8Encoding(false) }))
            doc.Save(w);
        return new Result(System.Text.Encoding.UTF8.GetString(ms.ToArray()), changes);
    }
}
