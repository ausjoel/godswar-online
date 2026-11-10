using System.Data;
using Godswar.Server.Application.WorldInstances;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresWonderlandTitleStore
{
    private static async Task<bool> BindRunAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        WonderlandTitleRequest request, CancellationToken token)
    {
        await using (var command = new NpgsqlCommand("""
            INSERT INTO public.wonderland_title_runs(world_instance_id,admission_reservation_id,realm_id,run_hash,
                policy_revision,started_at_ticks,admitted_character_ids,admitted_account_ids,admitted_owner_ids,admitted_owner_generations)
            VALUES(@instance,@reservation,@realm,@hash,@policy,@started,@characters,@accounts,@owners,@generations)
            ON CONFLICT DO NOTHING;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("instance", request.WorldInstanceId.Value);
            command.Parameters.AddWithValue("reservation", request.AdmissionReservationId);
            command.Parameters.AddWithValue("realm", checked((short)request.RealmId.Value));
            command.Parameters.AddWithValue("hash", request.RunHash);
            command.Parameters.AddWithValue("policy", WonderlandTitlePolicy.Revision);
            command.Parameters.AddWithValue("started", request.StartedAtUtc.UtcTicks);
            command.Parameters.AddWithValue("characters", request.AdmittedCharacterIds.ToArray());
            command.Parameters.AddWithValue("accounts", request.AdmittedMembers.Select(member => member.AccountId).ToArray());
            command.Parameters.AddWithValue("owners", request.AdmittedMembers.Select(member => member.Ownership.OwnerId).ToArray());
            command.Parameters.AddWithValue("generations", request.AdmittedMembers.Select(member => member.Ownership.Generation).ToArray());
            await command.ExecuteNonQueryAsync(token);
        }
        await using var read = new NpgsqlCommand("""
            SELECT run_hash FROM public.wonderland_title_runs
            WHERE world_instance_id=@instance AND admission_reservation_id=@reservation FOR UPDATE;
            """, connection, transaction);
        read.Parameters.AddWithValue("instance", request.WorldInstanceId.Value);
        read.Parameters.AddWithValue("reservation", request.AdmissionReservationId);
        return await read.ExecuteScalarAsync(token) is string hash && hash == request.RunHash;
    }

    private static async Task InsertMilestoneAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        WonderlandTitleRequest request, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO public.wonderland_title_milestones(world_instance_id,island_number,title_id,request_hash,
                cleared_at_ticks,character_ids) VALUES(@instance,@island,@title,@hash,@cleared,@characters);
            """, connection, transaction);
        AddIdentity(command, request);
        command.Parameters.AddWithValue("title", checked((int)request.Award.TitleId));
        command.Parameters.AddWithValue("hash", request.RequestHash);
        command.Parameters.AddWithValue("cleared", request.ClearedAtUtc.UtcTicks);
        command.Parameters.AddWithValue("characters", request.CharacterIds.ToArray());
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task WriteMemberAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        WonderlandTitleRequest request, WonderlandTitleReceiptMember member, CancellationToken token)
    {
        if (member.NewlyOwned)
        {
            await using var update = new NpgsqlCommand("""
                UPDATE public.character_base SET medusa_reward_revision=@revision
                WHERE id=@character AND account_id=@account AND server_id=@realm AND lifecycle_state='active'
                  AND medusa_reward_revision=@before;
                """, connection, transaction);
            update.Parameters.AddWithValue("revision", member.RewardRevision);
            update.Parameters.AddWithValue("character", member.CharacterId);
            update.Parameters.AddWithValue("account", member.AccountId);
            update.Parameters.AddWithValue("realm", checked((short)request.RealmId.Value));
            update.Parameters.AddWithValue("before", member.RewardRevision - 1);
            if (await update.ExecuteNonQueryAsync(token) != 1)
                throw new DBConcurrencyException("A locked Wonderland title character changed unexpectedly.");
        }
        var eligible = request.FrozenMembers.Single(value => value.CharacterId == member.CharacterId);
        await using (var command = new NpgsqlCommand("""
            INSERT INTO public.wonderland_title_members(world_instance_id,island_number,account_id,character_id,
                eligible_owner_id,eligible_owner_generation,title_id,honor_points,selected_title_id,reward_revision,newly_owned)
            VALUES(@instance,@island,@account,@character,@owner,@generation,@title,@honor,@selected,@revision,@new);
            """, connection, transaction))
        {
            AddIdentity(command, request);
            command.Parameters.AddWithValue("account", member.AccountId);
            command.Parameters.AddWithValue("character", member.CharacterId);
            command.Parameters.AddWithValue("owner", eligible.Ownership.OwnerId);
            command.Parameters.AddWithValue("generation", eligible.Ownership.Generation);
            command.Parameters.AddWithValue("title", checked((int)request.Award.TitleId));
            command.Parameters.AddWithValue("honor", member.HonorPoints);
            command.Parameters.AddWithValue("selected", checked((int)member.SelectedTitleId));
            command.Parameters.AddWithValue("revision", member.RewardRevision);
            command.Parameters.AddWithValue("new", member.NewlyOwned);
            await command.ExecuteNonQueryAsync(token);
        }
        if (!member.NewlyOwned) return;
        await using var ownership = new NpgsqlCommand("""
            INSERT INTO public.wonderland_character_title_ownership(character_id,title_id,
                source_world_instance_id,source_island_number,acquired_at)
            VALUES(@character,@title,@instance,@island,@at);
            """, connection, transaction);
        AddIdentity(ownership, request);
        ownership.Parameters.AddWithValue("character", member.CharacterId);
        ownership.Parameters.AddWithValue("title", checked((int)request.Award.TitleId));
        ownership.Parameters.AddWithValue("at", request.ClearedAtUtc.UtcDateTime);
        await ownership.ExecuteNonQueryAsync(token);
    }
}
