using System.Numerics;
using System.Security.Cryptography;

namespace Godswar.Server.Application.WorldInstances;

internal static class WonderlandChestGemRewardPolicy
{
    public const string Revision = "wonderland-chest-gems-v1";
    public const uint SapphireIV = 4213;
    public const uint EmeraldIV = 4223;
    public const int GemCount = 4;

    // Four independent equal choices. The bit count gives the number of sapphires.
    public static IReadOnlyList<WonderlandChestItemReward> Resolve(int roll)
    {
        if (roll is < 0 or >= 16) throw new ArgumentOutOfRangeException(nameof(roll));
        var sapphires = BitOperations.PopCount((uint)roll);
        return FromCounts(sapphires);
    }

    public static IEnumerable<IReadOnlyList<WonderlandChestItemReward>> PossibleRewards() =>
        Enumerable.Range(0, GemCount + 1).Select(FromCounts);

    private static IReadOnlyList<WonderlandChestItemReward> FromCounts(int sapphires)
    {
        var result = new List<WonderlandChestItemReward>(2);
        if (sapphires > 0) result.Add(new(SapphireIV, checked((short)sapphires), 1));
        if (sapphires < GemCount) result.Add(new(EmeraldIV, checked((short)(GemCount - sapphires)), 1));
        return result;
    }
}

internal interface IWonderlandChestGemRollSource
{
    int NextRoll();
}

internal sealed class CryptographicWonderlandChestGemRollSource : IWonderlandChestGemRollSource
{
    public int NextRoll() => RandomNumberGenerator.GetInt32(16);
}
