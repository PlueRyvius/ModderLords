using System.Collections.Generic;
using Common.Messaging;
using ProtoBuf;

namespace BetterEconomyCoopCompat.Messages;

// This module's own messages, in its own namespace. WIRE-FORMAT NOTE: Coop derives each message's wire id from a hash
// of the type's full name, so these names are part of the protocol. Never rename or move them; add new members with
// new tags, and raise Net.ProtocolVersion when a change means an older copy would misread them. Every message carries
// that version and is dropped by a side that speaks another.

/// <summary>Client -> server, when the joined campaign is ready: "I speak protocol N; may I relay?"</summary>
[ProtoContract(SkipConstructor = true)]
public sealed class LeHello : ICommand
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; }
}

/// <summary>Server -> that client: whether its relaying is accepted. Until it is, the player's PC changes nothing.</summary>
[ProtoContract(SkipConstructor = true)]
public sealed class LeHelloReply : IEvent
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; }
    [ProtoMember(2)] public bool Accepted { get; set; }
    [ProtoMember(3)] public string Detail { get; set; } = "";
}

/// <summary>Client -> server: a click in one of Living Economy's menus, for the server to carry out as this player.</summary>
[ProtoContract(SkipConstructor = true)]
public sealed class LeAction : ICommand
{
    [ProtoMember(1)] public string Op { get; set; } = "";
    [ProtoMember(2)] public List<string>? Args { get; set; }
    [ProtoMember(3)] public int Sequence { get; set; }
    [ProtoMember(4)] public int ProtocolVersion { get; set; }
}

/// <summary>Server -> the sending client: what happened, as the lines the mod would have shown ("colour|text").</summary>
[ProtoContract(SkipConstructor = true)]
public sealed class LeActionResult : IEvent
{
    [ProtoMember(1)] public string Op { get; set; } = "";
    [ProtoMember(2)] public int Sequence { get; set; }
    [ProtoMember(3)] public bool Ok { get; set; }
    [ProtoMember(4)] public List<string>? Lines { get; set; }
    [ProtoMember(5)] public int ProtocolVersion { get; set; }
}

/// <summary>Server -> accepted clients: one Living Economy behaviour's records, in full or the rows that changed.</summary>
[ProtoContract(SkipConstructor = true)]
public sealed class LeState : IEvent
{
    [ProtoMember(1)] public string Behaviour { get; set; } = "";
    [ProtoMember(2)] public string Payload { get; set; } = "";
    [ProtoMember(3)] public int ProtocolVersion { get; set; }
}

/// <summary>Client -> server: send me every mirrored behaviour in full (after the hello, or when a delta didn't fit).</summary>
[ProtoContract(SkipConstructor = true)]
public sealed class LeStateRequest : ICommand
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; }
}

/// <summary>Server -> one client: a line the mod showed while the server was acting for that player.</summary>
[ProtoContract(SkipConstructor = true)]
public sealed class LeNotice : IEvent
{
    [ProtoMember(1)] public string Text { get; set; } = "";
    [ProtoMember(2)] public uint Color { get; set; }
    [ProtoMember(3)] public int ProtocolVersion { get; set; }
}
