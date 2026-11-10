using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.WorldInstances;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresAtlantisCompletionRewardChecks
{
    private static async Task CheckTitleSelectionRejectionsAsync(NpgsqlDataSource source)
    {
        var fixture = await CreateFixtureAsync(source, 1);
        Check.True((await new PostgresAtlantisCompletionRewardStore(source).SettleAsync(fixture.Request)).Succeeded,
            "rejection fixture already owns a valid selectable Atlantis title");
        var request = await CreateTitleSelectionRequestAsync(source, fixture, 5014);
        var store = new PostgresCharacterTitleSelectionStore(source);
        var before = await ReadCharactersAsync(source, fixture);
        var counts = await ReadRewardCountsAsync(source, fixture);
        var rejectedRequests = new (CharacterTitleSelectionRequest Request, CharacterTitleSelectionStatus Status)[]
        {
            (request with { Subject = request.Subject with { AccountId = int.MaxValue } },
                CharacterTitleSelectionStatus.CharacterUnavailable),
            (request with { Subject = request.Subject with { CharacterId = int.MaxValue } },
                CharacterTitleSelectionStatus.CharacterUnavailable),
            (request with { RealmId = RealmId.Dwargon }, CharacterTitleSelectionStatus.CharacterUnavailable),
            (request with { RealmId = default }, CharacterTitleSelectionStatus.CharacterUnavailable),
            (request with { RealmId = new(short.MaxValue + 1) }, CharacterTitleSelectionStatus.CharacterUnavailable),
            (request with { Ownership = default }, CharacterTitleSelectionStatus.OwnershipLost),
            (request with { Ownership = request.Ownership with { OwnerId = Guid.NewGuid() } },
                CharacterTitleSelectionStatus.OwnershipLost),
            (request with { Ownership = request.Ownership with { Generation = request.Ownership.Generation + 1 } },
                CharacterTitleSelectionStatus.OwnershipLost),
            (request with { TitleId = 0, Ownership = default }, CharacterTitleSelectionStatus.OwnershipLost),
            (request with { TitleId = 5013 }, CharacterTitleSelectionStatus.TitleNotOwned),
            (request with { TitleId = uint.MaxValue }, CharacterTitleSelectionStatus.TitleNotOwned)
        };
        foreach (var rejected in rejectedRequests)
        {
            var receipt = await store.SelectAsync(rejected.Request);
            Check.True(receipt.Status == rejected.Status && !receipt.Succeeded &&
                receipt.OwnedTitleIds.Count == 0 && receipt.HonorPoints == 0 && receipt.RewardRevision == 0 &&
                (await ReadCharactersAsync(source, fixture)).SequenceEqual(before) &&
                await ReadRewardCountsAsync(source, fixture) == counts,
                "unowned, cross-account, cross-realm, out-of-range, and stale-owner requests reveal no wallet and change no state");
        }

        await UpdateFixtureAsync(source, fixture,
            "UPDATE public.character_base SET selected_title_id=5013 WHERE id=@character;");
        var corruptSelection = await ReadCharactersAsync(source, fixture);
        Check.True((await store.SelectAsync(request with { TitleId = 5013 })).Status ==
            CharacterTitleSelectionStatus.TitleNotOwned &&
            (await ReadCharactersAsync(source, fixture)).SequenceEqual(corruptSelection),
            "an existing selection is never accepted as evidence that the character owns an unearned title");
        Check.True((await store.SelectAsync(request with { TitleId = 0 })).Succeeded,
            "the current owner can always clear an unearned prior selection");

        await UpdateFixtureAsync(source, fixture,
            "UPDATE public.character_base SET medusa_reward_revision=9223372036854775807 WHERE id=@character;");
        var exhausted = await ReadCharactersAsync(source, fixture);
        Check.True((await store.SelectAsync(request)).Status == CharacterTitleSelectionStatus.RevisionExhausted &&
            (await ReadCharactersAsync(source, fixture)).SequenceEqual(exhausted),
            "a changed selection cannot overflow its shared reward revision");
        var same = await store.SelectAsync(request with { TitleId = 0 });
        Check.True(same.Status == CharacterTitleSelectionStatus.Unchanged && same.RewardRevision == long.MaxValue,
            "an authorized same-choice retry remains idempotent when the revision is exhausted");

        await UpdateFixtureAsync(source, fixture,
            """
            UPDATE public.character_base SET lifecycle_state='deleted', checkpoint_owner_id=NULL,
                deleted_at=clock_timestamp(),restore_until=clock_timestamp()+interval '1 day',
                purge_after=clock_timestamp()+interval '2 days' WHERE id=@character;
            """);
        var deleted = await ReadCharactersAsync(source, fixture);
        Check.True((await store.SelectAsync(request with { TitleId = 0 })).Status ==
            CharacterTitleSelectionStatus.OwnershipLost &&
            (await ReadCharactersAsync(source, fixture)).SequenceEqual(deleted) &&
            await ReadRewardCountsAsync(source, fixture) == counts,
            "a deleted character cannot even replay an unchanged choice using its formerly current session fence");
    }

    private static async Task CheckTitleSelectionRewardRaceAsync(NpgsqlDataSource source)
    {
        var fixture = await CreateFixtureAsync(source, 1);
        var medusa = await GrantSelectionMedusaTitleAsync(source, fixture);
        var request = await CreateTitleSelectionRequestAsync(source, fixture, 0);
        var before = (await ReadCharactersAsync(source, fixture)).Single();
        Check.True(before.SelectedTitle == medusa.Award.AwardedTitleId,
            "the compatibility fixture starts with the existing Medusa reward's selected title");
        var selectionTask = new PostgresCharacterTitleSelectionStore(source).SelectAsync(request);
        var rewardTask = new PostgresAtlantisCompletionRewardStore(source).SettleAsync(fixture.Request);
        await Task.WhenAll(selectionTask, rewardTask);
        var selected = await selectionTask;
        var reward = await rewardTask;
        var rewardedMember = reward.Members.Single();
        var after = (await ReadCharactersAsync(source, fixture)).Single();
        Check.True(selected.Status == CharacterTitleSelectionStatus.Applied &&
            reward.Status == AtlantisCompletionRewardStatus.Applied && after.SelectedTitle == 0 &&
            after.Honor == before.Honor + 2800 && after.Revision == before.Revision + 2 &&
            selected.RewardRevision != rewardedMember.RewardRevision &&
            Math.Max(selected.RewardRevision, rewardedMember.RewardRevision) == after.Revision &&
            selected.HonorPoints == (selected.RewardRevision > rewardedMember.RewardRevision ? after.Honor : before.Honor),
            "selection and a racing Atlantis reward serialize wallet snapshots without autoequipping or losing HardPoints");
        var latest = await new PostgresCharacterTitleSelectionStore(source).SelectAsync(request);
        Check.True(latest.Status == CharacterTitleSelectionStatus.Unchanged && latest.HonorPoints == after.Honor &&
            latest.RewardRevision == after.Revision && latest.OwnedTitleIds.SequenceEqual(new uint[] { 5011, 5014 }),
            "a post-race retry returns the complete current ownership and wallet even if the first selection preceded the reward");
        var switched = await new PostgresCharacterTitleSelectionStore(source).SelectAsync(request with { TitleId = 5014 });
        var replay = await new PostgresAtlantisCompletionRewardStore(source).SettleAsync(fixture.Request);
        var final = (await ReadCharactersAsync(source, fixture)).Single();
        Check.True(replay.Status == AtlantisCompletionRewardStatus.Duplicate && switched.Succeeded &&
            final.SelectedTitle == 5014 && final.Revision == switched.RewardRevision && final.Honor == after.Honor &&
            await ReadRewardCountsAsync(source, fixture) == new RewardRowCounts(1, 1, 1),
            "replaying the old reward after a newer manual choice cannot restore an older selection or duplicate value");
    }
}
