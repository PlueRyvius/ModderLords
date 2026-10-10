using ModderLords.CompatSync;

namespace ModderLords.CompatSync.Coop;

/// <summary>Entry points the submodule calls by reflection; keeps every Coop reference inside this assembly.</summary>
public static class Bridge
{
    public const int ProtocolVersion = 1;
    public static void InitializeOperations() => Operations.OperationRuntime.InitializeFromEnvironment();
    public static void BeforeCampaign() => Operations.OperationRuntime.BeforeCampaign();
    public static void EndOperations() => Operations.OperationRuntime.EndSession();

    private static readonly TickSteps Steps = new TickSteps(Log.Warn);

    /// <summary>
    /// Every 3 s on both sides: settings broadcast and apply, then each layer's periodic work. Each step runs on its own
    /// (<see cref="TickSteps"/>), so one that throws no longer skips the rest.
    /// </summary>
    public static void Tick()
    {
        Steps.Run("ServerSettingsHandler.BroadcastChanges", () => global::Coop.Core.Server.Services.ModderLordsCompat.Handlers.ServerSettingsHandler.Current?.BroadcastChanges());
        Steps.Run("ClientSettingsHandler.ApplyPending", () => global::Coop.Core.Client.Services.ModderLordsCompat.Handlers.ClientSettingsHandler.Current?.ApplyPending());
        Steps.Run("EncounterOptionGate.EnsureInstalled", () => EncounterOptionGate.EnsureInstalled());
        Steps.Run("EncounterOptionGate.Tick", () => EncounterOptionGate.Tick());
        Steps.Run("BattleScenePick.EnsureInstalled", () => BattleScenePick.EnsureInstalled());
        Steps.Run("BellumThreadSafety.EnsureInstalled", () => BellumThreadSafety.EnsureInstalled());
        Steps.Run("KingdomVoteTimeoutAi.EnsureInstalled", () => KingdomVoteTimeoutAi.EnsureInstalled());
        Steps.Run("InventoryExchangeEvent.EnsureInstalled", () => InventoryExchangeEvent.EnsureInstalled());
        Steps.Run("ModKingdomDecisions.EnsureInstalled", () => ModKingdomDecisions.EnsureInstalled());
        Steps.Run("SessionCheck.EnsureRegistered", () => SessionCheck.EnsureRegistered());
        Steps.Run("Admin.PlayerAdmin.ServerTick", () => Admin.PlayerAdmin.ServerTick());
        Steps.Run("NetMeter.ServerTick", () => NetMeter.ServerTick());
        Steps.Run("SessionCheck.ClientTick", () => SessionCheck.ClientTick());
        Steps.Run("SmokeCharacterCreation.ClientTick", () => SmokeCharacterCreation.ClientTick());
        Steps.Run("Taom.TaomLayer.Tick", () => Taom.TaomLayer.Tick());
        Steps.Run("Taom.TaomStateMirror.ServerTick", () => Taom.TaomStateMirror.ServerTick());
        Steps.Run("LivingEconomy.LivingEconomyLayer.Tick", () => LivingEconomy.LivingEconomyLayer.Tick());
        Steps.Run("LivingEconomy.LeStateMirror.ServerTick", () => LivingEconomy.LeStateMirror.ServerTick());
        Steps.Run("Fourberie.FourberieLayer.Tick", () => Fourberie.FourberieLayer.Tick());
        Steps.Run("Fourberie.FbBooks.ServerTick", () => Fourberie.FbBooks.ServerTick());
        Steps.Run("Fourberie.FbMirrorClient.ClientTick", () => Fourberie.FbMirrorClient.ClientTick());
        Steps.Run("Fourberie.FbEffects.ClientTick", () => Fourberie.FbEffects.ClientTick());
        Steps.Run("Taom.SpecialResourceSyncComponent.ClientTick", () => Taom.SpecialResourceSyncComponent.ClientTick());
        Steps.Run("Taom.CareerSyncComponent.ClientTick", () => Taom.CareerSyncComponent.ClientTick());
        Steps.Run("Taom.PartyComponentSyncComponent.ClientTick", () => Taom.PartyComponentSyncComponent.ClientTick());
        Steps.Run("Taom.RefugeComponent.ClientTick", () => Taom.RefugeComponent.ClientTick());
        Steps.Run("Taom.ClientStateBackupComponent.ClientTick", () => Taom.ClientStateBackupComponent.ClientTick());
        Steps.Run("Taom.FieldCommissionComponent.ClientTick", () => Taom.FieldCommissionComponent.ClientTick());
        Steps.Run("OperationRuntime.JoinBarrier.Tick", () => Operations.OperationRuntime.JoinBarrier?.Tick());
        Steps.Run("BellumSnapshotProbe.Tick", () => Operations.BellumSnapshotProbe.Tick());
        Steps.Run("BellumSnapshotBroadcast.Tick", () => Operations.BellumSnapshotBroadcast.Tick());
        Steps.Run("BellumPrompts.ServerTick", () => Operations.BellumPrompts.ServerTick());
        Steps.Run("OperationServerHandler.RemoveDisconnectedPeers", () => global::Coop.Core.Server.Services.ModderLordsCompat.Handlers.OperationServerHandler.Current?.RemoveDisconnectedPeers());
        Steps.Run("BehaviorGate.RetryPendingPostfixes", () => BehaviorGate.RetryPendingPostfixes());
        Steps.Run("OperationClientHandler.ApplyPendingSnapshots", () => global::Coop.Core.Client.Services.ModderLordsCompat.Handlers.OperationClientHandler.Current?.ApplyPendingSnapshots());
    }

    /// <summary>
    /// Client, every quarter second: sends relayed player actions whose control has settled, and opens the next Bellum
    /// prompt the server is waiting on. Cheap when none are waiting.
    /// </summary>
    public static void RelayTick()
    {
        Steps.Run("ClientSettingsHandler.FlushRelays", () => global::Coop.Core.Client.Services.ModderLordsCompat.Handlers.ClientSettingsHandler.Current?.FlushRelays());
        Steps.Run("BellumPrompts.ClientTick", () => Operations.BellumPrompts.ClientTick());
    }

    /// <summary>The verification counter line for this side (server should count gated behaviours, a client should stay at 0).</summary>
    public static string VerificationSummary() => BehaviorGate.VerificationSummary() + "; " + Operations.OperationRuntime.Report()
        + LivingEconomy.LivingEconomyLayer.Summary() + Fourberie.FourberieLayer.Summary() + Operations.BellumPrompts.Summary() + "; " + InventoryExchangeEvent.Summary() + "; " + Steps.Summary();

    /// <summary>Both sides, every 30 s and at unload: writes the ground-truth trace counters. Returns how many methods were written (0 when not tracing).</summary>
    public static int TraceFlush() => BehaviorGate.TraceFlush();
}
