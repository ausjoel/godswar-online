using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class MedusaRewardProjectionRevisionChecks
{
    public const string CheckName = "Medusa reward projection preserves newer manual title selection";

    public static Task RunAsync()
    {
        var character = new GameCharacter
        {
            Id = 93001,
            AccountId = 9301,
            Name = "TitleRevision",
            MedusaHonorPoints = 9900,
            MedusaRewardRevision = 7,
            SelectedTitleId = AtlantisCompletionRewardPolicy.SeabedExplorerTitleId,
            OwnedTitleIds = [AtlantisCompletionRewardPolicy.SeabedExplorerTitleId]
        };
        var delayedReward = new MedusaCompletionRewardMember(character.Id, character.Camp,
            HonorBefore: 3200, HonorAfter: 6000, RewardRevision: 6, AwardedTitleId: 5152);

        GameSessionRegistry.ApplyMedusaRewardProjection(character, delayedReward);
        AssertNewerChoiceRetained(character);
        Check.True(character.OwnedTitleIds.Order().SequenceEqual(new uint[] { 5013, 5152 }),
            "a delayed completion still merges its durably earned title into ownership");
        GameSessionRegistry.ApplyMedusaRewardProjection(character, delayedReward);
        AssertNewerChoiceRetained(character);
        Check.Equal(2, character.OwnedTitleIds.Length,
            "replaying the stale receipt cannot duplicate title ownership");

        var currentReward = delayedReward with
        {
            HonorBefore = 9900,
            HonorAfter = 12700,
            RewardRevision = 8,
            AwardedTitleId = 5011
        };
        GameSessionRegistry.ApplyMedusaRewardProjection(character, currentReward);
        Check.True(character.MedusaHonorPoints == 12700 && character.MedusaRewardRevision == 8 &&
            character.SelectedTitleId == 5011 && character.OwnedTitleIds.Contains(5011u),
            "a newer Medusa receipt preserves the existing reward and automatic selection behavior");
        GameSessionRegistry.ApplyMedusaRewardProjection(character, currentReward);
        Check.True(character.MedusaHonorPoints == 12700 && character.MedusaRewardRevision == 8 &&
            character.SelectedTitleId == 5011 && character.OwnedTitleIds.Length == 3,
            "an equal-revision Medusa receipt is an idempotent projection of the same committed write");

        character.SelectedTitleId = 0;
        character.MedusaRewardRevision = 9;
        GameSessionRegistry.ApplyMedusaRewardProjection(character, currentReward);
        Check.True(character.SelectedTitleId == 0 && character.MedusaRewardRevision == 9 &&
            character.MedusaHonorPoints == 12700,
            "a later explicit hide-title choice survives an older completion receipt");
        GameSessionRegistry.ApplyMedusaRewardProjection(character, currentReward with
        {
            HonorBefore = 12700,
            HonorAfter = 15500,
            RewardRevision = 10,
            AwardedTitleId = 0
        });
        Check.True(character.MedusaHonorPoints == 15500 && character.MedusaRewardRevision == 10 &&
            character.SelectedTitleId == 0 && character.OwnedTitleIds.Length == 3,
            "a current reward without a title updates the wallet while retaining the display choice");
        return Task.CompletedTask;
    }

    private static void AssertNewerChoiceRetained(GameCharacter character) =>
        Check.True(character.MedusaHonorPoints == 9900 && character.MedusaRewardRevision == 7 &&
            character.SelectedTitleId == AtlantisCompletionRewardPolicy.SeabedExplorerTitleId,
            "an older completion receipt preserves the newer manual selection, wallet, and revision");
}
