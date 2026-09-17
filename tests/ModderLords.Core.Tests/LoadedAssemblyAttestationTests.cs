using ModderLords.Operations;
using Newtonsoft.Json.Linq;

namespace ModderLords.Core.Tests;

public sealed class LoadedAssemblyAttestationTests
{
    private static readonly string Hash = new('A', 64);
    private static JArray Fingerprints() => new(new JObject { ["Name"] = "Fixture.dll", ["Side"] = "Client", ["Sha256"] = Hash });
    [Fact] public void EveryLoadedCopyMustMatch()
    {
        KeyValuePair<string, string>[] loaded = [new("Fixture", "selected"), new("Fixture", "other-copy")];
        Assert.False(LoadedAssemblyAttestation.Validate(Fingerprints(), "Client", loaded, p => p == "selected" ? Hash : new string('B', 64), out _));
        Assert.True(LoadedAssemblyAttestation.Validate(Fingerprints(), "Client", loaded, _ => Hash, out _));
    }
    [Fact] public void AmbiguousExpectedBytesCannotValidateLoadedAssembly()
    {
        var inputs = Fingerprints(); var duplicate = (JObject)inputs[0].DeepClone(); duplicate["Sha256"] = new string('B', 64); inputs.Add(duplicate);
        Assert.False(LoadedAssemblyAttestation.Validate(inputs, "Client", [new("Fixture", "file")], _ => Hash, out _));
    }
    [Fact] public void ChecksOnlyRelevantSideAndReportsUnreadableLoadedFile()
    {
        Assert.True(LoadedAssemblyAttestation.Validate(Fingerprints(), "Server", [new("Fixture", "file")], _ => throw new IOException(), out _));
        Assert.False(LoadedAssemblyAttestation.Validate(Fingerprints(), "Client", [new("Fixture", "file")], _ => throw new IOException(), out var reason));
        Assert.Contains("cannot be verified", reason);
    }
}
