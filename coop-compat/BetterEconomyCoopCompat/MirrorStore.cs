using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;

namespace BetterEconomyCoopCompat;

/// <summary>
/// An IDataStore that records a behaviour's SyncData values as JSON (saving) or hands them back (loading). Only the
/// value types Living Economy's behaviours use are accepted; anything else throws, which switches that behaviour's
/// mirror off rather than sending something the client cannot rebuild.
/// </summary>
internal sealed class MirrorStore : IDataStore
{
    private static readonly HashSet<Type> Supported = new HashSet<Type>
    {
        typeof(int), typeof(long), typeof(float), typeof(double), typeof(bool), typeof(string),
        typeof(List<string>), typeof(List<int>),
        typeof(Dictionary<string, string>), typeof(Dictionary<string, int>), typeof(Dictionary<string, float>),
    };

    private readonly JObject _values;

    private MirrorStore(bool saving, JObject values)
    {
        IsSaving = saving;
        _values = values;
    }

    public static MirrorStore ForSaving() => new MirrorStore(true, new JObject());
    public static MirrorStore ForLoading(string json) => new MirrorStore(false, JObject.Parse(json));

    public bool IsSaving { get; }
    public bool IsLoading => !IsSaving;

    public string Json => _values.ToString(Formatting.None);

    public bool SyncData<T>(string key, ref T data)
    {
        if (!Supported.Contains(typeof(T)) && !Records.IsRecordBook(typeof(T)) && !Records.IsRecordList(typeof(T)))
            throw new NotSupportedException($"key '{key}' has type {typeof(T).Name}, which the mirror does not carry");
        var serializer = Records.Serializer;
        if (IsSaving)
        {
            _values[key] = data == null ? JValue.CreateNull() : JToken.FromObject(data, serializer);
            return true;
        }
        // A key the server did not send is left alone, exactly as the engine leaves it for a key absent from a save.
        if (!_values.TryGetValue(key, out var token)) return false;
        data = token.Type == JTokenType.Null ? default! : token.ToObject<T>(serializer)!;
        return true;
    }
}

/// <summary>
/// Living Economy's own saved record classes: plain classes of public fields, kept in a Dictionary&lt;string, record&gt;
/// book or a list. They travel as their public fields only (their properties are views over
/// the same fields), with CampaignTime carried as its tick count.
/// </summary>
internal static class Records
{
    /// <summary>Dictionary&lt;string, X&gt; where X is a record class: a book the mirror can carry.</summary>
    internal static bool IsRecordBook(Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>)
        && t.GetGenericArguments()[0] == typeof(string) && IsRecord(t.GetGenericArguments()[1]);

    /// <summary>List&lt;X&gt; where X is a record (Living Economy keeps its trade log this way).</summary>
    internal static bool IsRecordList(Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) && IsRecord(t.GetGenericArguments()[0]);

    /// <summary>The namespace whose plain classes are Living Economy's saved records.</summary>
    private static readonly string[] RecordNamespaces = { "BetterEconomy.Core" };

    internal static bool IsRecord(Type t) =>
        t.IsClass && t.Namespace != null && RecordNamespaces.Any(ns => t.Namespace.StartsWith(ns, StringComparison.Ordinal))
        && t.GetConstructor(Type.EmptyTypes) != null;

    internal static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        ContractResolver = new FieldsOnlyResolver(),
        Converters = { new CampaignTimeConverter() },
    });

    private sealed class FieldsOnlyResolver : Newtonsoft.Json.Serialization.DefaultContractResolver
    {
        protected override List<MemberInfo> GetSerializableMembers(Type objectType) =>
            IsRecord(objectType)
                ? objectType.GetFields(BindingFlags.Public | BindingFlags.Instance).Cast<MemberInfo>().ToList()
                : base.GetSerializableMembers(objectType);
    }

    /// <summary>CampaignTime is a struct over one private tick count; its public members are all derived views.</summary>
    internal sealed class CampaignTimeConverter : JsonConverter
    {
        private static readonly FieldInfo? Ticks = typeof(CampaignTime)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(f => f.FieldType == typeof(long));

        public override bool CanConvert(Type objectType) => objectType == typeof(CampaignTime);

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) =>
            writer.WriteValue(value == null || Ticks == null ? 0L : (long)Ticks.GetValue(value));

        public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            object boxed = default(CampaignTime);
            if (Ticks != null && reader.Value != null) Ticks.SetValue(boxed, Convert.ToInt64(reader.Value));
            return boxed;
        }
    }
}
