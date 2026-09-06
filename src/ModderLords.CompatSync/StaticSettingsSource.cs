using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace ModderLords.CompatSync;

/// <summary>
/// Settings that are not MCM: plain classes in community mod assemblies with public settable primitives, reached
/// through a static singleton (X.Instance, Holder.Instance.Config) or static members. Discovered by reflection on
/// the tick, never during module load; every step is guarded and a type that throws once is blacklisted.
/// SettingsId = "static:&lt;AssemblyName&gt;:&lt;Type.FullName&gt;".
/// </summary>
public sealed class StaticSettingsSource : ISettingsSource
{
    public const string Prefix = "static:";

    private static readonly string[] NameSuffixes = { "Settings", "Setting", "Config", "Configs", "Configuration", "Options" };
    private static readonly string[] SingletonNames = { "Instance", "Current", "Settings", "Config", "Default" };
    private static readonly string[] HolderProps = { "Config", "Settings", "Configuration", "Options", "Current" };
    private static readonly string[] ExcludedBases = { "ViewModel", "CampaignBehaviorBase", "MissionBehavior", "MissionLogic", "MBSubModuleBase", "ScreenBase", "GameModel", "MissionView" };
    private static readonly string[] ExcludedNameParts = { "Template", "Snapshot", "Dto", "ViewModel", "VM" };
    private static readonly string[] ExcludedNamespaceSegments = { "SaveData", "Saveable", "Serialization" };
    private static readonly string[] ExcludedNamespaceTails = { "Data", "DataTypes" };
    /// <summary>A singleton alone is weak evidence; skip obvious managers/UI when the name does not look like settings.</summary>
    private static readonly string[] SingletonOnlyExcludedSuffixes = { "Manager", "UI", "Screen", "Widget", "Behavior", "Behaviour", "Handler", "Service", "Controller", "Patch", "Patches" };
    private static readonly string[] SkipAssemblyPrefixes =
    {
        "TaleWorlds.", "SandBox", "StoryMode", "CustomBattle", "Multiplayer", "BirthAndDeath", "System", "Microsoft", "mscorlib", "netstandard",
        "0Harmony", "MCM", "Bannerlord.", "Newtonsoft", "LiteNetLib", "ProtoBuf", "protobuf", "Autofac", "Coop", "GameInterface", "Common", "ModderLords.",
        "Serilog", "HarmonyLib", "Mono.", "UIExtenderEx", "ButterLib",
    };
    private static readonly string[] StockModules = { "Native", "SandBox", "SandBoxCore", "StoryMode", "CustomBattle", "Multiplayer", "BirthAndDeath", "Coop", "CoopNightly", "DedicatedServer.Windows", "ModderLords.Compat", "DedicatedServer.ModderLordsCompat", "Bannerlord.Harmony", "Bannerlord.ButterLib", "Bannerlord.UIExtenderEx", "Bannerlord.MBOptionScreen" };
    private static readonly string[] SavePrefixes = { "Save", "Write", "Store", "Persist", "Serialize", "Flush" };

    /// <summary>Full type names or trailing-* globs from recipes.json / the compat record. Include forces a type in past the name rule; Exclude drops it.</summary>
    public static List<string> Include { get; } = new List<string>();
    public static List<string> Exclude { get; } = new List<string>();

    private sealed class StaticObject
    {
        public string Id = "";
        public Type Type = null!;
        public string ModuleId = "";
        public string DisplayName = "";
        public Func<object?>? Target;            // null = static members only
        public bool IsStatic;
        public Type? HolderType;
        public Func<object?>? Holder;            // the Holder.Instance object (hop 1) for Save probing
        public List<ReflectionPropertyRef> Refs = new List<ReflectionPropertyRef>();
        public string? SaveVia;
    }

    private sealed class Pending
    {
        public Type Type = null!;
        public string ModuleId = "";
        public Func<object?> Resolve = null!;
        public bool Deferred;                     // non-auto getter: only read once a campaign exists / after the grace period
        public Type? HolderType;
        public Func<object?>? Holder;
    }

    private readonly Dictionary<string, StaticObject> _objects = new Dictionary<string, StaticObject>(StringComparer.Ordinal);
    private readonly List<Pending> _pending = new List<Pending>();
    private readonly HashSet<string> _seenAssemblies = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> _blacklist = new HashSet<string>(StringComparer.Ordinal);
    private DateTime _firstRefresh;
    private int _announced;

    public string Name => "static";
    public bool Present => _objects.Count > 0;
    public bool Owns(string settingsId) => settingsId.StartsWith(Prefix, StringComparison.Ordinal) && _objects.ContainsKey(settingsId);
    public int Count => _objects.Count;
    public IEnumerable<string> Ids => _objects.Keys;

    // ---- discovery ---------------------------------------------------------------------------------------------

    public void Refresh()
    {
        if (_firstRefresh == default) _firstRefresh = DateTime.UtcNow;
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            string name;
            try { if (a.IsDynamic) continue; name = a.GetName().Name ?? ""; } catch { continue; }
            if (!_seenAssemblies.Add(a.FullName ?? name)) continue;
            var moduleId = ModuleIdOf(a);
            if (moduleId == null || SkipAssemblyPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            try { ScanAssembly(a, name, moduleId); }
            catch (Exception ex) { Log.Warn($"static settings: scan of {name} failed: {ex.GetBaseException().Message}"); }
        }
        ResolvePending();
        if (_announced != _objects.Count && _objects.Count > 0)
        {
            _announced = _objects.Count;
            Log.Info($"static settings: found {_objects.Count} object(s): {string.Join(", ", _objects.Keys)}" + (_pending.Count > 0 ? $" ({_pending.Count} pending)" : ""));
        }
    }

    /// <summary>Community module id from the assembly's location (…\Modules\&lt;Id&gt;\bin\…); null for engine/stock/byte-loaded assemblies.</summary>
    private static string? ModuleIdOf(Assembly a)
    {
        string loc;
        try { loc = a.Location; } catch { return null; }
        if (string.IsNullOrEmpty(loc)) return null;
        var parts = loc.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = 0; i < parts.Length - 2; i++)
            if (parts[i].Equals("Modules", StringComparison.OrdinalIgnoreCase) && parts[i + 2].Equals("bin", StringComparison.OrdinalIgnoreCase))
                return StockModules.Contains(parts[i + 1], StringComparer.OrdinalIgnoreCase) ? null : parts[i + 1];
        return null;
    }

    private void ScanAssembly(Assembly a, string asmName, string moduleId)
    {
        Type?[] types;
        try { types = a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types; }
        var classes = types.Where(t => t != null && t.IsClass && !t.IsGenericTypeDefinition && !t.IsNested).Cast<Type>().ToList();
        var candidates = new HashSet<Type>(classes.Where(IsCandidate));

        // Hop 0: the candidate itself has static members or a singleton of its own type.
        foreach (var t in candidates) AddHop0(t, moduleId);

        // Hop 1: Holder.Instance.<Config|Settings|...> returning a candidate (or any class that passes the name rule).
        foreach (var holder in classes)
        {
            var singleton = SingletonMember(holder, holder);
            if (singleton == null) continue;
            foreach (var prop in SafeProps(holder))
            {
                if (prop.GetIndexParameters().Length > 0 || prop.GetGetMethod() == null || prop.GetGetMethod()!.IsStatic) continue;
                if (!HolderProps.Contains(prop.Name)) continue;
                var ct = prop.PropertyType;
                if (!ct.IsClass || ct == typeof(string) || !candidates.Contains(ct)) continue;
                if (_objects.ContainsKey(IdOf(ct)) || _pending.Any(p => p.Type == ct)) continue;
                var holderGet = singleton;
                var holderCopy = holder;
                Func<object?> holderResolve = () => holderGet();
                _pending.Add(new Pending
                {
                    Type = ct, ModuleId = moduleId,
                    Resolve = () => { var h = holderResolve(); return h == null ? null : prop.GetValue(h); },
                    Deferred = SingletonIsDeferred(holder), HolderType = holderCopy, Holder = holderResolve,
                });
            }
        }
    }

    private void AddHop0(Type t, string moduleId)
    {
        var id = IdOf(t);
        if (_objects.ContainsKey(id) || _pending.Any(p => p.Type == t)) return;
        // Static members on the type itself.
        var statics = MemberRefs(t, null, wantStatic: true);
        if (statics.Count > 0)
        {
            Register(new StaticObject { Id = id, Type = t, ModuleId = moduleId, IsStatic = true, Refs = statics, DisplayName = DisplayNameOf(t, moduleId) });
            return;
        }
        var singleton = SingletonMember(t, t);
        if (singleton == null) return;
        _pending.Add(new Pending { Type = t, ModuleId = moduleId, Resolve = singleton, Deferred = SingletonIsDeferred(t) });
    }

    private void ResolvePending()
    {
        if (_pending.Count == 0) return;
        var campaignUp = CampaignExists();
        var graceOver = (DateTime.UtcNow - _firstRefresh).TotalSeconds > 60;
        foreach (var p in _pending.ToList())
        {
            if (p.Deferred && !campaignUp && !graceOver) continue;
            object? instance;
            try { instance = p.Resolve(); }
            catch (Exception ex)
            {
                _pending.Remove(p);
                if (_blacklist.Add(p.Type.FullName ?? p.Type.Name)) Log.Warn($"static settings: {p.Type.FullName} skipped: {ex.GetBaseException().Message}");
                continue;
            }
            if (instance == null) continue;   // not created yet; try again next tick
            var resolve = p.Resolve;
            var refs = MemberRefs(instance.GetType(), resolve, wantStatic: false);
            _pending.Remove(p);
            if (refs.Count == 0) continue;
            Register(new StaticObject
            {
                Id = IdOf(p.Type), Type = p.Type, ModuleId = p.ModuleId, Target = resolve, Refs = refs,
                DisplayName = DisplayNameOf(p.Type, p.ModuleId), HolderType = p.HolderType, Holder = p.Holder,
            });
        }
    }

    private void Register(StaticObject o)
    {
        if (Exclude.Any(g => Glob(g, o.Type.FullName ?? ""))) return;
        _objects[o.Id] = o;
    }

    // ---- heuristics --------------------------------------------------------------------------------------------

    private static string IdOf(Type t) => Prefix + (t.Assembly.GetName().Name ?? "?") + ":" + t.FullName;

    private static string DisplayNameOf(Type t, string moduleId)
    {
        var name = SplitCamel(t.Name);
        return NameSuffixes.Contains(t.Name, StringComparer.OrdinalIgnoreCase) || t.Name.Length <= 8 ? moduleId + " / " + name : name;
    }

    private static string SplitCamel(string s)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1])) sb.Append(' ');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static bool IsCandidate(Type t)
    {
        var full = t.FullName ?? t.Name;
        if (Include.Any(g => Glob(g, full))) return true;
        if (Exclude.Any(g => Glob(g, full))) return false;
        if (t.IsAbstract && !t.IsSealed) return false;                       // abstract (static classes are abstract+sealed)
        if (ExcludedNameParts.Any(p => t.Name.IndexOf(p, StringComparison.Ordinal) >= 0)) return false;
        var ns = t.Namespace ?? "";
        var segments = ns.Split('.');
        if (segments.Any(s => ExcludedNamespaceSegments.Contains(s, StringComparer.OrdinalIgnoreCase))) return false;
        if (ExcludedNamespaceTails.Contains(segments[segments.Length - 1], StringComparer.OrdinalIgnoreCase)) return false;
        for (var b = t.BaseType; b != null && b != typeof(object); b = b.BaseType)
        {
            if (ExcludedBases.Contains(b.Name)) return false;
            var asm = b.Assembly.GetName().Name ?? "";
            if (asm.StartsWith("MCM", StringComparison.OrdinalIgnoreCase)) return false;
        }
        try
        {
            if (t.GetInterfaces().Any(i => i.Assembly.GetName().Name?.StartsWith("MCM", StringComparison.OrdinalIgnoreCase) == true || i.Name == "ISettings")) return false;
            if (t.GetCustomAttributesData().Any(a => a.AttributeType.Name is "SaveableClassAttribute" or "SaveableRootClassAttribute")) return false;
        }
        catch { return false; }
        if (NameSuffixes.Any(s => t.Name.EndsWith(s, StringComparison.OrdinalIgnoreCase))) return true;
        if (SingletonOnlyExcludedSuffixes.Any(s => t.Name.EndsWith(s, StringComparison.Ordinal))) return false;
        return SingletonMember(t, t) != null;
    }

    /// <summary>A public static Instance/Current/... member of the wanted type on holder; returns a resolver or null.</summary>
    private static Func<object?>? SingletonMember(Type holder, Type wanted)
    {
        try
        {
            foreach (var name in SingletonNames)
            {
                var p = holder.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
                if (p != null && p.GetGetMethod() != null && wanted.IsAssignableFrom(p.PropertyType) && p.GetIndexParameters().Length == 0)
                    return () => p.GetValue(null);
                var f = holder.GetField(name, BindingFlags.Public | BindingFlags.Static);
                if (f != null && wanted.IsAssignableFrom(f.FieldType)) return () => f.GetValue(null);
            }
        }
        catch { }
        return null;
    }

    /// <summary>True when the singleton is a real getter (may construct the object); auto-properties and fields are read freely.</summary>
    private static bool SingletonIsDeferred(Type holder)
    {
        foreach (var name in SingletonNames)
        {
            var f = holder.GetField(name, BindingFlags.Public | BindingFlags.Static);
            if (f != null) return false;
            var p = holder.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            var get = p?.GetGetMethod();
            if (get == null) continue;
            return get.GetCustomAttribute<CompilerGeneratedAttribute>() == null;
        }
        return true;
    }

    private static IEnumerable<PropertyInfo> SafeProps(Type t)
    {
        try { return t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly | BindingFlags.FlattenHierarchy); }
        catch { return Array.Empty<PropertyInfo>(); }
    }

    private static List<ReflectionPropertyRef> MemberRefs(Type t, Func<object?>? target, bool wantStatic)
    {
        var list = new List<ReflectionPropertyRef>();
        try
        {
            var flags = BindingFlags.Public | (wantStatic ? BindingFlags.Static : BindingFlags.Instance);
            foreach (var p in t.GetProperties(flags))
            {
                if (p.GetIndexParameters().Length > 0 || p.GetGetMethod() == null || p.GetSetMethod() == null) continue;
                if (!ValueConverter.IsSupported(p.PropertyType)) continue;
                list.Add(new ReflectionPropertyRef(p, wantStatic ? null : target));
            }
            foreach (var f in t.GetFields(flags))
            {
                if (f.IsInitOnly || f.IsLiteral || !ValueConverter.IsSupported(f.FieldType)) continue;
                if (list.Any(r => r.Id == f.Name)) continue;
                list.Add(new ReflectionPropertyRef(f, wantStatic ? null : target));
            }
        }
        catch { }
        return list;
    }

    private static bool IsVersionLike(PropertyRef r) => r.ValueType == typeof(string) && r.Id.IndexOf("Version", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool CampaignExists()
    {
        try
        {
            var t = Type.GetType("TaleWorlds.CampaignSystem.Campaign, TaleWorlds.CampaignSystem", false);
            return t?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) != null;
        }
        catch { return false; }
    }

    private static bool Glob(string pattern, string value) =>
        pattern.EndsWith("*") ? value.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.Ordinal) : value.Equals(pattern, StringComparison.Ordinal);

    // ---- ISettingsSource ---------------------------------------------------------------------------------------

    private static bool Alive(StaticObject o)
    {
        if (o.IsStatic) return true;
        try { return o.Target?.Invoke() != null; } catch { return false; }
    }

    public List<SettingsSnapshot> Capture()
    {
        var result = new List<SettingsSnapshot>();
        foreach (var o in _objects.Values)
        {
            if (!Alive(o)) continue;
            result.Add(new SettingsSnapshot { SettingsId = o.Id, Payload = ValueConverter.Payload(o.Refs) });
        }
        return result;
    }

    public int Apply(string settingsId, IDictionary<string, string> values, out string report)
    {
        if (!_objects.TryGetValue(settingsId, out var o)) { report = "settings '" + settingsId + "' not installed on this side"; return 0; }
        if (!Alive(o)) { report = "settings object not created yet on this side"; return 0; }
        var n = ValueConverter.ApplyTo(o.Refs.Where(r => !IsVersionLike(r)), values, out report);
        if (n > 0) Log.Info($"static settings: {o.Type.Name}: {report}");
        return n;
    }

    /// <summary>Best-effort: a zero-arg public Save-like method on the object, then its holder, then holder statics.</summary>
    public string Save(string settingsId)
    {
        if (!_objects.TryGetValue(settingsId, out var o)) return "not persisted: unknown id";
        try
        {
            var attempts = new List<(object? target, Type type)>();
            var inst = o.IsStatic ? null : o.Target?.Invoke();
            if (inst != null) attempts.Add((inst, inst.GetType()));
            attempts.Add((null, o.Type));
            var holder = o.Holder?.Invoke();
            if (holder != null) attempts.Add((holder, holder.GetType()));
            if (o.HolderType != null) attempts.Add((null, o.HolderType));
            foreach (var (target, type) in attempts)
            {
                var flags = BindingFlags.Public | (target == null ? BindingFlags.Static : BindingFlags.Instance);
                var m = type.GetMethods(flags).Where(x => x.GetParameters().Length == 0 && x.ReturnType != typeof(Task) && IsSaveName(x.Name))
                    .OrderBy(x => x.Name == "Save" ? 0 : 1).ThenBy(x => x.Name.Length).FirstOrDefault();
                if (m == null) continue;
                m.Invoke(target, null);
                o.SaveVia = type.Name + "." + m.Name + "()";
                return "persisted via " + o.SaveVia;
            }
            return "not persisted (no Save-like method found; overrides re-applied at launch)";
        }
        catch (Exception ex) { return "not persisted: " + ex.GetBaseException().Message; }
    }

    private static bool IsSaveName(string n) =>
        SavePrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase))
        || ((n.StartsWith("Create", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Update", StringComparison.OrdinalIgnoreCase))
            && (n.IndexOf("Config", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Settings", StringComparison.OrdinalIgnoreCase) >= 0));

    public List<object?> Describe()
    {
        var result = new List<object?>();
        foreach (var o in _objects.Values)
        {
            if (!Alive(o)) continue;
            var props = new List<object?>();
            foreach (var r in o.Refs)
            {
                var hint = o.Type.Name + "." + r.Id + " (" + (r.ValueType?.Name ?? "?") + ")";
                props.Add(ValueConverter.Describe(r, SplitCamel(r.Id), hint, null, null, false, IsVersionLike(r) ? false : (bool?)null));
            }
            result.Add(new Dictionary<string, object?>
            {
                ["SettingsId"] = o.Id,
                ["DisplayName"] = o.DisplayName,
                ["Folder"] = o.ModuleId,
                ["Groups"] = new List<object?> { new Dictionary<string, object?> { ["Name"] = "General", ["Properties"] = props } },
            });
        }
        return result;
    }
}
