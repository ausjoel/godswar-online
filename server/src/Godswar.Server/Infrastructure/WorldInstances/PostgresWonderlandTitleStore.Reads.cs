using Godswar.Server.Application.WorldInstances;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed partial class PostgresWonderlandTitleStore
{
    private static async Task<WonderlandTitleReceipt?> ReadExistingAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, WonderlandTitleRequest request, CancellationToken token)
    {
        await using (var command = new NpgsqlCommand("""
            SELECT world_instance_id, admission_reservation_id, run_hash
            FROM public.wonderland_title_runs
            WHERE world_instance_id=@instance OR admission_reservation_id=@reservation;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("instance", request.WorldInstanceId.Value);
            command.Parameters.AddWithValue("reservation", request.AdmissionReservationId);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return null;
            if (reader.GetGuid(0) != request.WorldInstanceId.Value || reader.GetGuid(1) != request.AdmissionReservationId ||
                reader.GetString(2) != request.RunHash || await reader.ReadAsync(token))
                return Failed(request, WonderlandTitleStatus.RequestConflict);
        }
        await using (var command = new NpgsqlCommand("""
            SELECT request_hash, title_id, cleared_at_ticks, character_ids
            FROM public.wonderland_title_milestones WHERE world_instance_id=@instance AND island_number=@island;
            """, connection, transaction))
        {
            AddIdentity(command, request);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return null;
            if (reader.GetString(0) != request.RequestHash || reader.GetInt32(1) != request.Award.TitleId ||
                reader.GetInt64(2) != request.ClearedAtUtc.UtcTicks ||
                !reader.GetFieldValue<int[]>(3).SequenceEqual(request.CharacterIds))
                return Failed(request, WonderlandTitleStatus.RequestConflict);
        }
        await using var members = new NpgsqlCommand("""
            SELECT account_id, character_id, honor_points, selected_title_id, reward_revision, newly_owned,
                   eligible_owner_id, eligible_owner_generation, title_id
            FROM public.wonderland_title_members WHERE world_instance_id=@instance AND island_number=@island
            ORDER BY character_id;
            """, connection, transaction);
        AddIdentity(members, request);
        var result = new List<WonderlandTitleReceiptMember>();
        await using (var reader = await members.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                var index = result.Count;
                if (index >= request.FrozenMembers.Count || reader.GetInt32(0) != request.FrozenMembers[index].AccountId ||
                    reader.GetInt32(1) != request.FrozenMembers[index].CharacterId ||
                    reader.GetGuid(6) != request.FrozenMembers[index].Ownership.OwnerId ||
                    reader.GetInt64(7) != request.FrozenMembers[index].Ownership.Generation ||
                    reader.GetInt32(8) != request.Award.TitleId)
                    throw new InvalidDataException("Wonderland title receipt identity is inconsistent.");
                result.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
                    checked((uint)reader.GetInt32(3)), reader.GetInt64(4), reader.GetBoolean(5)));
            }
        }
        if (result.Count != request.FrozenMembers.Count)
            throw new InvalidDataException("Wonderland title receipt membership is incomplete.");
        return new(WonderlandTitleStatus.Duplicate, request.WorldInstanceId, request.Award, result.AsReadOnly());
    }

    private static async Task<List<LockedCharacter>> LockCharactersAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, WonderlandTitleRequest request, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT account_id,id,medusa_honor_points,selected_title_id,medusa_reward_revision,lifecycle_state='active'
            FROM public.character_base cb WHERE server_id=@realm AND id=ANY(@characters) ORDER BY id FOR UPDATE;
            """, connection, transaction);
        command.Parameters.AddWithValue("realm", checked((short)request.RealmId.Value));
        command.Parameters.AddWithValue("characters", request.AdmittedCharacterIds.ToArray());
        var result = new List<LockedCharacter>();
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
                result.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), checked((uint)reader.GetInt32(3)),
                    reader.GetInt64(4), reader.GetBoolean(5), false));
        }
        // A different run can grant this same title while our character lock
        // waits. Use a fresh ReadCommitted statement after acquiring the lock.
        await using var ownership = new NpgsqlCommand("""
            SELECT character_id FROM public.wonderland_character_title_ownership
            WHERE character_id=ANY(@characters) AND title_id=@title;
            """, connection, transaction);
        ownership.Parameters.AddWithValue("characters", request.AdmittedCharacterIds.ToArray());
        ownership.Parameters.AddWithValue("title", checked((int)request.Award.TitleId));
        var owned = new HashSet<int>();
        await using (var reader = await ownership.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) owned.Add(reader.GetInt32(0));
        return result.Select(character => character with { AlreadyOwned = owned.Contains(character.CharacterId) }).ToList();
    }

    private static async Task<bool> AdmissionMatchesAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, WonderlandTitleRequest request, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT realm_id,instance_kind,character_id,claimed_at FROM public.legacy_instance_daily_entries
            WHERE reservation_id=ANY(@reservations) AND admitted_at IS NOT NULL ORDER BY character_id FOR SHARE;
            """, connection, transaction);
        command.Parameters.AddWithValue("reservations", request.AdmissionReservationIds.ToArray());
        var index = 0;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (index >= request.AdmittedCharacterIds.Count || reader.GetInt16(0) != request.RealmId.Value ||
                reader.GetInt16(1) != 2 || reader.GetInt32(2) != request.AdmittedCharacterIds[index] ||
                reader.GetDateTime(3) > request.StartedAtUtc.UtcDateTime) return false;
            index++;
        }
        return index == request.AdmittedCharacterIds.Count;
    }
}
