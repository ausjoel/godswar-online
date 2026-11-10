namespace Godswar.Server.Game;

/// <summary>Per-actor route cache. Every advertised movement segment remains collision checked.</summary>
internal sealed class MonsterNavigation(MonsterNavigationGrid grid)
{
    private List<(float X, float Z)> _route = [];
    private int _waypoint;
    private float _goalX, _goalZ;

    public bool CanTraverse(float x, float z, float targetX, float targetZ) =>
        grid.CanTraverse(x, z, targetX, targetZ);

    public bool TryGetStep(float x, float z, float targetX, float targetZ, float maxStep,
        out float dx, out float dz)
    {
        dx = dz = 0;
        if (!float.IsFinite(maxStep) || maxStep <= 0) return false;
        var next = (X: targetX, Z: targetZ);
        if (!CanTraverse(x, z, targetX, targetZ))
        {
            if (_waypoint >= _route.Count || DistanceSquared(_goalX, _goalZ, targetX, targetZ) > 1f ||
                !CanTraverse(x, z, _route[_waypoint].X, _route[_waypoint].Z))
            {
                _route = grid.FindPath(x, z, targetX, targetZ);
                _waypoint = 0;
                _goalX = targetX; _goalZ = targetZ;
            }
            if (_route.Count == 0) return false;
            // Skip visible route nodes without cutting corners across the collision grid.
            while (_waypoint + 1 < _route.Count &&
                CanTraverse(x, z, _route[_waypoint + 1].X, _route[_waypoint + 1].Z)) _waypoint++;
            next = _route[_waypoint];
        }
        var length = MathF.Sqrt(DistanceSquared(x, z, next.X, next.Z));
        if (length <= .0001f) return false;
        var scale = Math.Min(maxStep, length) / length;
        dx = (next.X - x) * scale;
        dz = (next.Z - z) * scale;
        if (CanTraverse(x, z, x + dx, z + dz)) return true;
        dx = dz = 0;
        return false;
    }

    private static float DistanceSquared(float x, float z, float targetX, float targetZ) =>
        (targetX - x) * (targetX - x) + (targetZ - z) * (targetZ - z);
}
