namespace Godswar.Server.Domain.World.Content;

internal static class WonderlandTransportProtocol
{
    public const short MapId = 207;
    public const uint FirstTeleporterObjectId = 5205;
    public const uint BlackmarketObjectId = 5221;
    public const int TeleportDialogIndex = 57;
    // Captured paid choices 62/63 both return errors through shared function 59.
    public const int BlackmarketResultDialogIndex = 59;
    public const float InteractionRadius = 8f;
    public static readonly TimeSpan DialogLifetime = TimeSpan.FromMinutes(2);
    public static IReadOnlyList<int> BlackmarketDialogIndices { get; } = Array.AsReadOnly(new[] { 59, 62, 63 });

    // Native map207 capture, 2026-09-11. These NPC locations are deliberately
    // independent of the authored combat safe-zone and arrival anchors.
    public static NpcSpawnDefinition CapturedFirstTeleporter => Create(
        "Fane_001", 5205, 153f, -125f, 3.0718751f);
    public static NpcSpawnDefinition CapturedSecondTeleporter => Create(
        "Fane_002", 5206, -5f, -168.5f, 2.3218751f);
    public static NpcSpawnDefinition Blackmarket => Create(
        "Fane_017", 5221, 165f, -219f, 1.7f);

    public static NpcSpawnDefinition Create(string key, uint objectId, float x, float z, float facing) =>
        new(MapId, "Fane", key, $"{key}_Male15", objectId, x, z, objectId, 0x0211, facing, [], []);

    public static bool IsBlackmarket(NpcSpawnDefinition npc) => npc.MapId == MapId &&
        npc.NpcKey == "Fane_017" && npc.ObjectId == BlackmarketObjectId &&
        npc.InteractionId == BlackmarketObjectId && npc.TemplateKey == "Fane_017_Male15" &&
        npc.X == 165f && npc.Z == -219f;
}
