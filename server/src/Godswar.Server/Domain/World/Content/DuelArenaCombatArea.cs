namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// ARENA build: where on map 57 players may fight, and where an arena death revives.
/// </summary>
/// <remarks>
/// IGG "Duel Arena" (igg-acid=372): the arena "is primarily divided into a waiting
/// area and a combat area"; the Arena Gatekeeper sends players to random points of
/// the combat area, where they may attack one another regardless of faction.
/// <para>
/// The two areas are the two walkable components of the client's own
/// <c>Map/Arena.hmp</c> (block plane read with
/// <c>Development/teleport-audit/mapwalk.py</c>, 1-unit flood fill, 2026-11-03):
/// the combat floor that holds the captured Gatekeeper arrival (-5,32) spans
/// x -89..17, z -26..80 (7,833 cells, the Ward at -48,40 stands on it); the waiting
/// area that holds the Battlefield Transporter arrival (-104,96) spans x -140..-53,
/// z 57..136 (5,755 cells). Their boxes overlap, but the line x - z = -114 separates
/// them with about ten units to spare on each side: every combat cell has
/// x - z &gt; -105 and every waiting cell has x - z &lt;= -124. The rule and the box
/// below are therefore DERIVED from the map file, not captured from a server.
/// </para>
/// </remarks>
internal static class DuelArenaCombatArea
{
    public const byte MapId = DuelArenaCapturedLayout.MapId;
    public const float SeparationLine = -114f;
    public const float MinimumX = -95f;
    public const float MaximumX = 25f;
    public const float MinimumZ = -32f;
    public const float MaximumZ = 86f;

    public static bool Contains(int mapId, float x, float z) =>
        mapId == MapId &&
        float.IsFinite(x) &&
        float.IsFinite(z) &&
        x - z > SeparationLine &&
        x >= MinimumX && x <= MaximumX &&
        z >= MinimumZ && z <= MaximumZ;

    /// <summary>
    /// GENERATED: an arena death revives in the waiting area, at the point the
    /// Battlefield Transporter lands players on (the captured upper arrival), instead
    /// of the faction capital fallback. The IGG page does not say where a defeated
    /// duellist goes; the GodsArena wiki only notes an "arena respawn fix".
    /// </summary>
    public static (byte MapId, float X, float Z) ReviveLanding =>
        (MapId, DuelArenaCapturedLayout.UpperArrivalX,
            DuelArenaCapturedLayout.UpperArrivalZ);
}
