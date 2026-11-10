namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// Authored random-entry pool on the lower Arena floor. Every point was
/// checked against Arena.hmp (08ED1AD3...63638537F24): it belongs to the
/// captured (-5,32) walkable component and clears blocked cells by >=2 units.
/// Only (-5,32) is externally captured; the remaining points implement the
/// requested random entry without asserting an external probability model.
/// </summary>
internal static class DuelArenaEntryDestinations
{
    public static IReadOnlyList<(float X, float Z)> All { get; } =
        Array.AsReadOnly<(float X, float Z)>(
        [
            (-5f, 32f),
            (-5f, 12f),
            (-17f, 16f),
            (-13f, 16f),
            (-9f, 16f),
            (-5f, 16f),
            (-1f, 16f),
            (3f, 16f),
            (7f, 16f),
            (-21f, 20f),
            (-17f, 20f),
            (-13f, 20f),
            (-9f, 20f),
            (-5f, 20f),
            (-1f, 20f),
            (3f, 20f),
            (7f, 20f),
            (11f, 20f),
            (-21f, 24f),
            (-17f, 24f),
            (-13f, 24f),
            (-9f, 24f),
            (-5f, 24f),
            (-1f, 24f),
            (3f, 24f),
            (7f, 24f),
            (11f, 24f),
            (-21f, 28f),
            (-17f, 28f),
            (-13f, 28f),
            (-9f, 28f),
            (-5f, 28f),
            (-1f, 28f),
            (3f, 28f),
            (7f, 28f),
            (11f, 28f),
            (-25f, 32f),
            (-21f, 32f),
            (-17f, 32f),
            (-13f, 32f),
            (-9f, 32f),
            (-1f, 32f),
            (3f, 32f),
            (7f, 32f),
            (11f, 32f),
            (15f, 32f),
            (-21f, 36f),
            (-17f, 36f),
            (-13f, 36f),
            (-9f, 36f),
            (-5f, 36f),
            (-1f, 36f),
            (3f, 36f),
            (7f, 36f),
            (11f, 36f),
            (-21f, 40f),
            (-9f, 40f),
            (-5f, 40f),
            (-1f, 40f),
            (3f, 40f),
            (7f, 40f),
            (11f, 40f),
            (-21f, 44f),
            (-9f, 44f),
            (-5f, 44f),
            (-1f, 44f),
            (3f, 44f),
            (7f, 44f),
            (11f, 44f),
            (-17f, 48f),
            (-13f, 48f),
            (-9f, 48f),
            (-5f, 48f),
            (-1f, 48f),
            (3f, 48f),
            (7f, 48f),
            (-5f, 52f),
        ]);

    public static bool TryResolve(int index, out DuelArenaTransportDestination destination)
    {
        destination = null!;
        if ((uint)index >= (uint)All.Count)
        {
            return false;
        }
        var point = All[index];
        destination = new(DuelArenaSection.LowerArena, point.X, point.Z);
        return true;
    }
}
