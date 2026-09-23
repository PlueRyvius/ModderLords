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

/// <summary>Client -> server (TAOM sessions): a Field Camp operation the player just made from TAOM's camp menu.</summary>
[ProtoContract(SkipConstructor = true)]
public sealed class NetworkTaomCampOp : ICommand
{
    /// <summary>establish, fortify, foraging or break.</summary>
    [ProtoMember(1)] public string Op { get; set; } = "";
    /// <summary>TAOM's CampType value, for establish.</summary>
    [ProtoMember(2)] public int CampType { get; set; }
    [ProtoMember(3)] public int ProtocolVersion { get; set; }
}

/// <summary>Server -> the sending client: whether TAOM ran the camp operation for that player.</summary>
[ProtoContract(SkipConstructor = true)]
public sealed class NetworkTaomCampResult : IEvent
{
    [ProtoMember(1)] public string Op { get; set; } = "";
    [ProtoMember(2)] public bool Ran { get; set; }
    [ProtoMember(3)] public string Detail { get; set; } = "";
    [ProtoMember(4)] public int ProtocolVersion { get; set; }
}

/// <summary>
/// Client -> server (TAOM sessions): a TAOM action the player took that TAOM itself only lets the host perform
/// (elite emissary purchase, messenger, ...). Feature + Op pick the server-side handler; Args are plain strings.
/// </summary>
[ProtoContract(SkipConstructor = true)]
public sealed class NetworkTaomAction : ICommand
{
    [ProtoMember(1)] public string Feature { get; set; } = "";
    [ProtoMember(2)] public string Op { get; set; } = "";
    [ProtoMember(3)] public List<string>? Args { get; set; }
    [ProtoMember(4)] public int Sequence { get; set; }
    [ProtoMember(5)] public int ProtocolVersion { get; set; }
}

/// <summary>Server -> the sending client: the outcome of a TAOM action, with the line to show the player.</summary>
[ProtoContract(SkipConstructor = true)]
public sealed class NetworkTaomActionResult : IEvent
{
    [ProtoMember(1)] public string Feature { get; set; } = "";
    [ProtoMember(2)] public string Op { get; set; } = "";
    [ProtoMember(3)] public int Sequence { get; set; }
    [ProtoMember(4)] public bool Ok { get; set; }
    /// <summary>Shown to the player as an on-screen message (already resolved on the server).</summary>
    [ProtoMember(5)] public string Message { get; set; } = "";
    [ProtoMember(6)] public int ProtocolVersion { get; set; }
    /// <summary>Feature data the client applies (e.g. the player's special-resource balances after a purchase).</summary>
    [ProtoMember(7)] public List<string>? Data { get; set; }
}

/// <summary>Server -> client(s) (TAOM sessions): one TAOM behaviour's SyncData values, for the client to load.</summary>
[ProtoContract(SkipConstructor = true)]
public sealed class NetworkTaomState : IEvent
{
    /// <summary>The TAOM behaviour's full type name.</summary>
    [ProtoMember(1)] public string Behaviour { get; set; } = "";
    [ProtoMember(2)] public string Json { get; set; } = "";
    [ProtoMember(3)] public int ProtocolVersion { get; set; }
}

/// <summary>Client -> server (TAOM sessions): send me the current state of every mirrored TAOM behaviour.</summary>
[ProtoContract(SkipConstructor = true)]
public sealed class NetworkTaomStateRequest : ICommand
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; }
}

/// <summary>
/// Server -> every client (TAOM sessions): the server just created a party with one of TAOM's own party components
/// (a refuge, a supply caravan). Coop only knows its eight vanilla component types, so without this the client's copy
/// of the party has no component at all. Fields are (name, kind, value) triples: s = string, v = primitive/enum
/// (invariant culture), o = a game object's StringId.
/// </summary>
[ProtoContract(SkipConstructor = true)]
public sealed class NetworkTaomPartyComponent : IEvent
{
    [ProtoMember(1)] public string TypeName { get; set; } = "";
    /// <summary>Coop object-manager id (PartyComponent_{party id}, the id Coop gives components loaded from a save).</summary>
    [ProtoMember(2)] public string ComponentId { get; set; } = "";
    [ProtoMember(3)] public string PartyId { get; set; } = "";
    [ProtoMember(4)] public List<string>? Fields { get; set; }
    [ProtoMember(5)] public int ProtocolVersion { get; set; }
}

/// <summary>
/// Server -> one client (TAOM sessions): a message TAOM showed while the server was acting for that player (a caravan
/// arrived, a refuge was raised, ...). On a dedicated server nobody would see it otherwise.
/// </summary>
[ProtoContract(SkipConstructor = true)]
public sealed class NetworkTaomNotice : IEvent
{
    [ProtoMember(1)] public string Text { get; set; } = "";
    /// <summary>ARGB of the line's colour (0 = default).</summary>
    [ProtoMember(2)] public uint Color { get; set; }
    /// <summary>True for the centre-screen banner (MBInformationManager.AddQuickInformation).</summary>
    [ProtoMember(3)] public bool Quick { get; set; }
    [ProtoMember(4)] public int ProtocolVersion { get; set; }
}
