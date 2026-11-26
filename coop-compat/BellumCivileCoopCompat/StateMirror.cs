using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.ObjectSystem;
using TaleWorlds.SaveSystem;

namespace BellumCivileCoopCompat
{
    // Player agency, state (docs/BELLUM-COOP-COVERAGE.md, stage 3b): Bellum's screens on a player's PC show the server's
    // Bellum.
    //
    // Coop never sends a mod's own state, and stage 1 stopped Bellum's ticks on players' PCs, so their Factions,
    // Hierarchy and title screens showed what they had when they joined. Here the server runs a Bellum behaviour's own
    // SyncData into a recording data store, as the game does when it saves, and sends the result to players; their PC
    // runs the same SyncData from it, as the game does when it loads. The behaviour's own loading code then rebuilds what
    // it rebuilds after a load. Game objects (heroes, clans, kingdoms, settlements) travel as their ids; Bellum's own
    // records travel as the fields the game's save system would write ([SaveableField]).
    //
    // Sent to every connected player each game day, and to one player when they ask (on joining, or opening a screen).
    public static class StateMirror
    {
        /// <summary>The behaviours whose state players' screens read.</summary>
        public static readonly string[] Behaviours =
        {
            "BellumCivile.Behaviors.FactionManagerBehavior",
            "BellumCivile.Behaviors.ClaimFeudBehavior",
            "BellumCivile.Behaviors.FeudalTitleBehavior",
            "BellumCivile.Behaviors.FeudalClaimFabricationBehavior",
            "BellumCivile.Behaviors.LordshipBehavior",
            "BellumCivile.Behaviors.RealmLawBehavior",
            "BellumCivile.Behaviors.ElectiveSuccessionBehavior",
            "BellumCivile.Behaviors.PrivyCouncilBehavior",
            "BellumCivile.Behaviors.SuccessionChallengeBehavior",
            "BellumCivile.Behaviors.CourtAgendaBehavior",
            "BellumCivile.Behaviors.CouncilAppointmentDeliberationBehavior",
            "BellumCivile.Behaviors.ClientKingdomBehavior",
            "BellumCivile.Behaviors.FiefDeliberationBehavior",
            "BellumCivile.Behaviors.PolicyDeliberationBehavior",
            "BellumCivile.Behaviors.ExpulsionDeliberationBehavior",
            "BellumCivile.Behaviors.HostagePactBehavior",
            "BellumCivile.Behaviors.ExiledClanRecoveryBehavior",
        };

        private const int Chunk = 24 * 1024;

        private static readonly Dictionary<string, string[]> Assembling = new Dictionary<string, string[]>();
        private static int _revision, _sent, _applied;
        private static double _lastDay = -1;

        /// <summary>Per behaviour: the revision last applied on this PC (player) or sent (server), for tests.</summary>
        public static readonly Dictionary<string, int> Applied = new Dictionary<string, int>();

        public static string Summary() => _sent > 0 ? "; state sent to players " + _sent : _applied > 0 ? "; state from the server applied " + _applied : "";

        public static void Install(HarmonyLib.Harmony harmony)
        {
            Agency.Handle("state", (sender, args) => { SubModule.Log("State: " + sender.StringId + " asked for Bellum's state."); SendAll(sender); });
            Agency.Listen("state", OnChunk);
            var found = Behaviours.Count(b => AccessTools.TypeByName(b) != null);
            SubModule.Log("State: Bellum's screens on a player's PC show the server's state (" + found + " of " + Behaviours.Length + " behaviours).");
        }

        /// <summary>Player's PC: asks the server for its Bellum state now.</summary>
        public static bool Request() => Agency.ToServer("state");

        /// <summary>Server, every few seconds: once a game day, every connected player gets the state.</summary>
        public static void Tick()
        {
            if (!Coop.OnServer || Agency.Channel == null || Campaign.Current == null)
                return;
            var day = Math.Floor(CampaignTime.Now.ToDays);
            if (day == _lastDay)
                return;
            _lastDay = day;
            foreach (var hero in Hero.AllAliveHeroes.Where(h => Coop.IsConnectedPlayer(h)).ToList())
                SendAll(hero);
        }

        /// <summary>Server: sends one player the state now (after an action they asked for, say).</summary>
        public static void SendTo(Hero hero) => SendAll(hero);

        private static void SendAll(Hero hero)
        {
            foreach (var name in Behaviours)
            {
                var behaviour = Behaviour(name);
                if (behaviour == null) continue;
                byte[] bytes;
                try { bytes = Encode(behaviour); }
                catch (Exception ex)
                {
                    SubModule.Log("State: could not read " + Short(name) + " for players: " + ex.GetBaseException().Message);
                    continue;
                }
                var text = Convert.ToBase64String(bytes);
                var count = (text.Length + Chunk - 1) / Chunk;
                var revision = (++_revision).ToString();
                var delivered = 0;
                for (var i = 0; i < count; i++)
                    if (Agency.ToPlayer(hero, "state", name, revision, i.ToString(), count.ToString(), text.Substring(i * Chunk, Math.Min(Chunk, text.Length - i * Chunk))))
                        delivered++;
                if (_sent < 12)
                    SubModule.Log("State: sent " + Short(name) + " to " + hero.StringId + " (" + bytes.Length + " bytes, " + delivered + " of " + count + " parts).");
                _sent++;
                lock (Applied) Applied[name] = _revision;
            }
        }

        private static void OnChunk(string[] args)
        {
            if (args.Length < 5 || !int.TryParse(args[2], out var index) || !int.TryParse(args[3], out var count) || count <= 0 || index >= count)
                return;
            var key = args[0] + "#" + args[1];
            if (!Assembling.TryGetValue(key, out var parts) || parts.Length != count)
                Assembling[key] = parts = new string[count];
            parts[index] = args[4];
            if (parts.Any(p => p == null))
                return;
            Assembling.Remove(key);
            foreach (var stale in Assembling.Keys.Where(k => k.StartsWith(args[0] + "#")).ToList())
                Assembling.Remove(stale);
            var behaviour = Behaviour(args[0]);
            if (behaviour == null) return;
            try
            {
                Decode(behaviour, Convert.FromBase64String(string.Concat(parts)));
                if (_applied < 12) SubModule.Log("State: applied the server's " + Short(args[0]) + " (revision " + args[1] + ").");
                _applied++;
                lock (Applied) Applied[args[0]] = int.Parse(args[1]);
            }
            catch (Exception ex) { SubModule.Log("State: could not apply the server's " + Short(args[0]) + ": " + ex.GetBaseException().Message); }
        }

        private static CampaignBehaviorBase Behaviour(string name)
        {
            var type = AccessTools.TypeByName(name);
            if (type == null || Campaign.Current == null) return null;
            return typeof(Campaign).GetMethod(nameof(Campaign.GetCampaignBehavior))?.MakeGenericMethod(type).Invoke(Campaign.Current, null) as CampaignBehaviorBase;
        }

        private static string Short(string name) => name.Substring(name.LastIndexOf('.') + 1);

        // ---- a behaviour's SyncData, recorded and replayed ----

        public static byte[] Encode(CampaignBehaviorBase behaviour)
        {
            var store = new Store(saving: true);
            behaviour.SyncData(store);
            using (var memory = new MemoryStream())
            {
                using (var zip = new GZipStream(memory, CompressionLevel.Fastest, true))
                using (var writer = new BinaryWriter(zip))
                {
                    var codec = new Codec();
                    writer.Write(store.Values.Count);
                    foreach (var kv in store.Values)
                    {
                        writer.Write(kv.Key);
                        codec.Write(writer, kv.Value);
                    }
                }
                return memory.ToArray();
            }
        }

        /// <summary>One object (a kingdom decision, say) in the same encoding: its saveable fields, game objects by id.</summary>
        public static byte[] EncodeObject(object value)
        {
            using (var memory = new MemoryStream())
            {
                using (var writer = new BinaryWriter(memory))
                    new Codec().Write(writer, value);
                return memory.ToArray();
            }
        }

        /// <summary>
        /// Reads an object from <see cref="EncodeObject"/>. Objects are built with their own parameterless constructor where
        /// they have one, so fields the encoding doesn't carry keep their usual start values.
        /// </summary>
        public static object DecodeObject(byte[] bytes)
        {
            using (var memory = new MemoryStream(bytes ?? new byte[0]))
            using (var reader = new BinaryReader(memory))
                return memory.Length == 0 ? null : new Codec { UseConstructors = true }.Read(reader);
        }

        public static void Decode(CampaignBehaviorBase behaviour, byte[] bytes)
        {
            var store = new Store(saving: false);
            using (var memory = new MemoryStream(bytes))
            using (var zip = new GZipStream(memory, CompressionMode.Decompress))
            using (var reader = new BinaryReader(zip))
            {
                var codec = new Codec();
                var n = reader.ReadInt32();
                for (var i = 0; i < n; i++)
                {
                    var key = reader.ReadString();
                    store.Values[key] = codec.Read(reader);
                }
            }
            behaviour.SyncData(store);
            // Bellum keeps lookup caches and indexes over this state (factions by clan, titles by holder, estates by hero); drop or
            // rebuild them as its own changes and loading do.
            foreach (var method in behaviour.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                         .Where(m => m.GetParameters().Length == 0 && m.ReturnType == typeof(void)
                                     && (m.Name.StartsWith("Invalidate") && m.Name.EndsWith("Cache") || m.Name == "Reindex"
                                         || m.Name.StartsWith("Rebuild") && m.Name.Contains("Index"))))
            {
                try { method.Invoke(behaviour, null); }
                catch { }
            }
        }

        private sealed class Store : IDataStore
        {
            public Store(bool saving) { IsSaving = saving; }
            public readonly Dictionary<string, object> Values = new Dictionary<string, object>();
            public bool IsSaving { get; }
            public bool IsLoading => !IsSaving;

            public bool SyncData<T>(string key, ref T data)
            {
                if (IsSaving)
                {
                    Values[key] = data;
                    return true;
                }
                if (!Values.TryGetValue(key, out var value))
                    return false;   // a key the server did not send is left alone, as a load leaves a key missing from a save
                data = value == null ? default : (T)value;
                return true;
            }
        }

        // ---- the encoding: the game's saveable fields, game objects by id, shared objects once ----

        private sealed class Codec
        {
            private enum Tag : byte { Null, Bool, Int, Long, Float, Double, String, Enum, GameObject, Ref, List, Dict, Set, Array, Object, Struct, Byte, Short, UInt, ULong, PlayerClanId, PlayerHeroId }

            // A player's clan and hero ids differ between PCs, and Bellum keeps many as plain strings (a title's holder, a
            // feud's sides): on the server those strings go as the player's name, and a PC reads its own id for that name.
            private Dictionary<string, string> _playerClans, _playerHeroes;

            private void PlayerIds()
            {
                if (_playerClans != null) return;
                _playerClans = new Dictionary<string, string>();
                _playerHeroes = new Dictionary<string, string>();
                if (!Coop.OnServer) return;
                foreach (var hero in Hero.AllAliveHeroes.Where(Coop.IsPlayerHero))
                {
                    var name = hero.Name?.ToString() ?? "";
                    if (name.Length == 0) continue;
                    _playerHeroes[hero.StringId] = name;
                    if (hero.Clan != null && hero.Clan.Leader == hero) _playerClans[hero.Clan.StringId] = name;
                }
            }

            /// <summary>Build objects with their parameterless constructor where they have one (else uninitialized).</summary>
            public bool UseConstructors;

            private readonly Dictionary<object, int> _written = new Dictionary<object, int>(ReferenceComparer.Instance);
            private readonly List<object> _read = new List<object>();
            private static readonly Dictionary<Type, FieldInfo[]> Fields = new Dictionary<Type, FieldInfo[]>();
            private static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>();

            public void Write(BinaryWriter w, object value)
            {
                switch (value)
                {
                    case null: w.Write((byte)Tag.Null); return;
                    case bool b: w.Write((byte)Tag.Bool); w.Write(b); return;
                    case int i: w.Write((byte)Tag.Int); w.Write(i); return;
                    case long l: w.Write((byte)Tag.Long); w.Write(l); return;
                    case float f: w.Write((byte)Tag.Float); w.Write(f); return;
                    case double d: w.Write((byte)Tag.Double); w.Write(d); return;
                    case string s:
                        PlayerIds();
                        if (_playerClans.TryGetValue(s, out var clanOf)) { w.Write((byte)Tag.PlayerClanId); w.Write(s); w.Write(clanOf); return; }
                        if (_playerHeroes.TryGetValue(s, out var heroOf)) { w.Write((byte)Tag.PlayerHeroId); w.Write(s); w.Write(heroOf); return; }
                        w.Write((byte)Tag.String); w.Write(s); return;
                    case byte by: w.Write((byte)Tag.Byte); w.Write(by); return;
                    case short sh: w.Write((byte)Tag.Short); w.Write(sh); return;
                    case uint ui: w.Write((byte)Tag.UInt); w.Write(ui); return;
                    case ulong ul: w.Write((byte)Tag.ULong); w.Write(ul); return;
                }
                var type = value.GetType();
                if (type.IsEnum)
                {
                    w.Write((byte)Tag.Enum); WriteType(w, type); w.Write(Convert.ToInt64(value)); return;
                }
                if (value is MBObjectBase game)
                {
                    w.Write((byte)Tag.GameObject); WriteType(w, type); w.Write(game.StringId ?? "");
                    w.Write(game is Hero h ? h.Name?.ToString() ?? "" : game is Clan c ? c.Leader?.Name?.ToString() ?? "" : "");
                    return;
                }
                if (!type.IsValueType)
                {
                    if (_written.TryGetValue(value, out var id)) { w.Write((byte)Tag.Ref); w.Write(id); return; }
                    _written[value] = _written.Count;
                }
                if (type.IsArray)
                {
                    var array = (Array)value;
                    w.Write((byte)Tag.Array); WriteType(w, type.GetElementType()); w.Write(array.Length);
                    foreach (var item in array) Write(w, item);
                    return;
                }
                if (value is IDictionary dict)
                {
                    w.Write((byte)Tag.Dict); WriteType(w, type); w.Write(dict.Count);
                    foreach (DictionaryEntry e in dict) { Write(w, e.Key); Write(w, e.Value); }
                    return;
                }
                if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(List<>)))
                {
                    var list = (IList)value;
                    w.Write((byte)Tag.List); WriteType(w, type); w.Write(list.Count);
                    foreach (var item in list) Write(w, item);
                    return;
                }
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(HashSet<>))
                {
                    var items = ((IEnumerable)value).Cast<object>().ToList();
                    w.Write((byte)Tag.Set); WriteType(w, type); w.Write(items.Count);
                    foreach (var item in items) Write(w, item);
                    return;
                }
                if (typeof(Delegate).IsAssignableFrom(type))
                {
                    w.Write((byte)Tag.Null); return;
                }
                w.Write((byte)(type.IsValueType ? Tag.Struct : Tag.Object)); WriteType(w, type);
                var fields = FieldsOf(type);
                w.Write(fields.Length);
                foreach (var field in fields)
                {
                    w.Write(field.Name);
                    Write(w, field.GetValue(value));
                }
            }

            public object Read(BinaryReader r)
            {
                var tag = (Tag)r.ReadByte();
                switch (tag)
                {
                    case Tag.Null: return null;
                    case Tag.Bool: return r.ReadBoolean();
                    case Tag.Int: return r.ReadInt32();
                    case Tag.Long: return r.ReadInt64();
                    case Tag.Float: return r.ReadSingle();
                    case Tag.Double: return r.ReadDouble();
                    case Tag.String: return r.ReadString();
                    case Tag.PlayerClanId:
                    {
                        var id = r.ReadString(); var name = r.ReadString();
                        return (Clan.All.FirstOrDefault(c => c?.StringId == id && c.Leader?.Name?.ToString() == name)
                                ?? Clan.All.FirstOrDefault(c => c?.Leader?.Name?.ToString() == name))?.StringId ?? id;
                    }
                    case Tag.PlayerHeroId:
                    {
                        var id = r.ReadString(); var name = r.ReadString();
                        return (Hero.FindFirst(h => h.StringId == id && h.Name?.ToString() == name)
                                ?? Hero.AllAliveHeroes.FirstOrDefault(h => h.Name?.ToString() == name))?.StringId ?? id;
                    }
                    case Tag.Byte: return r.ReadByte();
                    case Tag.Short: return r.ReadInt16();
                    case Tag.UInt: return r.ReadUInt32();
                    case Tag.ULong: return r.ReadUInt64();
                    case Tag.Enum: { var t = ReadType(r); return Enum.ToObject(t, r.ReadInt64()); }
                    case Tag.GameObject: { var t = ReadType(r); var id = r.ReadString(); var name = r.ReadString(); return Find(t, id, name); }
                    case Tag.Ref: return _read[r.ReadInt32()];
                    case Tag.Array:
                    {
                        var element = ReadType(r); var n = r.ReadInt32();
                        var array = Array.CreateInstance(element, n);
                        _read.Add(array);
                        for (var i = 0; i < n; i++) array.SetValue(Fit(Read(r), element), i);
                        return array;
                    }
                    case Tag.Dict:
                    {
                        var t = ReadType(r); var n = r.ReadInt32();
                        var dict = (IDictionary)Activator.CreateInstance(t);
                        _read.Add(dict);
                        var args = t.GetGenericArguments();
                        for (var i = 0; i < n; i++)
                        {
                            var k = Read(r); var v = Read(r);
                            if (k != null) dict[k] = Fit(v, args.Length == 2 ? args[1] : typeof(object));
                        }
                        return dict;
                    }
                    case Tag.List:
                    {
                        var t = ReadType(r); var n = r.ReadInt32();
                        var list = (IList)Activator.CreateInstance(t);
                        _read.Add(list);
                        var element = t.GetGenericArguments()[0];
                        for (var i = 0; i < n; i++) list.Add(Fit(Read(r), element));
                        return list;
                    }
                    case Tag.Set:
                    {
                        var t = ReadType(r); var n = r.ReadInt32();
                        var set = Activator.CreateInstance(t);
                        _read.Add(set);
                        var add = t.GetMethod("Add");
                        var element = t.GetGenericArguments()[0];
                        for (var i = 0; i < n; i++) { var v = Fit(Read(r), element); if (v != null || !element.IsValueType) add.Invoke(set, new[] { v }); }
                        return set;
                    }
                    case Tag.Object:
                    case Tag.Struct:
                    {
                        var t = ReadType(r);
                        object value = null;
                        if (UseConstructors && tag == Tag.Object && t.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null) != null)
                        {
                            try { value = Activator.CreateInstance(t, true); }
                            catch { value = null; }
                        }
                        if (value == null) value = FormatterServices.GetUninitializedObject(t);
                        if (tag == Tag.Object) _read.Add(value);
                        var n = r.ReadInt32();
                        var fields = FieldsOf(t).ToDictionary(f => f.Name);
                        for (var i = 0; i < n; i++)
                        {
                            var name = r.ReadString();
                            var v = Read(r);
                            if (fields.TryGetValue(name, out var field))
                                field.SetValue(value, Fit(v, field.FieldType));
                        }
                        return value;
                    }
                    default: throw new InvalidDataException("unknown tag " + tag);
                }
            }

            private static object Fit(object value, Type type) =>
                value == null && type.IsValueType && Nullable.GetUnderlyingType(type) == null ? Activator.CreateInstance(type) : value;

            // The game's save system writes [SaveableField]/[SaveableProperty] members; a type without them is a plain
            // record, and travels as all its instance fields (delegates and events aside).
            private static FieldInfo[] FieldsOf(Type type)
            {
                lock (Fields)
                {
                    if (Fields.TryGetValue(type, out var known)) return known;
                    var all = new List<FieldInfo>();
                    for (var t = type; t != null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType)
                        all.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
                    all = all.Where(f => !typeof(Delegate).IsAssignableFrom(f.FieldType) && !f.FieldType.IsPointer && f.FieldType != typeof(IntPtr)).ToList();
                    var saveable = all.Where(f => f.IsDefined(typeof(SaveableFieldAttribute), true) || IsBackingOfSaveableProperty(f)).ToArray();
                    var result = saveable.Length > 0 ? saveable : all.ToArray();
                    Fields[type] = result;
                    return result;
                }
            }

            private static bool IsBackingOfSaveableProperty(FieldInfo field)
            {
                if (!field.Name.StartsWith("<") || !field.Name.Contains(">k__BackingField")) return false;
                var name = field.Name.Substring(1, field.Name.IndexOf('>') - 1);
                return field.DeclaringType?.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    ?.IsDefined(typeof(SaveablePropertyAttribute), true) == true;
            }

            // Types travel by name without assembly versions (the dedicated server's game assemblies are other builds than a
            // player's): a generic type as its definition and arguments, an array as its element.
            private static void WriteType(BinaryWriter w, Type type)
            {
                if (type.IsArray)
                {
                    w.Write((byte)2); WriteType(w, type.GetElementType()); return;
                }
                if (type.IsGenericType && !type.IsGenericTypeDefinition)
                {
                    w.Write((byte)1); w.Write(type.GetGenericTypeDefinition().FullName ?? "");
                    var args = type.GetGenericArguments();
                    w.Write(args.Length);
                    foreach (var arg in args) WriteType(w, arg);
                    return;
                }
                w.Write((byte)0); w.Write(type.FullName ?? "");
            }

            private static Type ReadType(BinaryReader r)
            {
                switch (r.ReadByte())
                {
                    case 2: return ReadType(r).MakeArrayType();
                    case 1:
                    {
                        var definition = Named(r.ReadString());
                        var args = new Type[r.ReadInt32()];
                        for (var i = 0; i < args.Length; i++) args[i] = ReadType(r);
                        return definition.MakeGenericType(args);
                    }
                    default: return Named(r.ReadString());
                }
            }

            private static Type Named(string name)
            {
                lock (Types)
                {
                    if (Types.TryGetValue(name, out var known)) return known;
                    var type = Type.GetType(name, false) ?? AccessTools.TypeByName(name) ?? throw new InvalidDataException("type not found here: " + name);
                    Types[name] = type;
                    return type;
                }
            }

            // A game object by its id; a player's hero (and clan) by name, since a player's ids differ between PCs.
            private static object Find(Type type, string id, string name)
            {
                object found = null;
                if (typeof(Kingdom).IsAssignableFrom(type)) found = Kingdom.All.FirstOrDefault(k => k?.StringId == id);
                else if (typeof(Clan).IsAssignableFrom(type)) found = Clan.All.FirstOrDefault(c => c?.StringId == id);
                else if (typeof(Hero).IsAssignableFrom(type)) found = Hero.FindFirst(h => h.StringId == id);
                else if (typeof(TaleWorlds.CampaignSystem.Settlements.Settlement).IsAssignableFrom(type))
                    found = TaleWorlds.CampaignSystem.Settlements.Settlement.Find(id);
                // A party (an exile's at an asylum audience) is not in the object manager's lists.
                else if (typeof(TaleWorlds.CampaignSystem.Party.MobileParty).IsAssignableFrom(type))
                    found = TaleWorlds.CampaignSystem.Party.MobileParty.All.FirstOrDefault(p => p?.StringId == id);
                else
                {
                    try
                    {
                        var getObject = typeof(MBObjectManager).GetMethods().FirstOrDefault(m => m.Name == "GetObject" && m.IsGenericMethodDefinition
                            && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
                        found = getObject?.MakeGenericMethod(type).Invoke(MBObjectManager.Instance, new object[] { id });
                    }
                    catch { }
                }
                if (found != null || name.Length == 0) return found;
                if (typeof(Hero).IsAssignableFrom(type))
                    return Hero.AllAliveHeroes.Concat(Hero.DeadOrDisabledHeroes).FirstOrDefault(h => h.Name?.ToString() == name);
                if (typeof(Clan).IsAssignableFrom(type))
                    return Clan.All.FirstOrDefault(c => c.Leader?.Name?.ToString() == name);
                return null;
            }

            private sealed class ReferenceComparer : IEqualityComparer<object>
            {
                public static readonly ReferenceComparer Instance = new ReferenceComparer();
                public new bool Equals(object x, object y) => ReferenceEquals(x, y);
                public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
