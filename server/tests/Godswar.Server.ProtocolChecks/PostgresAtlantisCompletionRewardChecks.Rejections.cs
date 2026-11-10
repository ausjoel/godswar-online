using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.WorldInstances;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresAtlantisCompletionRewardChecks
{
    private static async Task CheckConflictingEvidenceAsync(NpgsqlDataSource source)
    {
        var fixture = await CreateFixtureAsync(source, 2);
        var store = new PostgresAtlantisCompletionRewardStore(source);
        Check.True((await store.SettleAsync(fixture.Request)).Succeeded, "conflict fixture first settles successfully");
        var expected = await ReadCharactersAsync(source, fixture);
        var counts = await ReadRewardCountsAsync(source, fixture);
        foreach (var altered in new[]
        {
            CopyRequest(fixture.Request, completed: fixture.Request.CompletedAtUtc.AddTicks(1)),
            CopyRequest(fixture.Request, instance: WorldInstanceId.New()),
            CopyRequest(fixture.Request, reservation: Guid.NewGuid()),
            CopyRequest(fixture.Request, finishers: [fixture.Request.FrozenMembers[0]])
        })
        {
            Check.True((await store.SettleAsync(altered)).Status == AtlantisCompletionRewardStatus.RequestConflict &&
                (await ReadCharactersAsync(source, fixture)).SequenceEqual(expected) &&
                await ReadRewardCountsAsync(source, fixture) == counts,
                "changed completion evidence or reused admission cannot produce another award");
        }

        var raced = await CreateFixtureAsync(source, 2);
        var outcomes = await Task.WhenAll(
            new PostgresAtlantisCompletionRewardStore(source).SettleAsync(raced.Request),
            new PostgresAtlantisCompletionRewardStore(source).SettleAsync(
                CopyRequest(raced.Request, instance: WorldInstanceId.New())));
        Check.True(outcomes.Count(result => result.Status == AtlantisCompletionRewardStatus.Applied) == 1 &&
            outcomes.Count(result => result.Status == AtlantisCompletionRewardStatus.RequestConflict) == 1 &&
            await ReadRewardCountsAsync(source, raced) == new RewardRowCounts(1, 2, 2),
            "two runtime identities cannot concurrently redeem the same original admission");
    }

    private static async Task CheckAdmissionRejectionsAsync(NpgsqlDataSource source)
    {
        var mutations = new[]
        {
            "DELETE FROM public.legacy_instance_daily_entries WHERE reservation_id=@reservation AND character_id=@character;",
            "UPDATE public.legacy_instance_daily_entries SET admitted_at=NULL WHERE reservation_id=@reservation AND character_id=@character;",
            "UPDATE public.legacy_instance_daily_entries SET instance_kind=2 WHERE reservation_id=@reservation AND character_id=@character;",
            "UPDATE public.legacy_instance_daily_entries SET realm_id=2 WHERE reservation_id=@reservation AND character_id=@character;"
        };
        foreach (var sql in mutations)
        {
            var fixture = await CreateFixtureAsync(source, 2);
            await UpdateFixtureAsync(source, fixture, sql, memberIndex: 1);
            var before = await ReadCharactersAsync(source, fixture);
            var rejected = await new PostgresAtlantisCompletionRewardStore(source).SettleAsync(fixture.Request);
            Check.True(rejected.Status == AtlantisCompletionRewardStatus.AdmissionConflict,
                "missing, pending, wrong-kind, or wrong-realm original admission refuses completion credit");
            await AssertNoRewardAsync(source, fixture, before, "unproven complete admission");
        }
        var extra = await CreateFixtureAsync(source, 2);
        var unrelated = await CreateFixtureAsync(source, 1);
        await using (var change = source.CreateCommand(
            "UPDATE public.legacy_instance_daily_entries SET reservation_id=@target WHERE reservation_id=@previous;"))
        {
            change.Parameters.AddWithValue("target", extra.Request.AdmissionReservationId);
            change.Parameters.AddWithValue("previous", unrelated.Request.AdmissionReservationId);
            Check.Equal(1, await change.ExecuteNonQueryAsync(), "add one unreported member to the original admission");
        }
        var expected = await ReadCharactersAsync(source, extra);
        Check.True((await new PostgresAtlantisCompletionRewardStore(source).SettleAsync(extra.Request)).Status ==
            AtlantisCompletionRewardStatus.AdmissionConflict, "the request cannot omit an original reservation member");
        await AssertNoRewardAsync(source, extra, expected, "extra original admission member");

        var admitted = await CreateFixtureAsync(source, 2);
        var unusedClaim = await CreateFixtureAsync(source, 1);
        var unusedBefore = await ReadCharactersAsync(source, unusedClaim);
        await using (var pending = source.CreateCommand(
            """
            UPDATE public.legacy_instance_daily_entries SET reservation_id=@target,admitted_at=NULL
            WHERE reservation_id=@previous;
            """))
        {
            pending.Parameters.AddWithValue("target", admitted.Request.AdmissionReservationId);
            pending.Parameters.AddWithValue("previous", unusedClaim.Request.AdmissionReservationId);
            Check.Equal(1, await pending.ExecuteNonQueryAsync(), "one reserved member never completed its transfer");
        }
        Check.True((await new PostgresAtlantisCompletionRewardStore(source).SettleAsync(admitted.Request)).Status ==
            AtlantisCompletionRewardStatus.Applied &&
            await ReadRewardCountsAsync(source, admitted) == new RewardRowCounts(1, 2, 2),
            "an unused pending claim does not prevent the successfully admitted roster's completion award");
        await AssertNoRewardAsync(source, unusedClaim, unusedBefore, "unadmitted pending claimant");

        var missing = await CreateFixtureAsync(source, 1);
        expected = await ReadCharactersAsync(source, missing);
        Check.True((await new PostgresAtlantisCompletionRewardStore(source).SettleAsync(
            CopyRequest(missing.Request, reservation: Guid.NewGuid()))).Status == AtlantisCompletionRewardStatus.AdmissionConflict,
            "an invented reservation cannot authorize completion rewards");
        await AssertNoRewardAsync(source, missing, expected, "missing original reservation");
    }

    private static async Task CheckUnavailableAndOverflowAsync(NpgsqlDataSource source)
    {
        foreach (var mutation in new[]
        {
            "UPDATE public.character_base SET medusa_honor_points=2147480848 WHERE id=@character;",
            "UPDATE public.character_base SET medusa_reward_revision=9223372036854775807 WHERE id=@character;",
            """
            UPDATE public.character_base SET lifecycle_state='deleted',
                deleted_at=clock_timestamp(),restore_until=clock_timestamp()+interval '1 day',
                purge_after=clock_timestamp()+interval '2 days' WHERE id=@character;
            """
        })
        {
            var fixture = await CreateFixtureAsync(source, 2);
            await UpdateFixtureAsync(source, fixture, mutation, memberIndex: 1);
            var before = await ReadCharactersAsync(source, fixture);
            Check.True((await new PostgresAtlantisCompletionRewardStore(source).SettleAsync(fixture.Request)).Status ==
                AtlantisCompletionRewardStatus.CharacterUnavailable,
                "one overflowing or inactive member prevents the entire party award");
            await AssertNoRewardAsync(source, fixture, before, "invalid party member state");
        }
        var mismatched = await CreateFixtureAsync(source, 2);
        var swappedAccounts = mismatched.Request.AdmittedMembers.Select((member, index) =>
            member with { AccountId = mismatched.Request.AdmittedMembers[1 - index].AccountId }).ToArray();
        var expected = await ReadCharactersAsync(source, mismatched);
        Check.True((await new PostgresAtlantisCompletionRewardStore(source).SettleAsync(
            CopyRequest(mismatched.Request, admitted: swappedAccounts, finishers: swappedAccounts))).Status ==
            AtlantisCompletionRewardStatus.CharacterUnavailable,
            "the original character IDs cannot award value under different account identities");
        await AssertNoRewardAsync(source, mismatched, expected, "account identity mismatch");
        Check.True((await new PostgresAtlantisCompletionRewardStore(source).SettleAsync(
            CopyRequest(mismatched.Request, realm: new RealmId(2)))).Status == AtlantisCompletionRewardStatus.CharacterUnavailable,
            "the same admitted character IDs cannot be redeemed under another realm");
        await AssertNoRewardAsync(source, mismatched, expected, "cross-realm completion request");

        var ceiling = await CreateFixtureAsync(source, 1);
        await UpdateFixtureAsync(source, ceiling,
            "UPDATE public.character_base SET medusa_honor_points=2147480847 WHERE id=@character;");
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            new PostgresAtlantisCompletionRewardStore(source).SettleAsync(ceiling.Request)));
        Check.True(results.Count(result => result.Status == AtlantisCompletionRewardStatus.Applied) == 1 &&
            results.Count(result => result.Status == AtlantisCompletionRewardStatus.Duplicate) == 2 &&
            (await ReadCharactersAsync(source, ceiling)).Single().Honor == int.MaxValue,
            "exactly representable last2800 points commit once and replay even though no further balance room remains");
    }

    private static async Task CheckMidPartyRollbackAsync(NpgsqlDataSource source)
    {
        var fixture = await CreateFixtureAsync(source, 2);
        var before = await ReadCharactersAsync(source, fixture);
        var token = Guid.NewGuid().ToString("N")[..12];
        var function = $"atlantis_reward_fail_{token}";
        var trigger = $"atlantis_reward_guard_{token}";
        var lastCharacter = fixture.Request.AdmittedCharacterIds.Max();
        await using (var install = source.CreateCommand($"""
            CREATE FUNCTION public.{function}() RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN
                IF NEW.id={lastCharacter} THEN
                    RAISE EXCEPTION 'Atlantis completion rollback fixture' USING ERRCODE='P0001';
                END IF;
                RETURN NEW;
            END;
            $body$;
            CREATE TRIGGER {trigger} BEFORE UPDATE OF medusa_honor_points ON public.character_base
                FOR EACH ROW EXECUTE FUNCTION public.{function}();
            """))
        {
            await install.ExecuteNonQueryAsync();
        }
        try
        {
            var failed = false;
            try
            {
                await new PostgresAtlantisCompletionRewardStore(source).SettleAsync(fixture.Request);
            }
            catch (PostgresException error) when (error.SqlState == "P0001")
            {
                failed = true;
            }
            Check.True(failed, "the second paid party member triggers a real transaction failure");
            await AssertNoRewardAsync(source, fixture, before, "mid-party transaction rollback");
        }
        finally
        {
            await using var cleanup = source.CreateCommand($"""
                DROP TRIGGER IF EXISTS {trigger} ON public.character_base;
                DROP FUNCTION IF EXISTS public.{function}();
                """);
            await cleanup.ExecuteNonQueryAsync();
        }
        var recovered = await new PostgresAtlantisCompletionRewardStore(source).SettleAsync(fixture.Request);
        Check.True(recovered.Status == AtlantisCompletionRewardStatus.Applied &&
            recovered.Members.Count == 2 && await ReadRewardCountsAsync(source, fixture) == new RewardRowCounts(1, 2, 2),
            "retry after rollback credits the original completion once without poisoned receipt or title rows");
    }
}
