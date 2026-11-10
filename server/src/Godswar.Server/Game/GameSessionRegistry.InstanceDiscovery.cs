using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game;

/// <summary>One running world instance as a GM sees it.</summary>
internal sealed record OperatorInstanceView(
    InstanceKind Kind,
    byte MapId,
    WorldInstanceId Id,
    IReadOnlyList<string> Players)
{
    public string Label =>
        $"{Kind} map {MapId} #{Id.Value.ToString("N")[..8]}";
}

internal sealed partial class GameSessionRegistry
{
    internal OperatorInstanceView? DescribeWorldInstance(GameSessionContext context) =>
        TryGetWorldInstance(context, out var instance)
            ? new OperatorInstanceView(
                instance.Kind,
                context.MapId,
                context.WorldInstanceId,
                [context.CharacterName])
            : null;

    internal WorldInstanceId GetDefaultWorldInstanceId(byte mapId) =>
        GetOrCreateDefaultWorldInstance(mapId).InstanceId;

    internal IReadOnlyList<OperatorInstanceView> SnapshotOccupiedWorldInstances()
    {
        var views = new List<OperatorInstanceView>();
        foreach (var group in SnapshotWorldReadySessions()
                     .GroupBy(static context => context.WorldInstanceId))
        {
            var first = group.First();
            if (DescribeWorldInstance(first) is { } view)
            {
                views.Add(view with
                {
                    Players = group
                        .Select(static context => context.CharacterName)
                        .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                });
            }
        }

        return views;
    }
}
