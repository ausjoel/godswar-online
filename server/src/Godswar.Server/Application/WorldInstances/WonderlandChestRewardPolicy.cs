using System.Collections.Immutable;
using Godswar.Server.Domain.Characters;

namespace Godswar.Server.Application.WorldInstances;

internal static class WonderlandChestRewardPolicy
{
    public const string Revision = "wonderland-native-sacks-v1";
    public const short StackCap = 99;

    public static ImmutableArray<WonderlandChestItemReward> Resolve(int island, byte partyCamp)
    {
        if (partyCamp is not (FactionPortalSkillPolicy.SpartaCamp or FactionPortalSkillPolicy.AthensCamp))
            throw new ArgumentOutOfRangeException(nameof(partyCamp));
        return island switch
        {
            1 => [Sack(4450)],
            2 => [Sack(4451)],
            3 => [Sack(4452)],
            4 => [Sack(4453)],
            5 => [Sack(partyCamp == FactionPortalSkillPolicy.SpartaCamp ? 4455u : 4454u)],
            6 => [Sack(4456)],
            7 => [Sack(4457)],
            8 => [Sack(4458), Sack(4459), Sack(4460), Sack(4461)],
            _ => throw new ArgumentOutOfRangeException(nameof(island))
        };
    }

    private static WonderlandChestItemReward Sack(uint id) => new(id, 1, 1);
}
