using Godswar.Server.Domain.World.Content;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Completes Arena's five-actor client-native roster by adding its Vendor,
/// Ward, and Physician to the corrected V3 transport pair. The reviewed V3
/// spawn set remains immutable.
/// </summary>
internal static class NpcContentBaselineV4
{
    public const int ExpectedEntryCount =
        NpcContentBaselineV3.ExpectedEntryCount + 3;
    public const string ExpectedRevision =
        "16E9D41EEC5ABA17734B22FF5FFB07EA0C9B29FF633A1FCE7266F2D822E41E85";
    public const string Source = "reviewed-published-npc-baseline-v4";

    public static NpcSpawnDefinition[] LoadDefinitions()
    {
        NpcSpawnDefinition[] definitions =
        [
            .. NpcContentBaselineV3.LoadDefinitions(),
            Create(
                DuelArenaNpcRoster.VendorNpcKey,
                DuelArenaNpcRoster.VendorTemplateKey,
                DuelArenaNpcRoster.LegacyVendorNpcId,
                DuelArenaNpcRoster.VendorSpawnX,
                DuelArenaNpcRoster.VendorSpawnZ,
                DuelArenaNpcRoster.VendorFacing),
            Create(
                DuelArenaNpcRoster.WardNpcKey,
                DuelArenaNpcRoster.WardTemplateKey,
                DuelArenaNpcRoster.LegacyWardNpcId,
                DuelArenaNpcRoster.WardSpawnX,
                DuelArenaNpcRoster.WardSpawnZ,
                DuelArenaNpcRoster.WardFacing),
            Create(
                DuelArenaNpcRoster.PhysicianNpcKey,
                DuelArenaNpcRoster.PhysicianTemplateKey,
                DuelArenaNpcRoster.LegacyPhysicianNpcId,
                DuelArenaNpcRoster.PhysicianSpawnX,
                DuelArenaNpcRoster.PhysicianSpawnZ,
                DuelArenaNpcRoster.PhysicianFacing)
        ];
        var canonical = definitions
            .OrderBy(static definition => definition.MapId)
            .ThenBy(
                static definition => definition.NpcKey,
                StringComparer.Ordinal)
            .ThenBy(
                static definition => definition.TemplateKey,
                StringComparer.Ordinal)
            .ThenBy(static definition => definition.ObjectId)
            .ToArray();
        if (canonical.Length != ExpectedEntryCount)
        {
            throw new InvalidDataException(
                "The V4 Arena roster overlay requires exactly three new " +
                "support actors.");
        }

        return canonical;
    }

    private static NpcSpawnDefinition Create(
        string npcKey,
        string templateKey,
        uint npcId,
        float x,
        float z,
        float facing) =>
        new(
            DuelArenaTransporterProtocol.MapId,
            "Arena",
            npcKey,
            templateKey,
            npcId,
            x,
            z,
            npcId,
            NpcAppearanceDefaults.AppearanceType,
            facing,
            Detail10077: [],
            Detail10080: []);
}
