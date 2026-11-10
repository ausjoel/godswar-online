using Godswar.Server.Application.WorldInstances;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresAtlantisCompletionRewardStore
{
    private static async Task<AtlantisCompletionRewardReceipt?> ReadExistingAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, AtlantisCompletionRewardRequest request, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT world_instance_id, admission_reservation_id, request_hash, realm_id,
                   policy_revision, started_at_ticks, completed_at_ticks, final_score,
                   hard_points, title_id, admitted_character_ids, character_ids
            FROM public.atlantis_completion_rewards
            WHERE world_instance_id = @instance OR admission_reservation_id = @reservation;
            """, connection, transaction);
        command.Parameters.AddWithValue("instance", request.WorldInstanceId.Value);
        command.Parameters.AddWithValue("reservation", request.AdmissionReservationId);
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) return null;
            var matches = reader.GetGuid(0) == request.WorldInstanceId.Value &&
                reader.GetGuid(1) == request.AdmissionReservationId && reader.GetString(2) == request.RequestHash &&
                reader.GetInt16(3) == request.RealmId.Value &&
                reader.GetString(4) == AtlantisCompletionRewardPolicy.Revision &&
                reader.GetInt64(5) == request.StartedAtUtc.UtcTicks && reader.GetInt64(6) == request.CompletedAtUtc.UtcTicks &&
                reader.GetInt32(7) == request.FinalScore && reader.GetInt32(8) == request.Award.HardPoints &&
                reader.GetInt32(9) == request.Award.TitleId &&
                reader.GetFieldValue<int[]>(10).SequenceEqual(request.AdmittedCharacterIds) &&
                reader.GetFieldValue<int[]>(11).SequenceEqual(request.CharacterIds);
            if (!matches || await reader.ReadAsync(token))
                return Failed(request, AtlantisCompletionRewardStatus.RequestConflict);
        }
        var members = await ReadMembersAsync(connection, transaction, request, token);
        return new(AtlantisCompletionRewardStatus.Duplicate, request.WorldInstanceId, request.Award, members);
    }

    private static async Task<List<LockedCharacter>> LockCharactersAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, AtlantisCompletionRewardRequest request, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT account_id, id, camp, medusa_honor_points, medusa_reward_revision, lifecycle_state = 'active'
            FROM public.character_base
            WHERE server_id = @realm AND id = ANY(@characters)
            ORDER BY id FOR UPDATE;
            """, connection, transaction);
        command.Parameters.AddWithValue("realm", checked((short)request.RealmId.Value));
        // Every recipient is locked, not only the registered party: a member who
        // joined the run by invitation holds his own reservation and is named by
        // the recipient list alone, so locking the admitted ids alone left him out
        // of the settlement entirely and he was never paid.
        command.Parameters.AddWithValue("characters",
            request.CharacterIds.Concat(request.AdmittedCharacterIds).Distinct().ToArray());
        var characters = new List<LockedCharacter>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            characters.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt16(2),
                reader.GetInt32(3), reader.GetInt64(4), reader.GetBoolean(5)));
        return characters;
    }

    private static async Task<bool> AdmissionMatchesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        AtlantisCompletionRewardRequest request, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT realm_id, instance_kind, character_id, claimed_at, admitted_at
            FROM public.legacy_instance_daily_entries
            WHERE reservation_id = @reservation AND admitted_at IS NOT NULL
            ORDER BY character_id FOR SHARE;
            """, connection, transaction);
        command.Parameters.AddWithValue("reservation", request.AdmissionReservationId);
        var index = 0;
        await using var reader = await command.ExecuteReaderAsync(token);
        // admitted_at records persistence time, not transfer time. A repaired
        // marker can be written after completion; claimed_at still predates
        // the run and every successful admitted identity must match exactly.
        // This fences the registered party. A member who joined later holds their
        // own reservation and is fenced by the recipient list instead, which only
        // ever names sessions that were inside the instance at completion.
        while (await reader.ReadAsync(token))
        {
            if (index >= request.AdmittedCharacterIds.Count || reader.GetInt16(0) != request.RealmId.Value ||
                reader.GetInt16(1) != 1 || reader.GetInt32(2) != request.AdmittedCharacterIds[index] ||
                reader.IsDBNull(4) || reader.GetDateTime(3) > request.StartedAtUtc.UtcDateTime)
                return false;
            index++;
        }
        return index == request.AdmittedCharacterIds.Count;
    }

    private static async Task<IReadOnlyList<AtlantisCompletionRewardMember>> ReadMembersAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, AtlantisCompletionRewardRequest request, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT account_id, character_id, camp, honor_before, honor_after, reward_revision, awarded_title_id
            FROM public.atlantis_completion_reward_members
            WHERE world_instance_id = @instance ORDER BY character_id;
            """, connection, transaction);
        command.Parameters.AddWithValue("instance", request.WorldInstanceId.Value);
        var members = new List<AtlantisCompletionRewardMember>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            members.Add(new(reader.GetInt32(0), reader.GetInt32(1), checked((byte)reader.GetInt16(2)),
                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt64(5), checked((uint)reader.GetInt32(6))));
        if (members.Count != request.FrozenMembers.Count || members.Where((member, index) =>
            member.AccountId != request.FrozenMembers[index].AccountId ||
            member.CharacterId != request.FrozenMembers[index].CharacterId ||
            member.HonorBefore < 0 || (long)member.HonorAfter - member.HonorBefore != request.Award.HardPoints ||
            member.RewardRevision <= 0 || member.AwardedTitleId != request.Award.TitleId).Any())
            throw new InvalidDataException("Atlantis durable reward membership is incomplete or inconsistent.");
        return members.AsReadOnly();
    }
}
