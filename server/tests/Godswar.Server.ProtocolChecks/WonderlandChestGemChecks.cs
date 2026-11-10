using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static class WonderlandChestGemChecks
{
    public const string CheckName = "Wonderland treasure boxes give four Grade IV gems independently of boss sacks";

    public static Task RunAsync()
    {
        var frequencies = new int[5];
        for (var roll = 0; roll < 16; roll++)
        {
            var rewards = WonderlandChestGemRewardPolicy.Resolve(roll);
            Check.True(rewards.Sum(item => item.Quantity) == 4 && rewards.All(item =>
                item.ItemId is 4213 or 4223 && item.Quantity is >= 1 and <= 4 && item.Bound == 1),
                "each roll gives exactly four bound Sapphire IV / Emerald IV gems and no boss sack");
            var sapphires = rewards.Where(item => item.ItemId == 4213).Sum(item => item.Quantity);
            frequencies[sapphires]++;
        }
        Check.True(frequencies.SequenceEqual(new[] { 1, 4, 6, 4, 1 }),
            "all sixteen outcomes implement four independent equal Sapphire/Emerald choices");
        Check.True(WonderlandChestGemRewardPolicy.PossibleRewards().Count() == 5 &&
            WonderlandChestGemRewardPolicy.PossibleRewards().All(mix => mix.Sum(item => item.Quantity) == 4),
            "capacity preflight enumerates every possible single chest outcome");
        Check.Throws<ArgumentOutOfRangeException>(() => WonderlandChestGemRewardPolicy.Resolve(-1), "negative RNG input rejects");
        Check.Throws<ArgumentOutOfRangeException>(() => WonderlandChestGemRewardPolicy.Resolve(16), "out-of-range RNG input rejects");
        return Task.CompletedTask;
    }
}
