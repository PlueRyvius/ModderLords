using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.SaveSystem;

namespace ModderLords.CompatSync;

/// <summary>
/// Registers save-container shapes that a loaded mod uses but does not define itself.
///
/// The registration must live in the shared client/server module: the host needs it while
/// saving and the joining client needs the same definition while receiving the save snapshot.
/// Keep this list limited to confirmed compatibility gaps; duplicate container definitions
/// cause the engine's save system to assert.
/// </summary>
internal sealed class SaveableTypeCompatDefiner : SaveableTypeDefiner
{
    // Reserved for ModderLords compatibility definitions. Container IDs are assigned by the
    // engine, but the API still requires a stable, assembly-unique base ID.
    private const int SaveBaseId = 0x4D4C0001;

    public SaveableTypeCompatDefiner() : base(SaveBaseId) { }

    protected override void DefineContainerDefinitions()
    {
        // Fourberie serializes this exact field shape without registering its container.
        ConstructContainerDefinition(typeof(Dictionary<int, CampaignTime>));
    }
}
