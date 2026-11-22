namespace Godswar.Server.Application.World;

/// <summary>
/// The published gameplay map set: the stock maps 0-69 and 200-210, plus (B22 G2, d36) the GENERATED Aether Sanctum
/// (map 70, Application.WorldInstances.AetherSanctumPolicy) when its rows are authored. Nothing else.
/// </summary>
internal static class GameplayMapSet
{
    public static readonly IReadOnlySet<short> Stock = Enumerable.Range(0, 70)
        .Concat(Enumerable.Range(200, 11))
        .Select(static value => (short)value)
        .ToHashSet();

    public static bool IsValid(IEnumerable<short> mapIds)
    {
        var ids = mapIds.ToHashSet();
        ids.Remove(Godswar.Server.Application.WorldInstances.AetherSanctumPolicy.MapId); // B22 G2: optional
        return ids.SetEquals(Stock);
    }
}
