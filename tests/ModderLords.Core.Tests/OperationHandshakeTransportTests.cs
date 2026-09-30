namespace ModderLords.Core.Tests;

/// <summary>
/// Guards the connection-handshake transport seam without making the portable test project depend on
/// Coop's locally installed assemblies. Coop drops ordinary server messages before the joining peer's
/// save transfer begins, so the plan and its acknowledgement must stay on its immediate path.
/// </summary>
public sealed class OperationHandshakeTransportTests
{
    [Fact]
    public void Plan_and_ack_bypass_Coops_pre_save_world_message_gate()
    {
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "ModderLords.CompatSync.Coop", "Operations", "OperationHandlers.cs"));

        var text = File.ReadAllText(source);
        Assert.Contains("network.SendImmediate(peer, new OperationPlanV1", text, StringComparison.Ordinal);
        Assert.Contains("network.SendImmediate(peer, new OperationPlanAckV1", text, StringComparison.Ordinal);
    }
}
