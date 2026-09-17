using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ModderLords.Operations;

/// <summary>Shared launcher/runtime encoding. Integrity is not authorization: only local compiled contracts can install.</summary>
public static class PlanIntegrity
{
    public static JObject Parse(string json)
    {
        if (json == null || Encoding.UTF8.GetByteCount(json) > 1024 * 1024) throw new InvalidDataException("Plan exceeds size limit");
        using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, MaxDepth = 64 })
        {
            var plan = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new InvalidDataException("Trailing plan content");
            return plan;
        }
    }
    public static string Compute(JObject plan)
    {
        var payload = (JObject)plan.DeepClone();
        payload.Remove("Digest");
        // Derived UI convenience; runtime derives activation from Contracts instead.
        payload.Remove("RequiresRuntime");
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) Write(writer, payload);
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "");
        }
    }
    public static bool Verify(JObject plan, out string reason)
    {
        reason = "Plan content does not match its digest";
        var digest = plan["Digest"]?.Type == JTokenType.String ? (string?)plan["Digest"] : null;
        if (!IsHash(digest) || digest != Compute(plan)) return false;
        if (!IsHash((string?)plan["ContextDigest"]) || plan["Contracts"] is not JArray || plan["Fingerprints"] is not JArray)
        { reason = "Plan envelope is incomplete"; return false; }
        reason = ""; return true;
    }
    public static bool IsHash(string? value) => value != null && value.Length == 64 && value.All(c => c >= '0' && c <= '9' || c >= 'A' && c <= 'F');
    private static void Write(BinaryWriter writer, JToken token)
    {
        writer.Write((byte)token.Type);
        if (token is JObject obj)
        {
            var properties = obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
            writer.Write(properties.Length);
            foreach (var property in properties) { writer.Write(property.Name); Write(writer, property.Value); }
        }
        else if (token is JArray array)
        {
            writer.Write(array.Count);
            foreach (var item in array) Write(writer, item);
        }
        else if (token.Type == JTokenType.String) writer.Write((string)token!);
        else if (token.Type == JTokenType.Boolean) writer.Write((bool)token);
        else if (token.Type == JTokenType.Integer) writer.Write(Convert.ToString(((JValue)token).Value, CultureInfo.InvariantCulture) ?? "");
        else if (token.Type != JTokenType.Null) throw new InvalidDataException("Unsupported plan value: " + token.Type);
    }
}
