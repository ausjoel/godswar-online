using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.WorldInstances;

internal static class AtlantisEncounterPolicy
{
    // Requested encounter rules from https://godswar.online/node/121.
    // Admission levels, daily allowances, and payments retain their own policies.
    public static readonly TimeSpan TimeLimit = TimeSpan.FromMinutes(40);
    public static readonly MapId ContentMapId = new(205);
    public const int CompletionTeamPoints = 850;

    public static bool IsAtlantisInstance(WorldInstanceDescriptor descriptor) =>
        descriptor.Kind == InstanceKind.Dungeon && descriptor.MapId == ContentMapId;

    public static bool TryGetKillPoints(AtlantisMonsterRank rank, out int points)
    {
        points = rank switch
        {
            AtlantisMonsterRank.Normal => 1,
            AtlantisMonsterRank.Elite => 10,
            AtlantisMonsterRank.Boss => 50,
            _ => 0
        };
        return points != 0;
    }

    public static bool TryParseMonsterRank(string? rank, out AtlantisMonsterRank parsed)
    {
        parsed = rank?.ToLowerInvariant() switch
        {
            "normal" => AtlantisMonsterRank.Normal,
            "elite" => AtlantisMonsterRank.Elite,
            "boss" => AtlantisMonsterRank.Boss,
            _ => default
        };
        return TryGetKillPoints(parsed, out _);
    }
}
