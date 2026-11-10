using System.Data;
using Godswar.Server.Application.WorldInstances;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresAtlantisCompletionRewardStore
{
    private static async Task<bool> TryInsertSettlementAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        AtlantisCompletionRewardRequest request, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO public.atlantis_completion_rewards (
                world_instance_id, admission_reservation_id, realm_id, request_hash,
                policy_revision, started_at_ticks, completed_at_ticks, final_score,
                hard_points, title_id, admitted_character_ids, character_ids)
            VALUES (@instance, @reservation, @realm, @hash, @policy, @started, @completed,
                @score, @points, @title, @admitted, @characters)
            ON CONFLICT DO NOTHING RETURNING 1;
            """, connection, transaction);
        command.Parameters.AddWithValue("instance", request.WorldInstanceId.Value);
        command.Parameters.AddWithValue("reservation", request.AdmissionReservationId);
        command.Parameters.AddWithValue("realm", checked((short)request.RealmId.Value));
        command.Parameters.AddWithValue("hash", request.RequestHash);
        command.Parameters.AddWithValue("policy", AtlantisCompletionRewardPolicy.Revision);
        command.Parameters.AddWithValue("started", request.StartedAtUtc.UtcTicks);
        command.Parameters.AddWithValue("completed", request.CompletedAtUtc.UtcTicks);
        command.Parameters.AddWithValue("score", request.FinalScore);
        command.Parameters.AddWithValue("points", request.Award.HardPoints);
        command.Parameters.AddWithValue("title", checked((int)request.Award.TitleId));
        command.Parameters.AddWithValue("admitted", request.AdmittedCharacterIds.ToArray());
        command.Parameters.AddWithValue("characters", request.CharacterIds.ToArray());
        return await command.ExecuteScalarAsync(token) is int and 1;
    }

    private static async Task WriteMemberAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        AtlantisCompletionRewardRequest request, AtlantisCompletionRewardMember member, CancellationToken token)
    {
        await using (var command = new NpgsqlCommand("""
            UPDATE public.character_base
            SET medusa_honor_points = @after, medusa_reward_revision = @revision
            WHERE id = @character AND account_id = @account AND server_id = @realm AND lifecycle_state = 'active'
                AND medusa_honor_points = @before AND medusa_reward_revision = @previousRevision;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("after", member.HonorAfter);
            command.Parameters.AddWithValue("revision", member.RewardRevision);
            command.Parameters.AddWithValue("character", member.CharacterId);
            command.Parameters.AddWithValue("account", member.AccountId);
            command.Parameters.AddWithValue("realm", checked((short)request.RealmId.Value));
            command.Parameters.AddWithValue("before", member.HonorBefore);
            command.Parameters.AddWithValue("previousRevision", member.RewardRevision - 1);
            if (await command.ExecuteNonQueryAsync(token) != 1)
                throw new DBConcurrencyException("A locked Atlantis reward character changed unexpectedly.");
        }
        await using (var command = new NpgsqlCommand("""
            INSERT INTO public.atlantis_completion_reward_members (
                world_instance_id, account_id, character_id, camp, honor_before, honor_after,
                reward_revision, awarded_title_id)
            VALUES (@instance, @account, @character, @camp, @before, @after, @revision, @title);
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("instance", request.WorldInstanceId.Value);
            command.Parameters.AddWithValue("account", member.AccountId);
            command.Parameters.AddWithValue("character", member.CharacterId);
            command.Parameters.AddWithValue("camp", checked((short)member.Camp));
            command.Parameters.AddWithValue("before", member.HonorBefore);
            command.Parameters.AddWithValue("after", member.HonorAfter);
            command.Parameters.AddWithValue("revision", member.RewardRevision);
            command.Parameters.AddWithValue("title", checked((int)member.AwardedTitleId));
            if (await command.ExecuteNonQueryAsync(token) != 1)
                throw new InvalidDataException("An Atlantis reward member receipt was not inserted.");
        }
        // Only a completed run grants a title. An incomplete settlement records
        // its HardPoints with no title, and the ownership table accepts only the
        // two real title identities.
        if (member.AwardedTitleId == 0)
        {
            return;
        }
        await using (var command = new NpgsqlCommand("""
            INSERT INTO public.atlantis_character_title_ownership (
                character_id, title_id, source_world_instance_id, acquired_at)
            VALUES (@character, @title, @instance, @acquired)
            ON CONFLICT (character_id, title_id) DO NOTHING;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("character", member.CharacterId);
            command.Parameters.AddWithValue("title", checked((int)member.AwardedTitleId));
            command.Parameters.AddWithValue("instance", request.WorldInstanceId.Value);
            command.Parameters.AddWithValue("acquired", request.CompletedAtUtc.UtcDateTime);
            await command.ExecuteNonQueryAsync(token);
        }
    }
}
