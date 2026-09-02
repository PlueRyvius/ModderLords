using Common.Messaging;
using ProtoBuf;

namespace ModularCoop.CompatSync.Messages;

// WIRE-FORMAT NOTE: Coop derives each message's wire id from a hash of the type's full name, so these names are part
// of the protocol between server and clients. Never rename or move them; add new members with new tags.

/// <summary>Client -> server: send me every settings snapshot you hold (sent when the campaign is ready).</summary>
[ProtoContract(SkipConstructor = true)]
public sealed class NetworkRequestSettingsSnapshots : ICommand
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; }
}

/// <summary>Client -> server: send me the behaviour recipes (sent as soon as the client handler exists, before the campaign loads).</summary>
[ProtoContract(SkipConstructor = true)]
public sealed class NetworkRequestCompatRecipes : ICommand
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; }
}

/// <summary>Server -> client: the recipes.json text the host launched with (which behaviours are server-only).</summary>
[ProtoContract(SkipConstructor = true)]
public sealed class NetworkCompatRecipes : IEvent
{
    [ProtoMember(1)] public string Json { get; set; } = "";
    [ProtoMember(2)] public int ProtocolVersion { get; set; }
}

/// <summary>Server -> client(s): the authoritative values of one settings object (MCM settings id + "prop=value" pairs).</summary>
[ProtoContract(SkipConstructor = true)]
public sealed class NetworkSettingsSnapshot : IEvent
{
    [ProtoMember(1)] public string SettingsId { get; set; } = "";
    /// <summary>Lines of <c>propertyId\tvalue</c>, invariant culture; only primitive, string and enum properties are carried.</summary>
    [ProtoMember(2)] public string Payload { get; set; } = "";
    [ProtoMember(3)] public int ProtocolVersion { get; set; }
}
