using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ModderLords.Operations;

/// <summary>Versioned, ID-only request envelope for Bellum player actions.</summary>
public sealed class BellumCommand
{
    public const int CurrentSchema = 1;
    public int SchemaVersion { get; set; } = CurrentSchema;
    public string Kind { get; set; } = "";
    public long ExpectedRevision { get; set; }
    public Dictionary<string, string> Arguments { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

public static class BellumCommandCodec
{
    public const int MaxBytes = 16 * 1024;
    private const int MaxArguments = 12;
    private const int MaxKeyLength = 64;
    private const int MaxValueLength = 512;

    public static string Serialize(BellumCommand command)
    {
        Validate(command);
        return JsonConvert.SerializeObject(command, Formatting.None);
    }

    public static BellumCommand Deserialize(string json)
    {
        if (json == null || Encoding.UTF8.GetByteCount(json) > MaxBytes)
            throw new JsonException("Bellum command exceeds its size limit");
        var token = JToken.Parse(json);
        if (token is not JObject obj || obj.Properties().Any(p => p.Name != nameof(BellumCommand.SchemaVersion)
            && p.Name != nameof(BellumCommand.Kind) && p.Name != nameof(BellumCommand.ExpectedRevision)
            && p.Name != nameof(BellumCommand.Arguments)))
            throw new JsonException("Bellum command contains unknown fields");
        var command = obj.ToObject<BellumCommand>() ?? throw new JsonException("Bellum command is empty");
        Validate(command);
        return command;
    }

    public static void Validate(BellumCommand command)
    {
        if (command == null) throw new JsonException("Bellum command is missing");
        if (command.SchemaVersion != BellumCommand.CurrentSchema) throw new JsonException("Unsupported Bellum command schema");
        if (string.IsNullOrWhiteSpace(command.Kind) || command.Kind.Length > MaxKeyLength)
            throw new JsonException("Invalid Bellum command kind");
        if (command.ExpectedRevision < 0) throw new JsonException("Invalid Bellum command revision");
        if (command.Arguments == null || command.Arguments.Count > MaxArguments)
            throw new JsonException("Invalid Bellum command arguments");
        foreach (var pair in command.Arguments)
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > MaxKeyLength || pair.Value == null || pair.Value.Length > MaxValueLength)
                throw new JsonException("Invalid Bellum command argument");
    }
}

/// <summary>An expected domain refusal that is safe to return to the requesting player.</summary>
public sealed class OperationRejectedException : Exception
{
    public OperationRejectedException(string message) : base(message) { }
}
