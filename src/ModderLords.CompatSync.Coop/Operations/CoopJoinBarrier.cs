using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Common.Messaging;
using Coop.Core.Server.Connections.Messages;
using Coop.Core.Server.Connections.States;
using HarmonyLib;
using LiteNetLib;
using ModderLords.Operations;

namespace ModderLords.CompatSync.Coop.Operations;

/// <summary>Exact, locally compiled admission seam for the inspected Coop build.</summary>
internal sealed class CoopJoinBarrier : IDisposable
{
    private const string Owner = "ModderLords.Operations.CoopJoin.v1";
    private readonly Harmony harmony = new Harmony(Owner);
    private readonly object gate = new object();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly JoinAdmission<NetPeer, Action> admission = new JoinAdmission<NetPeer, Action>();
    private MethodInfo? target;
    private static CoopJoinBarrier? current;
    [ThreadStatic] private static ResolveCharacterState? resuming;
    public Action<NetPeer>? SendPlan;
    public bool ClientAgreed { get; set; }

    public void Install()
    {
        if (current != null) throw new InvalidOperationException("Coop admission barrier already installed");
        var isServer = OperationProcessSide.IsServer;
        if (!CoopAdmissionCapabilities.TryResolve(isServer, out target, out var reason))
            throw new InvalidOperationException("Coop admission capability unavailable: " + reason);
        CheckOwnership();
        current = this;
        try
        {
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(CoopJoinBarrier), isServer ? nameof(ValidatePrefix) : nameof(LoadPrefix)));
            if (Harmony.GetPatchInfo(target)?.Owners.Contains(Owner) != true)
                throw new InvalidOperationException("Coop admission hook did not become active");
            Log.Info("operation admission capability active: " + CoopAdmissionCapabilities.Describe(isServer));
        }
        catch { Dispose(); throw; }
    }
    private void CheckOwnership()
    {
        if (target != null && Harmony.GetPatchInfo(target)?.Owners.Any(o => o != Owner) == true)
            throw new InvalidOperationException("Unexpected Harmony owner on Coop admission method " + target.Name);
    }
    public bool Begin(NetPeer peer)
    { lock (gate) return admission.Begin(peer, clock.ElapsedMilliseconds); }
    public bool Acknowledge(NetPeer peer)
    { lock (gate) return admission.Acknowledge(peer, clock.ElapsedMilliseconds); }
    private static bool ValidatePrefix(ResolveCharacterState __instance, MessagePayload<NetworkClientValidate> obj)
    {
        var barrier = current;
        if (barrier == null) return true;
        if (obj.Who is not NetPeer peer || !ReferenceEquals(peer, __instance.ConnectionLogic.Peer)) return false;
        try
        {
            barrier.CheckOwnership();
            if (ReferenceEquals(resuming, __instance)) return true;
            if (obj.What?.PlayerId == null || obj.What.PlayerId.Length > 256) { peer.Disconnect(); return false; }
            // Copy only the bounded, immutable validation identity, never retain a transport packet.
            var validation = new MessagePayload<NetworkClientValidate>(peer, new NetworkClientValidate(obj.What.PlayerId));
            lock (barrier.gate)
            {
                if (!barrier.admission.Queue(peer, validation.What.PlayerId, () =>
                {
                    if (peer.ConnectionState != ConnectionState.Connected || !ReferenceEquals(__instance.ConnectionLogic.State, __instance)) return;
                    resuming = __instance;
                    try { barrier.target!.Invoke(__instance, new object[] { validation }); }
                    finally { resuming = null; }
                }, barrier.clock.ElapsedMilliseconds)) { peer.Disconnect(); return false; }
            }
            Log.Info("operation admission pending peer=" + peer.Id);
            barrier.SendPlan?.Invoke(peer);
        }
        catch (Exception ex) { Log.Warn("Coop admission refused: " + ex.GetBaseException().Message); peer.Disconnect(); }
        return false;
    }
    private static bool LoadPrefix()
    {
        var barrier = current;
        if (barrier == null) return true;
        barrier.CheckOwnership();
        if (!barrier.ClientAgreed || !OperationRuntime.SessionActive || !OperationRuntime.CheckReadiness())
            throw new InvalidOperationException("Coop save load refused before operation plan agreement");
        return true;
    }
    public void Tick()
    {
        // Called on the game thread. No lock is held while Coop resumes character restoration.
        NetPeer[] peers;
        lock (gate) peers = admission.Peers;
        foreach (var peer in peers)
        {
            Action? resume = null;
            lock (gate)
            {
                if (peer.ConnectionState != ConnectionState.Connected || admission.IsExpired(peer, clock.ElapsedMilliseconds))
                { admission.Disconnect(peer); peer.Disconnect(); continue; }
                if (admission.TryTake(peer, clock.ElapsedMilliseconds, out var action)) resume = action;
            }
            try
            {
                if (resume != null && !OperationRuntime.CheckReadiness()) { peer.Disconnect(); continue; }
                if (resume != null) { Log.Info("operation admission resumed peer=" + peer.Id); resume(); }
            }
            catch (Exception ex) { Log.Warn("Coop admission resume failed: " + ex.GetBaseException().Message); peer.Disconnect(); }
        }
    }
    public void Dispose()
    {
        lock (gate) admission.Clear();
        if (target != null) harmony.Unpatch(target, HarmonyPatchType.All, Owner);
        ClientAgreed = false; SendPlan = null;
        if (ReferenceEquals(current, this)) current = null;
    }
}
