// Fixture for AssemblyScanTests: a settings-shaped class under a save-data namespace must be ignored by the scan.
namespace SaveData
{
    public sealed class FakeStoredSettings { public static FakeStoredSettings Instance = new(); public int Value { get; set; } }
}
