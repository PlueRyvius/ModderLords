using System.Collections.Generic;
using Common.Messaging;
using ProtoBuf;

namespace BellumCivileCoopCompat.Net.Messages
{
    // WIRE FORMAT: Coop derives each message's wire id from a hash of the type's full name, so these names and this
    // namespace are part of the protocol between the server and players. Never rename or move them; add members with
    // new tags. Two generic envelopes carry every kind of agency message (Agency in the module decides what each means).

    /// <summary>Player's PC to server: a request about Bellum (an action, an answer to a prompt, a state request).</summary>
    [ProtoContract(SkipConstructor = true)]
    public sealed class BellumAgencyRequest : ICommand
    {
        [ProtoMember(1)] public int Protocol { get; set; }
        [ProtoMember(2)] public string Kind { get; set; } = "";
        [ProtoMember(3)] public List<string> Args { get; set; } = new List<string>();
    }

    /// <summary>Server to one player's PC: a message about Bellum (a prompt, a notice, an answer, state).</summary>
    [ProtoContract(SkipConstructor = true)]
    public sealed class BellumAgencyMessage : IEvent
    {
        [ProtoMember(1)] public int Protocol { get; set; }
        [ProtoMember(2)] public string Kind { get; set; } = "";
        [ProtoMember(3)] public List<string> Args { get; set; } = new List<string>();
    }
}
