using ModderLords.Operations;
using Newtonsoft.Json.Linq;

namespace ModderLords.Core.Tests;

public sealed class InputAttestationTests
{
    private static readonly string Hash = new('A', 64);
    private static (JObject Plan, JObject Local) Inputs()
    {
        var fingerprint = new JObject { ["Module"] = "fixture", ["Name"] = "settings.xml", ["Side"] = "Client", ["Sha256"] = Hash };
        var order = new JArray("fixture");
        return (new JObject { ["ModuleOrder"] = order, ["Fingerprints"] = new JArray(fingerprint) },
            new JObject { ["ModuleOrder"] = order.DeepClone(), ["Gaps"] = new JArray(), ["Values"] = new JArray(),
                ["Files"] = new JArray(new JObject { ["Fingerprint"] = fingerprint.DeepClone(), ["Path"] = Path.GetFullPath("local-settings.xml") }) });
    }
    [Fact] public void RehashesLocalPathAndRejectsChangedBytes()
    {
        var (plan, local) = Inputs(); var reads = 0;
        Assert.True(InputAttestation.Validate(plan, local, "Client", p => { reads++; Assert.EndsWith("local-settings.xml", p); return Hash; }, out _));
        Assert.Equal(1, reads);
        Assert.False(InputAttestation.Validate(plan, local, "Client", _ => new string('B', 64), out var reason));
        Assert.Contains("changed", reason);
    }
    [Fact] public void RemotePathCannotChooseAFileToRead()
    {
        var (plan, local) = Inputs(); plan["Fingerprints"]![0]!["Path"] = "remote-controlled";
        Assert.True(InputAttestation.Validate(plan, local, "Client", p => { Assert.DoesNotContain("remote", p); return Hash; }, out _));
    }
    [Fact] public void MissingFilesUnresolvedInputsAndDifferentOrderRefuseActivation()
    {
        var (plan, local) = Inputs(); local["Files"] = new JArray();
        Assert.False(InputAttestation.Validate(plan, local, "Client", _ => Hash, out _));
        (plan, local) = Inputs(); local["Gaps"] = new JArray("unresolved");
        Assert.False(InputAttestation.Validate(plan, local, "Client", _ => Hash, out _));
        (plan, local) = Inputs(); local["ModuleOrder"] = new JArray("other", "fixture");
        Assert.False(InputAttestation.Validate(plan, local, "Client", _ => Hash, out _));
    }
    [Fact] public void UnreadableFileIsExplicitFailure()
    {
        var (plan, local) = Inputs();
        Assert.False(InputAttestation.Validate(plan, local, "Client", _ => throw new IOException(), out var reason));
        Assert.Contains("unreadable", reason);
    }
}
