using Common.Messaging;
using ProtoBuf;

namespace ModderLords.CompatSync.Coop.Operations;

[ProtoContract]
public sealed class OperationHelloV1 : ICommand
{
    [ProtoMember(1)] public int Version { get; set; } = 1;
}
[ProtoContract]
public sealed class OperationPlanV1 : IEvent
{
    [ProtoMember(1)] public int Version { get; set; } = 1;
    [ProtoMember(2)] public string Json { get; set; } = "";
    [ProtoMember(3)] public string Epoch { get; set; } = "";
}
[ProtoContract]
public sealed class OperationPlanAckV1 : ICommand
{
    [ProtoMember(1)] public string Digest { get; set; } = "";
    [ProtoMember(2)] public string Epoch { get; set; } = "";
}
[ProtoContract]
public sealed class OperationCommandV1 : ICommand
{
    [ProtoMember(1)] public string Digest { get; set; } = "";
    [ProtoMember(2)] public string Epoch { get; set; } = "";
    [ProtoMember(3)] public string RequestId { get; set; } = "";
    [ProtoMember(4)] public string OperationId { get; set; } = "";
    [ProtoMember(5)] public string Payload { get; set; } = "";
}
[ProtoContract]
public sealed class OperationResultV1 : IEvent
{
    [ProtoMember(1)] public string Epoch { get; set; } = "";
    [ProtoMember(2)] public string RequestId { get; set; } = "";
    [ProtoMember(3)] public int State { get; set; }
    [ProtoMember(4)] public string Detail { get; set; } = "";
    [ProtoMember(5)] public string Payload { get; set; } = "";
    [ProtoMember(6)] public long Revision { get; set; }
}
[ProtoContract]
public sealed class OperationSnapshotRequestV1 : ICommand
{
    [ProtoMember(1)] public string Digest { get; set; } = "";
    [ProtoMember(2)] public string Epoch { get; set; } = "";
    [ProtoMember(3)] public string OperationId { get; set; } = "";
}
[ProtoContract]
public sealed class OperationSnapshotV1 : IEvent
{
    [ProtoMember(1)] public string Epoch { get; set; } = "";
    [ProtoMember(2)] public string OperationId { get; set; } = "";
    [ProtoMember(3)] public long Revision { get; set; }
    [ProtoMember(4)] public string Payload { get; set; } = "";
}
