using System.IO.Compression;

namespace Godswar.Server.Game;

/// <summary>Native quarter-unit collision cells with a conservative one-unit route graph.</summary>
internal sealed class MonsterNavigationGrid
{
    private readonly byte[] _cells;
    private readonly bool[] _nodes;
    private readonly int _width, _height, _nodeWidth, _nodeHeight;
    private readonly float _minX, _minZ;
    public static MonsterNavigationGrid WonderlandFinalIsland { get; } = LoadWonderland();

    private MonsterNavigationGrid(int minX, int minZ, int width, int height, byte[] cells)
    {
        _minX = minX;
        _minZ = minZ;
        _width = width;
        _height = height;
        _cells = cells;
        _nodeWidth = width / 4;
        _nodeHeight = height / 4;
        _nodes = new bool[_nodeWidth * _nodeHeight];
        for (var z = 0; z < _nodeHeight; z++)
        for (var x = 0; x < _nodeWidth; x++)
        {
            var clear = true;
            for (var dz = 0; dz < 4; dz++)
            for (var dx = 0; dx < 4; dx++) clear &= Cell(x * 4 + dx, z * 4 + dz);
            _nodes[z * _nodeWidth + x] = clear;
        }
    }

    private static MonsterNavigationGrid LoadWonderland()
    {
        using var resource = typeof(MonsterNavigationGrid).Assembly.GetManifestResourceStream(
            "Godswar.Server.Game.Navigation.WonderlandFinalIsland.grid.gz") ??
            throw new InvalidDataException("Wonderland navigation resource is missing.");
        using var compressed = new GZipStream(resource, CompressionMode.Decompress);
        using var reader = new BinaryReader(compressed);
        if (reader.ReadUInt32() != 0x31564e57) throw new InvalidDataException("Invalid navigation grid.");
        var x = reader.ReadInt32();
        var z = reader.ReadInt32();
        var width = reader.ReadInt32();
        var height = reader.ReadInt32();
        if (x != -36 || z != -124 || width != 432 || height != 912)
            throw new InvalidDataException("Invalid Wonderland navigation bounds.");
        var cells = reader.ReadBytes(width * height);
        if (cells.Length != width * height || cells.Any(value => value > 1) || compressed.ReadByte() != -1)
            throw new InvalidDataException("Invalid Wonderland navigation cells.");
        return new(x, z, width, height, cells);
    }

    private bool Cell(int x, int z) => x >= 0 && z >= 0 && x < _width && z < _height &&
        _cells[z * _width + x] != 0;

    public bool IsWalkable(float x, float z) => float.IsFinite(x) && float.IsFinite(z) &&
        Cell((int)MathF.Floor((x - _minX) * 4), (int)MathF.Floor((z - _minZ) * 4));

    // Supercover traversal checks every crossed native cell, including both sides of a corner.
    public bool CanTraverse(float x, float z, float targetX, float targetZ)
    {
        if (!IsWalkable(x, z) || !IsWalkable(targetX, targetZ)) return false;
        double startX = (x - _minX) * 4d, startZ = (z - _minZ) * 4d;
        double dx = (targetX - x) * 4d, dz = (targetZ - z) * 4d;
        var cx = (int)Math.Floor(startX);
        var cz = (int)Math.Floor(startZ);
        var endX = (int)Math.Floor((targetX - _minX) * 4d);
        var endZ = (int)Math.Floor((targetZ - _minZ) * 4d);
        int stepX = Math.Sign(dx), stepZ = Math.Sign(dz);
        var deltaX = stepX == 0 ? double.PositiveInfinity : 1 / Math.Abs(dx);
        var deltaZ = stepZ == 0 ? double.PositiveInfinity : 1 / Math.Abs(dz);
        var nextX = stepX == 0 ? double.PositiveInfinity :
            (stepX > 0 ? cx + 1 - startX : startX - cx) / Math.Abs(dx);
        var nextZ = stepZ == 0 ? double.PositiveInfinity :
            (stepZ > 0 ? cz + 1 - startZ : startZ - cz) / Math.Abs(dz);
        while (cx != endX || cz != endZ)
        {
            // A negative-direction ray ending on a cell boundary remains in the
            // endpoint's floor cell. Do not step beyond the destination at t=1.
            if (Math.Min(nextX, nextZ) >= 1d - 1e-10) return true;
            if (Math.Abs(nextX - nextZ) < 1e-9)
            {
                if (!Cell(cx + stepX, cz) || !Cell(cx, cz + stepZ)) return false;
                cx += stepX; cz += stepZ; nextX += deltaX; nextZ += deltaZ;
            }
            else if (nextX < nextZ) { cx += stepX; nextX += deltaX; }
            else { cz += stepZ; nextZ += deltaZ; }
            if (!Cell(cx, cz)) return false;
        }
        return true;
    }

    public List<(float X, float Z)> FindPath(float x, float z, float targetX, float targetZ)
    {
        if (!IsWalkable(x, z) || !IsWalkable(targetX, targetZ)) return [];
        var start = NearestNode(x, z);
        var goal = NearestNode(targetX, targetZ);
        if (start < 0 || goal < 0) return [];
        var frontier = new PriorityQueue<int, float>();
        var costs = new Dictionary<int, float> { [start] = 0 };
        var parents = new Dictionary<int, int>();
        frontier.Enqueue(start, 0);
        while (frontier.TryDequeue(out var node, out _))
        {
            if (node == goal)
            {
                var path = new List<(float X, float Z)> { (targetX, targetZ), Point(goal) };
                while (parents.TryGetValue(node, out var parent)) { node = parent; path.Add(Point(node)); }
                path.Reverse();
                return path;
            }
            var nx = node % _nodeWidth;
            var nz = node / _nodeWidth;
            for (var dz = -1; dz <= 1; dz++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dz == 0 || !Node(nx + dx, nz + dz) ||
                    dx != 0 && dz != 0 && (!Node(nx + dx, nz) || !Node(nx, nz + dz))) continue;
                var next = (nz + dz) * _nodeWidth + nx + dx;
                var cost = costs[node] + (dx != 0 && dz != 0 ? 1.41421356f : 1f);
                if (costs.TryGetValue(next, out var old) && old <= cost) continue;
                costs[next] = cost;
                parents[next] = node;
                var point = Point(next);
                frontier.Enqueue(next, cost + MathF.Sqrt((point.X - targetX) * (point.X - targetX) +
                    (point.Z - targetZ) * (point.Z - targetZ)));
            }
        }
        return [];
    }

    private bool Node(int x, int z) => x >= 0 && z >= 0 && x < _nodeWidth && z < _nodeHeight &&
        _nodes[z * _nodeWidth + x];
    private (float X, float Z) Point(int node) =>
        (_minX + node % _nodeWidth + .5f, _minZ + node / _nodeWidth + .5f);

    private int NearestNode(float x, float z)
    {
        var cx = (int)MathF.Floor(x - _minX);
        var cz = (int)MathF.Floor(z - _minZ);
        var best = -1;
        var distance = float.PositiveInfinity;
        for (var dz = -2; dz <= 2; dz++)
        for (var dx = -2; dx <= 2; dx++)
        {
            if (!Node(cx + dx, cz + dz)) continue;
            var node = (cz + dz) * _nodeWidth + cx + dx;
            var point = Point(node);
            var squared = (point.X - x) * (point.X - x) + (point.Z - z) * (point.Z - z);
            if (squared >= distance || !CanTraverse(x, z, point.X, point.Z)) continue;
            best = node;
            distance = squared;
        }
        return best;
    }
}
