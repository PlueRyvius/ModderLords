using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace ModderLords.Operations;

public enum RequestState { Pending, Completed, Rejected, Disconnected }
public enum AdapterReadiness { Disabled, Waiting, Ready, Degraded, Failed }
public interface ICompatibilityAdapter : IDisposable
{
    string Id { get; }
    AdapterReadiness Readiness { get; }
    string Detail { get; }
    bool ValidateTargets(out string reason);
    void Install();
}
public sealed class Actor
{
    public string ControllerId { get; }
    public string HeroId { get; }
    public string ClanId { get; }
    public Actor(string controllerId, string heroId, string clanId) { ControllerId = controllerId; HeroId = heroId; ClanId = clanId; }
}
public sealed class OperationCommand
{
    public string Digest { get; }
    public string Epoch { get; }
    public string RequestId { get; }
    public string OperationId { get; }
    public string Payload { get; }
    public OperationCommand(string digest, string epoch, string requestId, string operationId, string payload)
    { Digest = digest; Epoch = epoch; RequestId = requestId; OperationId = operationId; Payload = payload; }
}
public sealed class OperationResult
{
    public string RequestId { get; }
    public RequestState State { get; }
    public string Detail { get; }
    public string Payload { get; }
    public long Revision { get; }
    public OperationResult(string requestId, RequestState state, string detail, string payload = "", long revision = 0)
    { RequestId = requestId; State = state; Detail = detail; Payload = payload; Revision = revision; }
}
public interface IServerOperation
{
    string Id { get; }
    int MaxPayloadBytes { get; }
    bool Validate(Actor actor, string payload, out string reason);
    string Execute(Actor actor, string payload);
}
public interface ISnapshotOperation : IServerOperation
{
    bool CanReadSnapshot(Actor actor);
    long SnapshotRevision { get; }
    string CaptureSnapshot(Actor actor);
}
/// <summary>Called on the game thread, after transport authentication and peer-plan admission.</summary>
public sealed class CommandDispatcher
{
    private sealed class Receipt { public string Fingerprint = ""; public OperationResult Result = null!; }
    private readonly Dictionary<string, IServerOperation> operations = new Dictionary<string, IServerOperation>(StringComparer.Ordinal);
    private readonly Dictionary<string, Receipt> receipts = new Dictionary<string, Receipt>(StringComparer.Ordinal);
    private readonly string digest;
    private readonly string epoch;
    private readonly int capacity;
    private long revision;
    public CommandDispatcher(string digest, string epoch, IEnumerable<IServerOperation> operations, int capacity = 4096)
    { this.digest = digest; this.epoch = epoch; this.capacity = capacity; foreach (var op in operations) this.operations.Add(op.Id, op); }
    public OperationResult Execute(OperationCommand command, Actor? actor)
    {
        OperationResult Reject(string reason) => new OperationResult(command.RequestId ?? "", RequestState.Rejected, reason);
        if (actor == null || string.IsNullOrEmpty(actor.ControllerId) || string.IsNullOrEmpty(actor.HeroId)) return Reject("Unauthenticated actor");
        if (command.Digest != digest || command.Epoch != epoch) return Reject("Session plan mismatch");
        if (!Guid.TryParseExact(command.RequestId, "N", out _) || command.OperationId == null || !operations.TryGetValue(command.OperationId, out var operation)) return Reject("Unknown operation or invalid request id");
        if (command.Payload == null || Encoding.UTF8.GetByteCount(command.Payload) > operation.MaxPayloadBytes) return Reject("Payload exceeds contract bounds");
        var key = actor.ControllerId + "\n" + command.RequestId;
        var fingerprint = Hash(command.OperationId + "\n" + command.Payload);
        if (receipts.TryGetValue(key, out var previous)) return previous.Fingerprint == fingerprint ? previous.Result : Reject("Request id reused with different content");
        if (receipts.Count >= capacity) return Reject("Session receipt capacity reached; no mutation performed");
        var receipt = new Receipt { Fingerprint = fingerprint, Result = new OperationResult(command.RequestId, RequestState.Pending, "Executing") };
        receipts.Add(key, receipt); // Reserve before execution, including re-entrant calls.
        try
        {
            if (!operation.Validate(actor, command.Payload, out var reason)) receipt.Result = Reject(reason);
            else receipt.Result = new OperationResult(command.RequestId, RequestState.Completed, "Completed", operation.Execute(actor, command.Payload), ++revision);
        }
        catch (Exception)
        {
            // Failed operations are never retried automatically: a feature must provide its own atomic mutation.
            receipt.Result = Reject("Operation failed; authoritative state must be refreshed");
        }
        return receipt.Result;
    }
    private static string Hash(string text) { using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text))); }
}

public sealed class ClientOperationState
{
    private readonly Dictionary<string, RequestState> requests = new Dictionary<string, RequestState>();
    private string epoch = "";
    private long appliedRevision = -1;
    private long pendingRevision = -1;
    private string? pendingSnapshot;
    public bool ApplyingSnapshot { get; private set; }
    public void BeginSession(string authenticatedEpoch)
    {
        if (string.IsNullOrEmpty(authenticatedEpoch)) throw new ArgumentException("Authenticated epoch required");
        Disconnect(); epoch = authenticatedEpoch; appliedRevision = -1; pendingRevision = -1; pendingSnapshot = null;
    }
    public bool BeginRequest(string id)
    {
        if (epoch.Length == 0 || ApplyingSnapshot || requests.ContainsKey(id)) return false;
        requests.Add(id, RequestState.Pending); return true;
    }
    public RequestState? State(string id) => requests.TryGetValue(id, out var state) ? state : (RequestState?)null;
    public bool AcceptResult(string authenticatedEpoch, OperationResult result)
    {
        if (authenticatedEpoch != epoch || State(result.RequestId) != RequestState.Pending) return false;
        requests[result.RequestId] = result.State; return true;
    }
    public bool OfferSnapshot(string authenticatedEpoch, long revision, string payload, Func<bool> ready, Action<string> apply, Action refresh)
    {
        if (authenticatedEpoch != epoch || epoch.Length == 0 || revision < 0 || revision <= appliedRevision || revision < pendingRevision) return false;
        pendingSnapshot = payload; pendingRevision = revision;
        return ApplyPending(ready, apply, refresh);
    }
    public bool ApplyPending(Func<bool> ready, Action<string> apply, Action refresh)
    {
        if (pendingSnapshot == null || !ready()) return false;
        ApplyingSnapshot = true;
        try { apply(pendingSnapshot); refresh(); appliedRevision = pendingRevision; pendingSnapshot = null; return true; }
        finally { ApplyingSnapshot = false; }
    }
    public void Disconnect()
    {
        foreach (var id in new List<string>(requests.Keys)) if (requests[id] == RequestState.Pending) requests[id] = RequestState.Disconnected;
        epoch = ""; pendingSnapshot = null;
    }
}

public sealed class SessionActivation
{
    public string? Digest { get; private set; }
    public bool CampaignStarted { get; private set; }
    public bool Freeze(string digest)
    {
        if (string.IsNullOrEmpty(digest)) return false;
        if (Digest != null) return Digest == digest;
        if (CampaignStarted) return false;
        Digest = digest; return true;
    }
    public void MarkCampaignStarted() { CampaignStarted = true; }
    public bool Admit(string peerDigest) => Digest != null && Digest == peerDigest;
}
