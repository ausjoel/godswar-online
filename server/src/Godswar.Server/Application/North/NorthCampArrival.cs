using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.North;

/// <summary>
/// FINALISE2 F-08 (the admin 2026-11-05): "Each of the new maps should teleport players the NPC safe zone area when coming
/// from a NPC teleporter". A transporter button whose target is a north map (6, 7, 10, 20, 21, 22) lands the player in
/// that map's camp: the centre of the operator-placed npcs standing within <see cref="RingRadius"/> of the camp quest
/// giver (NorthLocalQuests.Givers), which is inside the PvP safe radius when that rate is on. NPCs stand on walkable
/// ground, so their centre does too (GENERATED rule). An editor row in npc_teleport_overrides still wins.
/// </summary>
internal static class NorthCampArrival
{
    public const float RingRadius = 15f;

    public static bool TryGet(int targetMapId, out float x, out float z) =>
        TryGet(GmNpcOverrideCatalog.Current, targetMapId, out x, out z);

    internal static bool TryGet(GmNpcOverrideSnapshot snapshot, int targetMapId, out float x, out float z)
    {
        x = z = 0f;
        if (targetMapId is < short.MinValue or > short.MaxValue || !NorthNpcSafeZone.Maps.Contains((short)targetMapId))
        {
            return false;
        }

        var mapId = (short)targetMapId;
        var giverKey = NorthLocalQuests.Givers.FirstOrDefault(g => g.MapId == mapId).NpcKey;
        var spawns = snapshot.SpawnsFor(mapId);
        var giver = spawns.FirstOrDefault(s => s.Enabled && string.Equals(s.NpcKey, giverKey, StringComparison.Ordinal));
        if (giver is null) return false;
        var ring = spawns.Where(s => s.Enabled &&
                                     Math.Abs(s.X - giver.X) <= RingRadius && Math.Abs(s.Z - giver.Z) <= RingRadius &&
                                     ((s.X - giver.X) * (s.X - giver.X)) + ((s.Z - giver.Z) * (s.Z - giver.Z)) <=
                                     RingRadius * RingRadius).ToList();
        x = ring.Average(s => s.X);
        z = ring.Average(s => s.Z);
        return float.IsFinite(x) && float.IsFinite(z);
    }
}
