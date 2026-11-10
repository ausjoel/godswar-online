using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.WorldInstances;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresWonderlandTitleChecks
{
    private static async Task CheckRefusalsAsync(NpgsqlDataSource source)
    {
        foreach (var mutation in new[]
        {
            "DELETE FROM public.legacy_instance_daily_entries WHERE reservation_id=@reservation AND character_id=@character;",
            "UPDATE public.legacy_instance_daily_entries SET admitted_at=NULL WHERE reservation_id=@reservation AND character_id=@character;",
            "UPDATE public.legacy_instance_daily_entries SET instance_kind=1 WHERE reservation_id=@reservation AND character_id=@character;",
            "UPDATE public.legacy_instance_daily_entries SET realm_id=2 WHERE reservation_id=@reservation AND character_id=@character;"
        })
        {
            var request = await CreateAsync(source);
            await MutateAsync(source, request, mutation);
            var before = await StateAsync(source, request);
            Check.True((await new PostgresWonderlandTitleStore(source).SettleAsync(request)).Status ==
                WonderlandTitleStatus.AdmissionConflict && await StateAsync(source, request) == before,
                "absent, pending, wrong-kind, and wrong-realm admissions cannot grant a milestone title");
        }
        foreach (var mutation in new[]
        {
            "UPDATE public.character_base SET medusa_reward_revision=9223372036854775807 WHERE id=@character;",
            """
            UPDATE public.character_base SET lifecycle_state='deleted',lifecycle_version=lifecycle_version+1,
                checkpoint_owner_id=NULL,deleted_at=GREATEST(clock_timestamp(),"Register_time"),
                restore_until=GREATEST(clock_timestamp(),"Register_time")+interval '1 day',
                purge_after=GREATEST(clock_timestamp(),"Register_time")+interval '2 days' WHERE id=@character;
            """
        })
        {
            var request = await CreateAsync(source);
            await MutateAsync(source, request, mutation);
            var before = await StateAsync(source, request);
            Check.True((await new PostgresWonderlandTitleStore(source).SettleAsync(request)).Status ==
                WonderlandTitleStatus.CharacterUnavailable && await StateAsync(source, request) == before,
                "inactive or overflowing eligible characters cause no partial title grant or receipt");
        }
        var original = await CreateAsync(source);
        var store = new PostgresWonderlandTitleStore(source);
        var unchanged = await StateAsync(source, original);
        var wrongAccounts = original.AdmittedMembers.Select((member, index) =>
            member with { AccountId = original.AdmittedMembers[1 - index].AccountId }).ToArray();
        Check.True((await store.SettleAsync(Copy(original, admitted: wrongAccounts, eligible: wrongAccounts))).Status ==
            WonderlandTitleStatus.CharacterUnavailable &&
            (await store.SettleAsync(Copy(original, realm: RealmId.Dwargon))).Status == WonderlandTitleStatus.CharacterUnavailable &&
            await StateAsync(source, original) == unchanged, "same character IDs cannot redeem titles under another account or realm");
        Check.True((await store.SettleAsync(original)).Succeeded, "conflict fixture first settles");
        var committed = await StateAsync(source, original);
        foreach (var altered in new[]
        {
            Copy(original, cleared: original.ClearedAtUtc.AddTicks(1)),
            Copy(original, eligible: [original.FrozenMembers[0]]),
            Copy(original, instance: WorldInstanceId.New()),
            Copy(original, island: 4, reservation: Guid.NewGuid()),
            Copy(original, island: 4, admitted: [original.AdmittedMembers[0]], eligible: [original.FrozenMembers[0]])
        })
            Check.True((await store.SettleAsync(altered)).Status == WonderlandTitleStatus.RequestConflict &&
                await StateAsync(source, original) == committed,
                "milestone retries and later islands cannot rewrite the bound instance, reservation, original roster, or clear evidence");
    }

    private static async Task CheckRollbackAsync(NpgsqlDataSource source)
    {
        var request = await CreateAsync(source);
        var last = request.FrozenMembers.Last().CharacterId;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var function = "wonder_title_fail_" + suffix;
        var trigger = "wonder_title_trigger_" + suffix;
        var before = await StateAsync(source, request);
        await using (var install = source.CreateCommand($"""
            CREATE FUNCTION public.{function}() RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN
                IF NEW.id={last} THEN RAISE EXCEPTION 'Wonderland title rollback fixture' USING ERRCODE='P0001'; END IF;
                RETURN NEW;
            END; $body$;
            CREATE TRIGGER {trigger} BEFORE UPDATE OF medusa_reward_revision ON public.character_base
                FOR EACH ROW EXECUTE FUNCTION public.{function}();
            """)) await install.ExecuteNonQueryAsync();
        try
        {
            var failed = false;
            try { await new PostgresWonderlandTitleStore(source).SettleAsync(request); }
            catch (PostgresException error) when (error.SqlState == "P0001") { failed = true; }
            Check.True(failed && await StateAsync(source, request) == before,
                "second-member failure rolls back run binding, milestone, revisions, receipts, and every ownership grant");
        }
        finally
        {
            await using var cleanup = source.CreateCommand($"""
                DROP TRIGGER IF EXISTS {trigger} ON public.character_base;
                DROP FUNCTION IF EXISTS public.{function}();
                """);
            await cleanup.ExecuteNonQueryAsync();
        }
        Check.True((await new PostgresWonderlandTitleStore(source).SettleAsync(request)).Status == WonderlandTitleStatus.Applied,
            "retry after transaction rollback grants the original milestone successfully");
    }
}
