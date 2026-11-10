using System.Security.Cryptography;

namespace Godswar.Server.Application.WorldInstances;

internal readonly record struct WonderlandSackReward(uint ItemId, short Quantity, int Weight);

internal static class WonderlandSackRewardPolicy
{
    public const string Revision = "wonderland-sack-rewards-v1";
    private static readonly WonderlandSackReward[] Early =
    [new(10133, 25, 10), new(4174, 25, 15), new(10133, 10, 25), new(10133, 5, 30), new(10107, 1, 2)];
    private static readonly WonderlandSackReward[] Late =
    [new(10134, 10, 20), new(10133, 50, 20), new(10134, 20, 10), new(10107, 1, 10)];
    private static readonly WonderlandSackReward[] Scorpion =
    [new(10134, 99, 50), new(10107, 3, 50), new(4213, 1, 50), new(4223, 1, 50)];

    public static bool IsSack(uint itemId) => itemId is >= 4450 and <= 4461;

    public static IReadOnlyList<WonderlandSackReward> Outcomes(uint itemId) => itemId switch
    {
        >= 4450 and <= 4455 => Array.AsReadOnly(Early),
        >= 4456 and <= 4460 => Array.AsReadOnly(Late),
        4461 => Array.AsReadOnly(Scorpion),
        _ => throw new ArgumentOutOfRangeException(nameof(itemId))
    };

    public static int TotalWeight(uint itemId) => Outcomes(itemId).Sum(outcome => outcome.Weight);

    public static int SelectIndex(uint itemId, int roll)
    {
        var outcomes = Outcomes(itemId);
        if (roll < 0 || roll >= outcomes.Sum(outcome => outcome.Weight))
            throw new ArgumentOutOfRangeException(nameof(roll));
        for (var index = 0; index < outcomes.Count; index++)
        {
            if (roll < outcomes[index].Weight) return index;
            roll -= outcomes[index].Weight;
        }
        throw new InvalidOperationException("Wonderland sack weights have no selected outcome.");
    }
}

internal interface IWonderlandSackRollSource
{
    int NextRoll(int exclusiveMaximum);
}

internal sealed class CryptographicWonderlandSackRollSource : IWonderlandSackRollSource
{
    public static readonly CryptographicWonderlandSackRollSource Instance = new();
    public int NextRoll(int exclusiveMaximum) => RandomNumberGenerator.GetInt32(exclusiveMaximum);
}

internal sealed record WonderlandSackOpenEvidence(
    long SourceItemInstanceId,
    uint SourceTemplateId,
    int KitBagSlot,
    string RewardPolicyRevision,
    string ItemContentRevision,
    int Roll,
    int TotalWeight,
    int OutcomeIndex,
    uint RewardItemId,
    short RewardQuantity,
    short RewardBound)
{
    public bool IsValid => SourceItemInstanceId > 0 &&
        WonderlandSackRewardPolicy.IsSack(SourceTemplateId) && KitBagSlot is >= 0 and < 96 &&
        RewardPolicyRevision == WonderlandSackRewardPolicy.Revision &&
        ItemContentRevision is { Length: 64 } && ItemContentRevision.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F') &&
        TotalWeight == WonderlandSackRewardPolicy.TotalWeight(SourceTemplateId) &&
        Roll >= 0 && Roll < TotalWeight &&
        OutcomeIndex == WonderlandSackRewardPolicy.SelectIndex(SourceTemplateId, Roll) &&
        RewardItemId == WonderlandSackRewardPolicy.Outcomes(SourceTemplateId)[OutcomeIndex].ItemId &&
        RewardQuantity == WonderlandSackRewardPolicy.Outcomes(SourceTemplateId)[OutcomeIndex].Quantity &&
        RewardBound is 0 or 1;
}
