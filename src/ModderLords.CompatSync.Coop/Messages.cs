using System.Collections.Generic;
using Common.Messaging;
using ProtoBuf;

namespace ModderLords.CompatSync.Messages;

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

/// <summary>Client -> server: a player action the recipe relays — run this method as me, with these arguments.</summary>
[ProtoContract(SkipConstructor = true)]
public sealed class NetworkRelayInvoke : ICommand
{
    [ProtoMember(1)] public string Method { get; set; } = "";
    /// <summary>One per argument: b/i/l/f/d/s for primitives, or a game type's simple name (Town, Settlement, Hero, ...).</summary>
    [ProtoMember(2)] public List<string>? Kinds { get; set; }
    /// <summary>Invariant-culture value, or the game object's StringId (a Town travels as its settlement's).</summary>
    [ProtoMember(3)] public List<string>? Values { get; set; }
    [ProtoMember(4)] public int Sequence { get; set; }
    [ProtoMember(5)] public int ProtocolVersion { get; set; }
}

/// <summary>Server -> the sending client: whether a relayed action ran, and why not.</summary>
[ProtoContract(SkipConstructor = true)]
public sealed class NetworkRelayResult : IEvent
{
    [ProtoMember(1)] public string Method { get; set; } = "";
    [ProtoMember(2)] public int Sequence { get; set; }
    [ProtoMember(3)] public bool Ran { get; set; }
    [ProtoMember(4)] public string Reason { get; set; } = "";
    [ProtoMember(5)] public int ProtocolVersion { get; set; }
}

/// <summary>Client -> server (TAOM sessions): the joining player's TAOM character-creation choices, sent once.</summary>
[ProtoContract(SkipConstructor = true)]
public sealed class NetworkTaomJoinChoices : ICommand
{
    /// <summary>The character-creation hero's id on the client (the hero the join replaced).</summary>
    [ProtoMember(1)] public string HeroId { get; set; } = "";
    [ProtoMember(2)] public string CultureId { get; set; } = "";
    [ProtoMember(3)] public int RaceId { get; set; } = -1;
    [ProtoMember(4)] public string CareerId { get; set; } = "";
    [ProtoMember(5)] public int ProtocolVersion { get; set; }
}

/// <summary>Server -> the sending client: what happened to its TAOM join package.</summary>
[ProtoContract(SkipConstructor = true)]
public sealed class NetworkTaomJoinResult : IEvent
{
    [ProtoMember(1)] public bool Applied { get; set; }
    [ProtoMember(2)] public string Detail { get; set; } = "";
    [ProtoMember(3)] public int ProtocolVersion { get; set; }
    /// <summary>False when the server could not act yet (player or hero not known); the client sends again later.</summary>
    [ProtoMember(4)] public bool Final { get; set; }
}
