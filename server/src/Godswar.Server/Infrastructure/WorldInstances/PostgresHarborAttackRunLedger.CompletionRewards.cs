using System.Data;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldInstances;

/// <summary>
/// BAY2: exactly-once Bay Under Attack completion EXP (pattern of
/// PostgresAtlantisCompletionRewardStore): one settlement row per run
/// reservation (and per world instance), one receipt row per member, the EXP
/// written with the progression_reward_revision compare-and-swap the EXP Pill
/// and the monster-death executor use, and the run's ledger row closed in the
/// SAME transaction. Tables are created on first use with the ledger's DDL (no
/// registered migration).
/// </summary>
internal sealed partial class PostgresHarborAttackRunLedger : IHarborAttackCompletionRewardStore
{
    internal const string CompletionRewardSchemaSql =
        """

        CREATE TABLE IF NOT EXISTS public.harbor_attack_completion_rewards (
            run_reservation_id uuid PRIMARY KEY,
            world_instance_id uuid NOT NULL UNIQUE,
            realm_id smallint NOT NULL,
            map_id smallint NOT NULL CHECK (map_id IN (208, 209)),
            request_hash text NOT NULL,
            policy_revision text NOT NULL,
            end_reason text NOT NULL
                CHECK (end_reason IN ('completed', 'terminated', 'deadline')),
            started_at timestamptz NOT NULL,
            ended_at timestamptz NOT NULL,
            cleared_score integer NOT NULL CHECK (cleared_score >= 0),
            total_score integer NOT NULL CHECK (total_score >= 0),
            clearance_bp integer NOT NULL CHECK (clearance_bp BETWEEN 0 AND 10000),
            percent_of_level integer NOT NULL CHECK (percent_of_level BETWEEN 0 AND 1000),
            character_ids integer[] NOT NULL,
            settled_at timestamptz NOT NULL DEFAULT clock_timestamp()
        );
        CREATE TABLE IF NOT EXISTS public.harbor_attack_completion_reward_members (
            run_reservation_id uuid NOT NULL,
            character_id integer NOT NULL,
            account_id integer NOT NULL,
            admitted boolean NOT NULL,
            experience_granted integer NOT NULL CHECK (experience_granted >= 0),
            level_before integer NOT NULL,
            experience_before bigint NOT NULL,
            level_after integer NOT NULL,
            experience_after bigint NOT NULL,
            progression_revision bigint NOT NULL,
            PRIMARY KEY (run_reservation_id, character_id)
        );
        COMMENT ON TABLE public.harbor_attack_completion_rewards IS
            'BAY2: one row per paid Bay Under Attack run (exactly-once completion EXP, GENERATED amounts).';
        COMMENT ON TABLE public.harbor_attack_completion_reward_members IS
            'BAY2: one receipt per member of a paid Bay Under Attack run.';
        """;

    public async Task<HarborAttackCompletionRewardReceipt> SettleAsync(
        HarborAttackCompletionRewardRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        var existing = await ReadExistingRewardAsync(connection, transaction, request, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        // Character rows first, in id order (the order every daily-entry and
        // reward store keeps), then the settlement row.
        var characters = await LockRewardCharactersAsync(connection, transaction, request, cancellationToken);
        existing = await ReadExistingRewardAsync(connection, transaction, request, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }
        var admitted = await ReadAdmittedCharactersAsync(connection, transaction, request, cancellationToken);

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO public.harbor_attack_completion_rewards (
                run_reservation_id, world_instance_id, realm_id, map_id, request_hash,
                policy_revision, end_reason, started_at, ended_at, cleared_score,
                total_score, clearance_bp, percent_of_level, character_ids)
            VALUES (@run, @instance, @realm, @map, @hash, @policy, @reason, @started, @ended,
                @cleared, @total, @clearance, @percent, @characters)
            ON CONFLICT DO NOTHING RETURNING 1;
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("run", request.RunReservationId);
            insert.Parameters.AddWithValue("instance", request.WorldInstanceId);
            insert.Parameters.AddWithValue("realm", request.RealmId);
            insert.Parameters.AddWithValue("map", (short)request.MapId);
            insert.Parameters.AddWithValue("hash", request.RequestHash);
            insert.Parameters.AddWithValue("policy", HarborAttackCompletionRewardPolicy.Revision);
            insert.Parameters.AddWithValue("reason", request.EndReason);
            insert.Parameters.AddWithValue("started", request.StartedAtUtc.UtcDateTime);
            insert.Parameters.AddWithValue("ended", request.EndedAtUtc.UtcDateTime);
            insert.Parameters.AddWithValue("cleared", request.ClearedScore);
            insert.Parameters.AddWithValue("total", request.TotalScore);
            insert.Parameters.AddWithValue("clearance", request.ClearanceBasisPoints);
            insert.Parameters.AddWithValue("percent", request.PercentOfLevel);
            insert.Parameters.AddWithValue("characters", request.CharacterIds.ToArray());
            if (await insert.ExecuteScalarAsync(cancellationToken) is not int)
            {
                var raced = await ReadExistingRewardAsync(connection, transaction, request, cancellationToken) ??
                    new(HarborAttackCompletionRewardStatus.RequestConflict, request.RunReservationId,
                        request.ClearanceBasisPoints, []);
                await transaction.CommitAsync(cancellationToken);
                return raced;
            }
        }

        var members = new List<HarborAttackCompletionRewardMember>(request.Members.Count);
        foreach (var member in request.Members)
        {
            var locked = characters.GetValueOrDefault(member.CharacterId);
            var eligible = locked is not null && locked.IsActive &&
                locked.AccountId == member.AccountId && admitted.Contains(member.CharacterId);
            HarborAttackCompletionRewardMember receipt;
            if (!eligible)
            {
                receipt = new(member.AccountId, member.CharacterId, false, 0,
                    locked?.Level ?? 0, locked?.Experience ?? 0,
                    locked?.Level ?? 0, locked?.Experience ?? 0, locked?.Revision ?? 0);
            }
            else
            {
                var amount = HarborAttackCompletionRewardPolicy.Resolve(
                    locked!.Level, locked.Sealed, request.ClearanceBasisPoints, request.PercentOfLevel);
                var progression = amount > 0
                    ? PlayerExperienceCatalog.Apply(locked.Level, locked.Experience, amount, locked.Sealed)
                    : null;
                if (progression is null || progression.ExperienceGained <= 0)
                {
                    // Level 200 (or nothing cleared): a receipt with 0, no write.
                    receipt = new(member.AccountId, member.CharacterId, true, 0,
                        locked.Level, locked.Experience, locked.Level, locked.Experience, locked.Revision);
                }
                else
                {
                    var revision = checked(locked.Revision + 1);
                    await using var update = new NpgsqlCommand(
                        """
                        UPDATE public.character_base
                        SET fighter_job_lv = @level, fighter_job_exp = @experience,
                            progression_reward_revision = @revision
                        WHERE id = @character AND account_id = @account AND lifecycle_state = 'active'
                          AND progression_reward_revision = @expected;
                        """,
                        connection,
                        transaction);
                    update.Parameters.AddWithValue("level", progression.Level);
                    update.Parameters.AddWithValue("experience", progression.Experience);
                    update.Parameters.AddWithValue("revision", revision);
                    update.Parameters.AddWithValue("character", member.CharacterId);
                    update.Parameters.AddWithValue("account", member.AccountId);
                    update.Parameters.AddWithValue("expected", locked.Revision);
                    if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                    {
                        throw new DBConcurrencyException(
                            "A locked Bay Under Attack reward character changed unexpectedly.");
                    }
                    receipt = new(member.AccountId, member.CharacterId, true, progression.ExperienceGained,
                        locked.Level, locked.Experience, progression.Level, progression.Experience, revision);
                }
            }

            await using (var row = new NpgsqlCommand(
                """
                INSERT INTO public.harbor_attack_completion_reward_members (
                    run_reservation_id, character_id, account_id, admitted, experience_granted,
                    level_before, experience_before, level_after, experience_after, progression_revision)
                VALUES (@run, @character, @account, @admitted, @granted, @levelBefore, @expBefore,
                    @levelAfter, @expAfter, @revision);
                """,
                connection,
                transaction))
            {
                row.Parameters.AddWithValue("run", request.RunReservationId);
                row.Parameters.AddWithValue("character", receipt.CharacterId);
                row.Parameters.AddWithValue("account", receipt.AccountId);
                row.Parameters.AddWithValue("admitted", receipt.Admitted);
                row.Parameters.AddWithValue("granted", receipt.ExperienceGranted);
                row.Parameters.AddWithValue("levelBefore", receipt.LevelBefore);
                row.Parameters.AddWithValue("expBefore", receipt.ExperienceBefore);
                row.Parameters.AddWithValue("levelAfter", receipt.LevelAfter);
                row.Parameters.AddWithValue("expAfter", receipt.ExperienceAfter);
                row.Parameters.AddWithValue("revision", receipt.ProgressionRevision);
                if (await row.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new InvalidDataException("A Bay Under Attack reward receipt was not inserted.");
                }
            }
            members.Add(receipt);
        }

        // The ledger row closes with the payout, so a restart refund can never
        // give back the entry of a paid run. Inserted closed if its (queued)
        // open has not landed yet; the later open is then a no-op.
        await using (var close = new NpgsqlCommand(
            """
            INSERT INTO public.harbor_attack_runs (
                reservation_id, realm_id, map_id, started_at, closed_at, close_reason)
            VALUES (@run, @realm, @map, @started, clock_timestamp(), @reason)
            ON CONFLICT (reservation_id) DO UPDATE
                SET closed_at = EXCLUDED.closed_at, close_reason = EXCLUDED.close_reason
                WHERE public.harbor_attack_runs.closed_at IS NULL;
            """,
            connection,
            transaction))
        {
            close.Parameters.AddWithValue("run", request.RunReservationId);
            close.Parameters.AddWithValue("realm", request.RealmId);
            close.Parameters.AddWithValue("map", (short)request.MapId);
            close.Parameters.AddWithValue("started", request.StartedAtUtc.UtcDateTime);
            close.Parameters.AddWithValue("reason", request.EndReason);
            await close.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new(HarborAttackCompletionRewardStatus.Applied, request.RunReservationId,
            request.ClearanceBasisPoints, members.AsReadOnly());
    }

    public async Task<(int Level, long Experience)?> ReadProgressionAsync(
        int characterId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT fighter_job_lv, fighter_job_exp FROM public.character_base WHERE id = @id;",
            connection);
        command.Parameters.AddWithValue("id", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt32(0), reader.GetInt64(1))
            : null;
    }

    private static async Task<int> ClosePaidRunsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        short realmId,
        DateTimeOffset processStartedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE public.harbor_attack_runs r
            SET closed_at = clock_timestamp(), close_reason = w.end_reason
            FROM public.harbor_attack_completion_rewards w
            WHERE w.run_reservation_id = r.reservation_id
              AND r.realm_id = @realm
              AND r.closed_at IS NULL
              AND r.started_at < @processStart;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("realm", realmId);
        command.Parameters.AddWithValue("processStart", processStartedAtUtc.UtcDateTime);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<HarborAttackCompletionRewardReceipt?> ReadExistingRewardAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        HarborAttackCompletionRewardRequest request,
        CancellationToken cancellationToken)
    {
        await using (var command = new NpgsqlCommand(
            """
            SELECT run_reservation_id, world_instance_id, request_hash, clearance_bp
            FROM public.harbor_attack_completion_rewards
            WHERE run_reservation_id = @run OR world_instance_id = @instance;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("run", request.RunReservationId);
            command.Parameters.AddWithValue("instance", request.WorldInstanceId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }
            var matches = reader.GetGuid(0) == request.RunReservationId &&
                reader.GetGuid(1) == request.WorldInstanceId &&
                reader.GetString(2) == request.RequestHash;
            if (!matches || await reader.ReadAsync(cancellationToken))
            {
                return new(HarborAttackCompletionRewardStatus.RequestConflict, request.RunReservationId,
                    request.ClearanceBasisPoints, []);
            }
        }

        var members = new List<HarborAttackCompletionRewardMember>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT account_id, character_id, admitted, experience_granted, level_before,
                   experience_before, level_after, experience_after, progression_revision
            FROM public.harbor_attack_completion_reward_members
            WHERE run_reservation_id = @run ORDER BY character_id;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("run", request.RunReservationId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                members.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetBoolean(2),
                    reader.GetInt32(3), reader.GetInt32(4), reader.GetInt64(5), reader.GetInt32(6),
                    reader.GetInt64(7), reader.GetInt64(8)));
            }
        }
        if (!members.Select(static member => member.CharacterId).SequenceEqual(request.CharacterIds))
        {
            throw new InvalidDataException("Bay Under Attack reward receipts are incomplete.");
        }
        return new(HarborAttackCompletionRewardStatus.Duplicate, request.RunReservationId,
            request.ClearanceBasisPoints, members.AsReadOnly());
    }

    private static async Task<Dictionary<int, LockedRewardCharacter>> LockRewardCharactersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        HarborAttackCompletionRewardRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id, account_id, fighter_job_lv, fighter_job_exp, fighter_level_sealed,
                   progression_reward_revision, lifecycle_state = 'active'
            FROM public.character_base
            WHERE server_id = @realm AND id = ANY(@characters)
            ORDER BY id FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("realm", request.RealmId);
        command.Parameters.AddWithValue("characters", request.CharacterIds.ToArray());
        var characters = new Dictionary<int, LockedRewardCharacter>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var character = new LockedRewardCharacter(reader.GetInt32(1), reader.GetInt32(2),
                reader.GetInt64(3), reader.GetBoolean(4), reader.GetInt64(5), reader.GetBoolean(6));
            if (character.Level is < 1 or > PlayerExperienceCatalog.MaximumLevel ||
                character.Experience is < 0 or > PlayerExperienceCatalog.MaximumStoredExperience ||
                character.Revision is < 0 or long.MaxValue)
            {
                throw new InvalidDataException("A Bay Under Attack reward character's progression is invalid.");
            }
            characters[reader.GetInt32(0)] = character;
        }
        return characters;
    }

    /// <summary>
    /// The members who hold a Bay Under Attack (kind 3) daily entry on one of
    /// this run's reservations: the fence that only an admitted entrant is paid.
    /// </summary>
    private static async Task<HashSet<int>> ReadAdmittedCharactersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        HarborAttackCompletionRewardRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT d.character_id
            FROM public.legacy_instance_daily_entries d
            WHERE d.realm_id = @realm AND d.instance_kind = @kind
              AND d.character_id = ANY(@characters)
              AND (d.reservation_id = ANY(@reservations)
                   OR d.reservation_id IN (
                       SELECT m.member_reservation_id FROM public.harbor_attack_run_members m
                       WHERE m.run_reservation_id = @run AND m.character_id = d.character_id))
            FOR SHARE OF d;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("realm", request.RealmId);
        command.Parameters.AddWithValue("kind", HarborAttackInstanceKind);
        command.Parameters.AddWithValue("characters", request.CharacterIds.ToArray());
        command.Parameters.AddWithValue("reservations", request.EntryReservationIds.ToArray());
        command.Parameters.AddWithValue("run", request.RunReservationId);
        var admitted = new HashSet<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            admitted.Add(reader.GetInt32(0));
        }
        return admitted;
    }

    private sealed record LockedRewardCharacter(
        int AccountId,
        int Level,
        long Experience,
        bool Sealed,
        long Revision,
        bool IsActive);
}
