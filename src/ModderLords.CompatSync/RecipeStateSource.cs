using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ModderLords.CompatSync;

/// <summary>
/// Mod state named by the recipe (<c>Mods[].SyncState</c>, "Ns.Type.field"): static fields that server-only code
/// writes and player-facing code reads. Exposed as one settings object per declaring type (id
/// <c>state:&lt;Type&gt;</c>) so the existing settings-sync channel carries them: the server captures and broadcasts
/// changes, clients apply them. Nothing is persisted (it is runtime state, the save carries it) and nothing is shown in
/// the host's settings editor. Free of game, Coop and Log dependencies so the test project can run it.
/// </summary>
public sealed class RecipeStateSource : ISettingsSource
{
    public const string Prefix = "state:";

    private readonly HashSet<string> _members = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ReflectionPropertyRef>> _resolved = new Dictionary<string, List<ReflectionPropertyRef>>(StringComparer.Ordinal);
    private readonly HashSet<string> _unresolved = new HashSet<string>(StringComparer.Ordinal);
    private bool _dirty;

    public string Name => "recipe state";
    public bool Present => _resolved.Count > 0;
    public bool Owns(string settingsId) => settingsId.StartsWith(Prefix, StringComparison.Ordinal) && _resolved.ContainsKey(settingsId);

    /// <summary>Adds a "Ns.Type.field" to the list; true when it was new. Resolution happens on the next Refresh.</summary>
    public bool Add(string member)
    {
        if (string.IsNullOrEmpty(member) || member.IndexOf('.') <= 0) return false;
        lock (_members)
        {
            if (!_members.Add(member)) return false;
            _dirty = true;
            return true;
        }
    }

    public int MemberCount { get { lock (_members) return _members.Count; } }

    /// <summary>"N field(s) in M type(s), K not found yet".</summary>
    public string Summary()
    {
        lock (_members) return $"{_resolved.Values.Sum(l => l.Count)} field(s) in {_resolved.Count} type(s), {_unresolved.Count} not found yet";
    }

    /// <summary>Resolves listed members whose type has loaded since. Cheap when nothing changed.</summary>
    public void Refresh()
    {
        lock (_members)
        {
            if (!_dirty && _unresolved.Count == 0) return;
            _dirty = false;
            var pending = _members.Where(m => !_resolved.Values.Any(l => l.Any(r => r.Member.DeclaringType!.FullName + "." + r.Id == m))).ToList();
            _unresolved.Clear();
            foreach (var member in pending)
            {
                var dot = member.LastIndexOf('.');
                var typeName = member.Substring(0, dot);
                var fieldName = member.Substring(dot + 1);
                var type = FindType(typeName);
                var field = type?.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (field is null || field.IsLiteral || field.IsInitOnly || !ValueConverter.IsSupported(field.FieldType)) { _unresolved.Add(member); continue; }
                var id = Prefix + typeName;
                if (!_resolved.TryGetValue(id, out var list)) _resolved[id] = list = new List<ReflectionPropertyRef>();
                list.Add(new ReflectionPropertyRef(field, null));
            }
        }
    }

    public List<SettingsSnapshot> Capture()
    {
        lock (_members)
            return _resolved.Select(kv => new SettingsSnapshot { SettingsId = kv.Key, Payload = ValueConverter.Payload(kv.Value) }).ToList();
    }

    public int Apply(string settingsId, IDictionary<string, string> values, out string report)
    {
        List<ReflectionPropertyRef>? refs;
        lock (_members) { if (!_resolved.TryGetValue(settingsId, out refs)) { report = "unknown state object"; return 0; } }
        return ValueConverter.ApplyTo(refs, values, out report);
    }

    public string Save(string settingsId) => "not persisted: runtime mod state, the save carries it";

    public List<object?> Describe() => new List<object?>();

    private static Type? FindType(string fullName)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (a.IsDynamic) continue;
            Type? t = null;
            try { t = a.GetType(fullName, false); } catch { }
            if (t != null) return t;
        }
        return null;
    }
}
