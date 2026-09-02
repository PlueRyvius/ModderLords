using System.Collections.Generic;

namespace ModularCoop.CompatSync;

/// <summary>One settings object serialised as "propertyId\tvalue" lines (invariant culture); the wire and live-dir payload.</summary>
public sealed class SettingsSnapshot
{
    public string SettingsId = "";
    public string Payload = "";
}

/// <summary>
/// A place mod settings live (MCM, plain settings classes, ...). Every method is best-effort and must never throw
/// into the engine: report problems through the returned report strings and the Log.
/// </summary>
public interface ISettingsSource
{
    string Name { get; }
    /// <summary>True once the source has something to offer (MCM loaded, at least one static object found, ...).</summary>
    bool Present { get; }
    /// <summary>Does this source own the SettingsId (used to route Apply/Save)?</summary>
    bool Owns(string settingsId);
    /// <summary>Re-run discovery for lazily created objects; cheap when nothing changed.</summary>
    void Refresh();
    List<SettingsSnapshot> Capture();
    /// <summary>Sets property values by id; returns the number changed. Unknown ids are ignored, unsupported kinds reported.</summary>
    int Apply(string settingsId, IDictionary<string, string> values, out string report);
    /// <summary>Persists a settings object so the value survives a restart. One-line result.</summary>
    string Save(string settingsId);
    /// <summary>Editor description (MiniJson dictionaries: SettingsId, DisplayName, Folder, Groups[{Name, Properties[]}]).</summary>
    List<object?> Describe();
}
