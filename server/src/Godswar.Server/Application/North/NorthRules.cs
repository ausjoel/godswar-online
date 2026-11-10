using System.Collections.Frozen;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.World;

namespace Godswar.Server.Application.North;

/// <summary>
/// FINALISE-NORTH (the admin 2026-11-05): "I'd like for the NPCS to not have mobs near them and for it
/// to be a small safe zone when close to them (factions cant kill eachother when close enough to
/// interact with them)". The radius is GENERATED: the server has no npc interaction-distance
/// constant (the client decides when a click opens a dialog), so the default covers a camp's npc
/// ring (the placed npcs stand 4.5-10 units apart) plus a few steps.
/// </summary>
internal static class NorthRateDefinitions
{
    public const string NorthCategory = "North maps"; // FINALISE-NORTH

    public static IReadOnlyList<ServerRateDefinition> All { get; } =
    [
        new(NorthRateKeys.NpcSafeRadius, NorthCategory, "PvP safe radius round north camp npcs", "units",
            ServerRateKind.Absolute, 0m, 0m, 40m, 1m, 10,
            "Game/PvpWorldAuthorityCatalog.cs (every player-vs-player admission)",
            "GENERATED rule (the admin 2026-11-05). On the north maps (Mycenae 6, Olympia 7, Larissa 10, " +
            "Delphi Forest 20, Olympus 21, Elasson 22) a player standing within this many units of an " +
            "operator-placed npc (gm_npc_spawns) cannot attack, or be attacked by, another player: " +
            "basic attacks, skills, area skills and periodic damage are all refused as a safe zone. " +
            "0 = off (as before). Suggested 12. Read on every PvP admission, so a change applies after " +
            "the next rates reload."),
    ];
}

internal static class NorthRateKeys
{
    public const string NpcSafeRadius = "pvp.npc_safe_radius";
}

/// <summary>FINALISE-NORTH: the PvP safe zone round the npcs placed on the north maps.</summary>
internal static class NorthNpcSafeZone
{
    /// <summary>The maps the rule applies to (the north maps of SCALE-PROPOSAL-V2).</summary>
    public static FrozenSet<short> Maps { get; } = new short[] { 6, 7, 10, 20, 21, 22 }.ToFrozenSet();

    public static bool Contains(short mapId, float x, float z) =>
        Contains(
            GmNpcOverrideCatalog.Current,
            ServerRates.Current.Get(NorthRateKeys.NpcSafeRadius),
            mapId,
            x,
            z);

    internal static bool Contains(
        GmNpcOverrideSnapshot snapshot,
        decimal radius,
        short mapId,
        float x,
        float z)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (radius <= 0m || !Maps.Contains(mapId) || !float.IsFinite(x) || !float.IsFinite(z))
        {
            return false;
        }

        var limit = (double)radius * (double)radius;
        foreach (var npc in snapshot.SpawnsFor(mapId))
        {
            var dx = (double)npc.X - x;
            var dz = (double)npc.Z - z;
            if ((dx * dx) + (dz * dz) <= limit)
            {
                return true;
            }
        }

        return false;
    }
}
