using Godswar.Server.Application.WorldInstances;
using Npgsql;

namespace Godswar.Server.State;

// Pindus / Ni Mini rounds (BattlefieldRewardPolicy), their settlement and the Battlefield Awarder's claims.
//
// Like server_rates and the economy build's tables, these tables are NOT a registered schema migration: they are
// created with idempotent DDL on first use (any method below), so the previous server dll still starts against a
// database this build has used (the migration runner refuses a history it does not know) and rolling back needs no
// database step. Development/build/battlefield/migration.sql holds the same DDL as a pre-release step.
internal sealed partial class PostgresGameStore : IBattlefieldRoundStore
{
    private const long BattlefieldRoundLockKey = 0x4246524E;   // "BFRN"
    private const long BattlefieldSchemaLockKey = 0x42465343;  // "BFSC"
    internal const string PindusTitleGrantor = "battlefield-pindus";
    internal const string NiMiniTitleGrantor = "battlefield-nimini";

    internal const string BattlefieldSchemaSql =
        """
        CREATE TABLE IF NOT EXISTS public.battlefield_rounds (
            battlefield smallint NOT NULL CHECK (battlefield IN (1, 2)),
            round_start timestamptz NOT NULL,
            round_end timestamptz NOT NULL CHECK (round_end > round_start),
            sparta_points bigint NOT NULL DEFAULT 0,
            athens_points bigint NOT NULL DEFAULT 0,
            winner smallint CHECK (winner IN (0, 1)),
            created_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
            settled_at timestamptz,
            PRIMARY KEY (battlefield, round_start)
        );
        CREATE TABLE IF NOT EXISTS public.battlefield_participants (
            battlefield smallint NOT NULL,
            round_start timestamptz NOT NULL,
            character_id integer NOT NULL REFERENCES public.character_base(id) ON DELETE CASCADE,
            faction smallint NOT NULL CHECK (faction IN (0, 1)),
            kills integer NOT NULL DEFAULT 0 CHECK (kills >= 0),
            kill_points bigint NOT NULL DEFAULT 0 CHECK (kill_points >= 0),
            kill_level_sum bigint NOT NULL DEFAULT 0 CHECK (kill_level_sum >= 0),
            holy_points bigint NOT NULL DEFAULT 0 CHECK (holy_points >= 0),
            boss_points bigint NOT NULL DEFAULT 0 CHECK (boss_points >= 0),
            holy_issued integer NOT NULL DEFAULT 0 CHECK (holy_issued >= 0),
            holy_turned_in integer NOT NULL DEFAULT 0 CHECK (holy_turned_in >= 0),
            entered_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
            updated_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
            PRIMARY KEY (battlefield, round_start, character_id),
            FOREIGN KEY (battlefield, round_start) REFERENCES public.battlefield_rounds(battlefield, round_start)
        );
        CREATE TABLE IF NOT EXISTS public.battlefield_round_rewards (
            battlefield smallint NOT NULL,
            round_start timestamptz NOT NULL,
            character_id integer NOT NULL REFERENCES public.character_base(id) ON DELETE CASCADE,
            faction smallint NOT NULL CHECK (faction IN (0, 1)),
            level integer NOT NULL CHECK (level > 0),
            outcome smallint NOT NULL CHECK (outcome IN (0, 1, 2)),
            kill_points bigint NOT NULL,
            holy_points bigint NOT NULL,
            boss_points bigint NOT NULL,
            kill_rank integer NOT NULL,
            task_rank integer NOT NULL,
            faction_rank integer NOT NULL,
            experience integer NOT NULL CHECK (experience >= 0),
            talent_points integer NOT NULL CHECK (talent_points >= 0),
            reputation integer NOT NULL CHECK (reputation >= 0),
            kill_experience integer NOT NULL CHECK (kill_experience >= 0),
            holy_experience integer NOT NULL CHECK (holy_experience >= 0),
            created_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
            faction_claimed_at timestamptz,
            points_claimed_at timestamptz,
            titles_claimed smallint NOT NULL DEFAULT 0,
            PRIMARY KEY (battlefield, round_start, character_id),
            FOREIGN KEY (battlefield, round_start) REFERENCES public.battlefield_rounds(battlefield, round_start)
        );
        CREATE INDEX IF NOT EXISTS battlefield_round_rewards_character
            ON public.battlefield_round_rewards (character_id, battlefield, round_start DESC);
        COMMENT ON TABLE public.battlefield_rounds IS
            'BATTLEFIELD build: Pindus (1) / Ni Mini (2) rounds; created on first use, not a registered migration.';
        """;

    private int _battlefieldSchemaReady;
    private static readonly SemaphoreSlim BattlefieldSchemaGate = new(1, 1);

    /// <summary>Opens a connection, creating the battlefield tables first if this process has not yet done so.</summary>
    private async Task<NpgsqlConnection> OpenBattlefieldConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _battlefieldSchemaReady) == 0)
            {
                // One DDL run at a time (concurrent CREATE ... IF NOT EXISTS can deadlock), across processes too.
                await BattlefieldSchemaGate.WaitAsync(cancellationToken);
                try
                {
                    if (Volatile.Read(ref _battlefieldSchemaReady) == 0)
                    {
                        await using var schema = await connection.BeginTransactionAsync(cancellationToken);
                        await using (var guard = new NpgsqlCommand(
                            "SELECT pg_advisory_xact_lock(@key);", connection, schema))
                        {
                            guard.Parameters.AddWithValue("key", BattlefieldSchemaLockKey);
                            await guard.ExecuteNonQueryAsync(cancellationToken);
                        }
                        await using (var ddl = new NpgsqlCommand(BattlefieldSchemaSql, connection, schema))
                        {
                            await ddl.ExecuteNonQueryAsync(cancellationToken);
                        }
                        await schema.CommitAsync(cancellationToken);
                        Volatile.Write(ref _battlefieldSchemaReady, 1);
                    }
                }
                finally
                {
                    BattlefieldSchemaGate.Release();
                }
            }
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The round row a scoring event at <paramref name="now"/> belongs to. An unsettled round that already covers
    /// <paramref name="now"/> wins over the freshly computed bounds, so a mid-window change of the shift/length rates
    /// (which moves the computed start) keeps scoring the round already running instead of starting a second one.
    /// Otherwise the computed round row is created. Returns the round's start.
    /// </summary>
    private static async Task<DateTimeOffset> ResolveBattlefieldRoundAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, short battlefield, DateTimeOffset start,
        DateTimeOffset end, DateTimeOffset now, CancellationToken token)
    {
        await using (var existing = new NpgsqlCommand("""
            SELECT round_start FROM public.battlefield_rounds
            WHERE battlefield = @battlefield AND settled_at IS NULL AND round_start <= @now AND round_end > @now
            ORDER BY round_start LIMIT 1;
            """, connection, transaction))
        {
            existing.Parameters.AddWithValue("battlefield", battlefield);
            existing.Parameters.AddWithValue("now", now);
            if (await existing.ExecuteScalarAsync(token) is DateTime found)
            {
                return new DateTimeOffset(DateTime.SpecifyKind(found, DateTimeKind.Utc));
            }
        }

        await using var command = new NpgsqlCommand("""
            INSERT INTO public.battlefield_rounds (battlefield, round_start, round_end)
            VALUES (@battlefield, @start, @end) ON CONFLICT (battlefield, round_start) DO NOTHING;
            """, connection, transaction);
        command.Parameters.AddWithValue("battlefield", battlefield);
        command.Parameters.AddWithValue("start", start);
        command.Parameters.AddWithValue("end", end);
        await command.ExecuteNonQueryAsync(token);
        return start;
    }

    private static async Task EnsureBattlefieldParticipantAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, short battlefield, DateTimeOffset start,
        int characterId, byte faction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO public.battlefield_participants (battlefield, round_start, character_id, faction)
            VALUES (@battlefield, @start, @character, @faction)
            ON CONFLICT (battlefield, round_start, character_id) DO NOTHING;
            """, connection, transaction);
        command.Parameters.AddWithValue("battlefield", battlefield);
        command.Parameters.AddWithValue("start", start);
        command.Parameters.AddWithValue("character", characterId);
        command.Parameters.AddWithValue("faction", (short)faction);
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>Locks the round row; null when it is settled, otherwise the faction totals (Sparta, Athens).</summary>
    private static async Task<(long Sparta, long Athens)?> LockOpenBattlefieldRoundAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, short battlefield, DateTimeOffset start,
        CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT settled_at IS NULL, sparta_points, athens_points FROM public.battlefield_rounds
            WHERE battlefield = @battlefield AND round_start = @start FOR UPDATE;
            """, connection, transaction);
        command.Parameters.AddWithValue("battlefield", battlefield);
        command.Parameters.AddWithValue("start", start);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) && reader.GetBoolean(0) ? (reader.GetInt64(1), reader.GetInt64(2)) : null;
    }

    private static async Task AddBattlefieldFactionPointsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, short battlefield, DateTimeOffset start,
        byte faction, long points, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE public.battlefield_rounds
            SET sparta_points = sparta_points + CASE WHEN @faction = 0 THEN @points ELSE 0 END,
                athens_points = athens_points + CASE WHEN @faction = 1 THEN @points ELSE 0 END
            WHERE battlefield = @battlefield AND round_start = @start;
            """, connection, transaction);
        command.Parameters.AddWithValue("faction", (short)faction);
        command.Parameters.AddWithValue("points", Math.Max(0, points));
        command.Parameters.AddWithValue("battlefield", battlefield);
        command.Parameters.AddWithValue("start", start);
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task RecordBattlefieldParticipantAsync(
        short battlefield, DateTimeOffset roundStart, DateTimeOffset roundEnd, int characterId, byte faction,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenBattlefieldConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var start = await ResolveBattlefieldRoundAsync(
            connection, transaction, battlefield, roundStart, roundEnd, DateTimeOffset.UtcNow, cancellationToken);
        if (await LockOpenBattlefieldRoundAsync(connection, transaction, battlefield, start, cancellationToken) is not null)
        {
            await EnsureBattlefieldParticipantAsync(
                connection, transaction, battlefield, start, characterId, faction, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> RecordBattlefieldKillAsync(
        short battlefield, DateTimeOffset roundStart, DateTimeOffset roundEnd, int killerId, byte killerFaction,
        int victimId, byte victimFaction, int victimLevel, int factionPoints, int personalPoints,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenBattlefieldConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var start = await ResolveBattlefieldRoundAsync(
            connection, transaction, battlefield, roundStart, roundEnd, DateTimeOffset.UtcNow, cancellationToken);
        if (await LockOpenBattlefieldRoundAsync(connection, transaction, battlefield, start, cancellationToken) is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await EnsureBattlefieldParticipantAsync(
            connection, transaction, battlefield, start, killerId, killerFaction, cancellationToken);
        await EnsureBattlefieldParticipantAsync(
            connection, transaction, battlefield, start, victimId, victimFaction, cancellationToken);
        await using (var killer = new NpgsqlCommand("""
            UPDATE public.battlefield_participants
            SET kills = kills + 1, kill_points = kill_points + @points,
                kill_level_sum = kill_level_sum + @victimLevel, updated_at = transaction_timestamp()
            WHERE battlefield = @battlefield AND round_start = @start AND character_id = @killer;
            """, connection, transaction))
        {
            killer.Parameters.AddWithValue("points", (long)Math.Max(0, personalPoints));
            killer.Parameters.AddWithValue("victimLevel", (long)Math.Max(0, victimLevel));
            killer.Parameters.AddWithValue("battlefield", battlefield);
            killer.Parameters.AddWithValue("start", start);
            killer.Parameters.AddWithValue("killer", killerId);
            await killer.ExecuteNonQueryAsync(cancellationToken);
        }
        await AddBattlefieldFactionPointsAsync(
            connection, transaction, battlefield, start, killerFaction, factionPoints, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RecordBattlefieldTaskPointsAsync(
        short battlefield, DateTimeOffset roundStart, DateTimeOffset roundEnd, int characterId, byte faction,
        int holyPoints, int bossPoints, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenBattlefieldConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var start = await ResolveBattlefieldRoundAsync(
            connection, transaction, battlefield, roundStart, roundEnd, DateTimeOffset.UtcNow, cancellationToken);
        if (await LockOpenBattlefieldRoundAsync(connection, transaction, battlefield, start, cancellationToken) is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await EnsureBattlefieldParticipantAsync(
            connection, transaction, battlefield, start, characterId, faction, cancellationToken);
        await AddBattlefieldTaskPointsAsync(
            connection, transaction, battlefield, start, characterId, faction, holyPoints, bossPoints, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task AddBattlefieldTaskPointsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, short battlefield, DateTimeOffset start,
        int characterId, byte faction, int holyPoints, int bossPoints, CancellationToken token)
    {
        await using (var add = new NpgsqlCommand("""
            UPDATE public.battlefield_participants
            SET holy_points = holy_points + @holy, boss_points = boss_points + @boss, updated_at = transaction_timestamp()
            WHERE battlefield = @battlefield AND round_start = @start AND character_id = @character;
            """, connection, transaction))
        {
            add.Parameters.AddWithValue("holy", (long)Math.Max(0, holyPoints));
            add.Parameters.AddWithValue("boss", (long)Math.Max(0, bossPoints));
            add.Parameters.AddWithValue("battlefield", battlefield);
            add.Parameters.AddWithValue("start", start);
            add.Parameters.AddWithValue("character", characterId);
            await add.ExecuteNonQueryAsync(token);
        }
        await AddBattlefieldFactionPointsAsync(connection, transaction, battlefield, start, faction,
            (long)Math.Max(0, holyPoints) + Math.Max(0, bossPoints), token);
    }

    public async Task<BattlefieldRoundTick> AdvanceBattlefieldRoundsAsync(
        DateTimeOffset now, int titleDays, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenBattlefieldConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key);", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("key", BattlefieldRoundLockKey);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var due = new List<(short Battlefield, DateTimeOffset Start, long Sparta, long Athens)>();
        await using (var read = new NpgsqlCommand("""
            SELECT battlefield, round_start, sparta_points, athens_points FROM public.battlefield_rounds
            WHERE settled_at IS NULL AND round_end <= @now ORDER BY round_start FOR UPDATE;
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("now", now);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                due.Add((reader.GetInt16(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetInt64(2),
                    reader.GetInt64(3)));
            }
        }

        var rewarded = 0;
        foreach (var round in due)
        {
            rewarded += await SettleBattlefieldRoundAsync(connection, transaction, round, now, cancellationToken);
        }

        var expired = 0;
        if (titleDays > 0)
        {
            await using var expire = new NpgsqlCommand("""
                DELETE FROM public.character_title_grants
                WHERE granted_by IN (@pindus, @nimini) AND granted_at < @cutoff;
                """, connection, transaction);
            expire.Parameters.AddWithValue("pindus", PindusTitleGrantor);
            expire.Parameters.AddWithValue("nimini", NiMiniTitleGrantor);
            expire.Parameters.AddWithValue("cutoff", now.AddDays(-titleDays));
            expired = await expire.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new BattlefieldRoundTick(due.Count, rewarded, expired);
    }

    private static async Task<int> SettleBattlefieldRoundAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        (short Battlefield, DateTimeOffset Start, long Sparta, long Athens) round, DateTimeOffset now,
        CancellationToken token)
    {
        var rows = new List<(int Character, byte Faction, int Level, long KillPoints, long LevelSum, long Holy,
            long Boss, int CharacterOrder)>();
        await using (var read = new NpgsqlCommand("""
            SELECT p.character_id, p.faction, c.fighter_job_lv, p.kill_points, p.kill_level_sum, p.holy_points,
                   p.boss_points
            FROM public.battlefield_participants p
            JOIN public.character_base c ON c.id = p.character_id
            WHERE p.battlefield = @battlefield AND p.round_start = @start
            ORDER BY p.character_id;
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("battlefield", round.Battlefield);
            read.Parameters.AddWithValue("start", round.Start);
            await using var reader = await read.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                rows.Add((reader.GetInt32(0), (byte)reader.GetInt16(1), reader.GetInt32(2), reader.GetInt64(3),
                    reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), rows.Count));
            }
        }

        // Rankings: kills by kill points then the victims' levels (the IGG note: the scale follows the enemy's
        // level); the other ranking by Holy Water + boss points; the faction ranking (Ni Mini's title) by kill points.
        static Dictionary<int, int> Rank<T>(IEnumerable<T> ordered, Func<T, int> id)
        {
            var ranks = new Dictionary<int, int>();
            foreach (var item in ordered)
            {
                ranks[id(item)] = ranks.Count + 1;
            }
            return ranks;
        }

        var killRanks = Rank(rows.Where(r => r.KillPoints > 0)
            .OrderByDescending(r => r.KillPoints).ThenByDescending(r => r.LevelSum).ThenBy(r => r.Character),
            r => r.Character);
        var taskRanks = Rank(rows.Where(r => r.Holy + r.Boss > 0)
            .OrderByDescending(r => r.Holy + r.Boss).ThenBy(r => r.Character), r => r.Character);
        var factionRanks = new Dictionary<int, int>();
        foreach (var faction in new byte[] { 0, 1 })
        {
            foreach (var (character, rank) in Rank(rows.Where(r => r.Faction == faction && r.KillPoints > 0)
                         .OrderByDescending(r => r.KillPoints).ThenByDescending(r => r.LevelSum)
                         .ThenBy(r => r.Character), r => r.Character))
            {
                factionRanks[character] = rank;
            }
        }

        var winner = BattlefieldRewardPolicy.Winner(round.Sparta, round.Athens);
        foreach (var row in rows)
        {
            var outcome = BattlefieldRewardPolicy.OutcomeFor(row.Faction, round.Sparta, round.Athens);
            var reward = round.Battlefield == BattlefieldRewardPolicy.Pindus
                ? BattlefieldRewardPolicy.PindusReward(row.Level, outcome, row.KillPoints, row.Holy)
                : BattlefieldRewardPolicy.NiMiniReward(outcome);
            await using var insert = new NpgsqlCommand("""
                INSERT INTO public.battlefield_round_rewards
                    (battlefield, round_start, character_id, faction, level, outcome, kill_points, holy_points,
                     boss_points, kill_rank, task_rank, faction_rank, experience, talent_points, reputation,
                     kill_experience, holy_experience)
                VALUES (@battlefield, @start, @character, @faction, @level, @outcome, @kill, @holy, @boss,
                        @killRank, @taskRank, @factionRank, @exp, @talent, @rep, @killExp, @holyExp)
                ON CONFLICT (battlefield, round_start, character_id) DO NOTHING;
                """, connection, transaction);
            insert.Parameters.AddWithValue("battlefield", round.Battlefield);
            insert.Parameters.AddWithValue("start", round.Start);
            insert.Parameters.AddWithValue("character", row.Character);
            insert.Parameters.AddWithValue("faction", (short)row.Faction);
            insert.Parameters.AddWithValue("level", Math.Max(1, row.Level));
            insert.Parameters.AddWithValue("outcome", (short)outcome);
            insert.Parameters.AddWithValue("kill", row.KillPoints);
            insert.Parameters.AddWithValue("holy", row.Holy);
            insert.Parameters.AddWithValue("boss", row.Boss);
            insert.Parameters.AddWithValue("killRank", killRanks.GetValueOrDefault(row.Character));
            insert.Parameters.AddWithValue("taskRank", taskRanks.GetValueOrDefault(row.Character));
            insert.Parameters.AddWithValue("factionRank", factionRanks.GetValueOrDefault(row.Character));
            insert.Parameters.AddWithValue("exp", reward.Experience);
            insert.Parameters.AddWithValue("talent", reward.TalentPoints);
            insert.Parameters.AddWithValue("rep", reward.Reputation);
            insert.Parameters.AddWithValue("killExp", reward.KillExperience);
            insert.Parameters.AddWithValue("holyExp", reward.HolyExperience);
            await insert.ExecuteNonQueryAsync(token);
        }

        await using (var finish = new NpgsqlCommand("""
            UPDATE public.battlefield_rounds SET winner = @winner, settled_at = @now
            WHERE battlefield = @battlefield AND round_start = @start;
            """, connection, transaction))
        {
            finish.Parameters.AddWithValue("winner", winner is { } w ? (object)(short)w : DBNull.Value);
            finish.Parameters.AddWithValue("now", now);
            finish.Parameters.AddWithValue("battlefield", round.Battlefield);
            finish.Parameters.AddWithValue("start", round.Start);
            await finish.ExecuteNonQueryAsync(token);
        }
        return rows.Count;
    }

    public async Task<IReadOnlyList<BattlefieldRewardRecord>> GetBattlefieldRewardsAsync(
        short battlefield, int characterId, DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenBattlefieldConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT r.round_start, b.round_end, r.faction, r.level, r.outcome, r.kill_points, r.holy_points,
                   r.boss_points, r.kill_rank, r.task_rank, r.faction_rank, r.experience, r.talent_points,
                   r.reputation, r.kill_experience, r.holy_experience, r.faction_claimed_at IS NOT NULL,
                   r.points_claimed_at IS NOT NULL, r.titles_claimed
            FROM public.battlefield_round_rewards r
            JOIN public.battlefield_rounds b ON b.battlefield = r.battlefield AND b.round_start = r.round_start
            WHERE r.battlefield = @battlefield AND r.character_id = @character AND r.round_start >= @since
            ORDER BY r.round_start;
            """, connection);
        command.Parameters.AddWithValue("battlefield", battlefield);
        command.Parameters.AddWithValue("character", characterId);
        command.Parameters.AddWithValue("since", since);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<BattlefieldRewardRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new BattlefieldRewardRecord(
                battlefield,
                reader.GetFieldValue<DateTimeOffset>(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                (byte)reader.GetInt16(2),
                reader.GetInt32(3),
                (BattlefieldOutcome)reader.GetInt16(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetInt32(10),
                new BattlefieldReward(reader.GetInt32(11), reader.GetInt32(12), reader.GetInt32(13),
                    reader.GetInt32(14), reader.GetInt32(15)),
                reader.GetBoolean(16),
                reader.GetBoolean(17),
                reader.GetInt16(18)));
        }
        return records;
    }

    public async Task<bool> IsInUnsettledBattlefieldRoundAsync(
        short battlefield, int characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenBattlefieldConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM public.battlefield_participants p
                JOIN public.battlefield_rounds b ON b.battlefield = p.battlefield AND b.round_start = p.round_start
                WHERE p.battlefield = @battlefield AND p.character_id = @character AND b.settled_at IS NULL);
            """, connection);
        command.Parameters.AddWithValue("battlefield", battlefield);
        command.Parameters.AddWithValue("character", characterId);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    public async Task<BattlefieldClaimReceipt?> ClaimBattlefieldRewardAsync(
        short battlefield, DateTimeOffset roundStart, int characterId, BattlefieldClaimPart part, int reputation,
        CancellationToken cancellationToken = default)
    {
        var column = part == BattlefieldClaimPart.Points ? "points_claimed_at" : "faction_claimed_at";
        await using var connection = await OpenBattlefieldConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var mark = new NpgsqlCommand($"""
            UPDATE public.battlefield_round_rewards SET {column} = transaction_timestamp()
            WHERE battlefield = @battlefield AND round_start = @start AND character_id = @character
              AND {column} IS NULL;
            """, connection, transaction))
        {
            mark.Parameters.AddWithValue("battlefield", battlefield);
            mark.Parameters.AddWithValue("start", roundStart);
            mark.Parameters.AddWithValue("character", characterId);
            if (await mark.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }

        BattlefieldClaimReceipt receipt;
        await using (var pay = new NpgsqlCommand("""
            UPDATE public.character_base
            SET medusa_honor_points = LEAST(2147483647, COALESCE(medusa_honor_points, 0)::bigint + @rep)::integer,
                medusa_reward_revision = medusa_reward_revision + CASE WHEN @rep > 0 THEN 1 ELSE 0 END
            WHERE id = @character
            RETURNING COALESCE(medusa_honor_points, 0), medusa_reward_revision;
            """, connection, transaction))
        {
            pay.Parameters.AddWithValue("rep", (long)Math.Max(0, reputation));
            pay.Parameters.AddWithValue("character", characterId);
            await using var reader = await pay.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException($"Battlefield claim for a missing character {characterId}.");
            }
            receipt = new BattlefieldClaimReceipt(reader.GetInt32(0), reader.GetInt64(1));
        }
        await transaction.CommitAsync(cancellationToken);
        return receipt;
    }

    public async Task<BattlefieldClaimReceipt?> UnclaimBattlefieldRewardAsync(
        short battlefield, DateTimeOffset roundStart, int characterId, BattlefieldClaimPart part, int reputation,
        CancellationToken cancellationToken = default)
    {
        var column = part == BattlefieldClaimPart.Points ? "points_claimed_at" : "faction_claimed_at";
        await using var connection = await OpenBattlefieldConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var unmark = new NpgsqlCommand($"""
            UPDATE public.battlefield_round_rewards SET {column} = NULL
            WHERE battlefield = @battlefield AND round_start = @start AND character_id = @character
              AND {column} IS NOT NULL;
            """, connection, transaction))
        {
            unmark.Parameters.AddWithValue("battlefield", battlefield);
            unmark.Parameters.AddWithValue("start", roundStart);
            unmark.Parameters.AddWithValue("character", characterId);
            if (await unmark.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }

        BattlefieldClaimReceipt receipt;
        await using (var refund = new NpgsqlCommand("""
            UPDATE public.character_base
            SET medusa_honor_points = GREATEST(0, COALESCE(medusa_honor_points, 0)::bigint - @rep)::integer,
                medusa_reward_revision = medusa_reward_revision + CASE WHEN @rep > 0 THEN 1 ELSE 0 END
            WHERE id = @character
            RETURNING COALESCE(medusa_honor_points, 0), medusa_reward_revision;
            """, connection, transaction))
        {
            refund.Parameters.AddWithValue("rep", (long)Math.Max(0, reputation));
            refund.Parameters.AddWithValue("character", characterId);
            await using var reader = await refund.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException($"Battlefield unclaim for a missing character {characterId}.");
            }
            receipt = new BattlefieldClaimReceipt(reader.GetInt32(0), reader.GetInt64(1));
        }
        await transaction.CommitAsync(cancellationToken);
        return receipt;
    }

    public async Task<bool> ClaimBattlefieldTitleAsync(
        short battlefield, DateTimeOffset roundStart, int characterId, short flag, int titleId, string grantedBy,
        string note, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenBattlefieldConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var mark = new NpgsqlCommand("""
            UPDATE public.battlefield_round_rewards SET titles_claimed = titles_claimed | @flag
            WHERE battlefield = @battlefield AND round_start = @start AND character_id = @character
              AND titles_claimed & @flag = 0;
            """, connection, transaction))
        {
            mark.Parameters.AddWithValue("flag", flag);
            mark.Parameters.AddWithValue("battlefield", battlefield);
            mark.Parameters.AddWithValue("start", roundStart);
            mark.Parameters.AddWithValue("character", characterId);
            if (await mark.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        // A title the character already holds from somewhere else is left alone; one held from an earlier
        // battlefield claim starts its days again.
        await using (var grant = new NpgsqlCommand("""
            INSERT INTO public.character_title_grants (character_id, title_id, granted_by, note)
            VALUES (@character, @title, @by, @note)
            ON CONFLICT (character_id, title_id) DO UPDATE
                SET granted_at = transaction_timestamp(), note = EXCLUDED.note
                WHERE public.character_title_grants.granted_by = EXCLUDED.granted_by;
            """, connection, transaction))
        {
            grant.Parameters.AddWithValue("character", characterId);
            grant.Parameters.AddWithValue("title", titleId);
            grant.Parameters.AddWithValue("by", grantedBy);
            grant.Parameters.AddWithValue("note", note.Length > 500 ? note[..500] : note);
            await grant.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<BattlefieldStatistics?> GetBattlefieldStatisticsAsync(
        short battlefield, byte faction, int characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenBattlefieldConnectionAsync(cancellationToken);
        DateTimeOffset start;
        long sparta, athens;
        await using (var round = new NpgsqlCommand("""
            SELECT round_start, sparta_points, athens_points FROM public.battlefield_rounds
            WHERE battlefield = @battlefield ORDER BY round_start DESC LIMIT 1;
            """, connection))
        {
            round.Parameters.AddWithValue("battlefield", battlefield);
            await using var reader = await round.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }
            start = reader.GetFieldValue<DateTimeOffset>(0);
            sparta = reader.GetInt64(1);
            athens = reader.GetInt64(2);
        }

        async Task<List<long>> Column(string expression)
        {
            var values = new List<long>();
            await using var command = new NpgsqlCommand($"""
                SELECT {expression} FROM public.battlefield_participants
                WHERE battlefield = @battlefield AND round_start = @start AND faction = @faction AND {expression} > 0
                ORDER BY {expression} DESC, character_id LIMIT 5;
                """, connection);
            command.Parameters.AddWithValue("battlefield", battlefield);
            command.Parameters.AddWithValue("start", start);
            command.Parameters.AddWithValue("faction", (short)faction);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                values.Add(reader.GetInt64(0));
            }
            return values;
        }

        var points = await Column("(kill_points + holy_points + boss_points)");
        var kills = await Column("kills::bigint");
        long own = 0;
        await using (var mine = new NpgsqlCommand("""
            SELECT kill_points + holy_points + boss_points FROM public.battlefield_participants
            WHERE battlefield = @battlefield AND round_start = @start AND character_id = @character;
            """, connection))
        {
            mine.Parameters.AddWithValue("battlefield", battlefield);
            mine.Parameters.AddWithValue("start", start);
            mine.Parameters.AddWithValue("character", characterId);
            own = await mine.ExecuteScalarAsync(cancellationToken) is long value ? value : 0;
        }
        return new BattlefieldStatistics(sparta, athens, points, kills, own);
    }

    /// <summary>
    /// The participant's Holy Water counters for this round (issued, turned in), creating the participant row.
    /// </summary>
    private static async Task<(int Issued, int TurnedIn)> LockHolyWaterCountersAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, short battlefield, DateTimeOffset start,
        int characterId, byte faction, CancellationToken token)
    {
        await EnsureBattlefieldParticipantAsync(connection, transaction, battlefield, start, characterId, faction, token);
        await using var command = new NpgsqlCommand("""
            SELECT holy_issued, holy_turned_in FROM public.battlefield_participants
            WHERE battlefield = @battlefield AND round_start = @start AND character_id = @character FOR UPDATE;
            """, connection, transaction);
        command.Parameters.AddWithValue("battlefield", battlefield);
        command.Parameters.AddWithValue("start", start);
        command.Parameters.AddWithValue("character", characterId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? (reader.GetInt32(0), reader.GetInt32(1)) : (0, 0);
    }

    private static async Task SetHolyWaterCountersAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, short battlefield, DateTimeOffset start,
        int characterId, int issued, int turnedIn, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE public.battlefield_participants
            SET holy_issued = @issued, holy_turned_in = @turnedIn, updated_at = transaction_timestamp()
            WHERE battlefield = @battlefield AND round_start = @start AND character_id = @character;
            """, connection, transaction);
        command.Parameters.AddWithValue("issued", issued);
        command.Parameters.AddWithValue("turnedIn", turnedIn);
        command.Parameters.AddWithValue("battlefield", battlefield);
        command.Parameters.AddWithValue("start", start);
        command.Parameters.AddWithValue("character", characterId);
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<HolyWaterChange> GrantHolyWaterAsync(
        short battlefield, DateTimeOffset roundStart, DateTimeOffset roundEnd, int characterId, byte faction,
        int itemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenBattlefieldConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var start = await ResolveBattlefieldRoundAsync(
            connection, transaction, battlefield, roundStart, roundEnd, DateTimeOffset.UtcNow, cancellationToken);
        if (await LockOpenBattlefieldRoundAsync(connection, transaction, battlefield, start, cancellationToken) is null)
        {
            return new HolyWaterChange(HolyWaterOutcome.RoundClosed, null);
        }
        await using (var lockRow = new NpgsqlCommand(
            "SELECT 1 FROM public.character_base WHERE id = @character FOR UPDATE;", connection, transaction))
        {
            lockRow.Parameters.AddWithValue("character", characterId);
            if (await lockRow.ExecuteScalarAsync(cancellationToken) is null)
            {
                return new HolyWaterChange(HolyWaterOutcome.BagFull, null);
            }
        }

        var (issued, turnedIn) = await LockHolyWaterCountersAsync(
            connection, transaction, battlefield, start, characterId, faction, cancellationToken);
        if (issued > turnedIn)
        {
            // This round's bottle is still out (in the bag, or wherever it was moved): one at a time.
            await transaction.CommitAsync(cancellationToken);
            return new HolyWaterChange(HolyWaterOutcome.AlreadyHolding, null);
        }

        var occupied = new HashSet<short>();
        var holding = false;
        await using (var items = new NpgsqlCommand("""
            SELECT slot_index, prop_id FROM public.character_items
            WHERE user_id = @character AND item_location = @bag FOR UPDATE;
            """, connection, transaction))
        {
            items.Parameters.AddWithValue("character", characterId);
            items.Parameters.AddWithValue("bag", ItemLocationKitBag);
            await using var reader = await items.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                occupied.Add(reader.GetInt16(0));
                holding |= reader.GetInt32(1) == itemId;
            }
        }
        if (holding)
        {
            // A bottle left over from an earlier round becomes this round's one bottle; no second one is added.
            await SetHolyWaterCountersAsync(
                connection, transaction, battlefield, start, characterId, issued + 1, turnedIn, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new HolyWaterChange(HolyWaterOutcome.AlreadyHolding, null);
        }

        short slot = -1;
        for (short candidate = 0; candidate < KitBagItemGrantPlanner.SlotCount; candidate++)
        {
            if (!occupied.Contains(candidate)) { slot = candidate; break; }
        }
        if (slot < 0)
        {
            await transaction.CommitAsync(cancellationToken);   // keeps the participant row
            return new HolyWaterChange(HolyWaterOutcome.BagFull, null);
        }

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO public.character_items (user_id, item_location, slot_index, prop_id, item_quality, item_grade,
                                                bound, stack, item_exp, holy_suit_code)
            VALUES (@character, @bag, @slot, @item, 1, 1, 1, 1, 0, 0);
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("character", characterId);
            insert.Parameters.AddWithValue("bag", ItemLocationKitBag);
            insert.Parameters.AddWithValue("slot", slot);
            insert.Parameters.AddWithValue("item", itemId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await SetHolyWaterCountersAsync(
            connection, transaction, battlefield, start, characterId, issued + 1, turnedIn, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new HolyWaterChange(HolyWaterOutcome.Done, await GetCharacterByIdAsync(characterId, cancellationToken));
    }

    public async Task<HolyWaterChange> TurnInHolyWaterAsync(
        short battlefield, DateTimeOffset roundStart, DateTimeOffset roundEnd, int characterId, byte faction,
        bool hostileAltar, int itemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenBattlefieldConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var start = await ResolveBattlefieldRoundAsync(
            connection, transaction, battlefield, roundStart, roundEnd, DateTimeOffset.UtcNow, cancellationToken);
        // The round row is locked first (the settlement clock locks it too), so the bottle and the points move together
        // or not at all: a round that settles meanwhile refuses the hand-in and the bottle stays in the bag.
        var totals = await LockOpenBattlefieldRoundAsync(connection, transaction, battlefield, start, cancellationToken);
        if (totals is null)
        {
            return new HolyWaterChange(HolyWaterOutcome.RoundClosed, null);
        }
        var (sparta, athens) = totals.Value;

        long? id = null;
        short stack = 0;
        await using (var read = new NpgsqlCommand("""
            SELECT id, stack FROM public.character_items
            WHERE user_id = @character AND item_location = @bag AND prop_id = @item
            ORDER BY slot_index LIMIT 1 FOR UPDATE;
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("character", characterId);
            read.Parameters.AddWithValue("bag", ItemLocationKitBag);
            read.Parameters.AddWithValue("item", itemId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                id = reader.GetInt64(0);
                stack = reader.GetInt16(1);
            }
        }
        if (id is null)
        {
            return new HolyWaterChange(HolyWaterOutcome.NoneHeld, null);
        }

        // Only a bottle issued in THIS round scores: stockpiled bottles (an earlier round's, or banked in the
        // warehouse) can never be dumped for points. One hand-in per bottle the Quartermaster issued this round.
        var (issued, turnedIn) = await LockHolyWaterCountersAsync(
            connection, transaction, battlefield, start, characterId, faction, cancellationToken);
        if (turnedIn >= issued)
        {
            await transaction.CommitAsync(cancellationToken);   // keeps the participant row
            return new HolyWaterChange(HolyWaterOutcome.NotIssuedThisRound, null);
        }

        var own = faction == 0 ? sparta : athens;
        var other = faction == 0 ? athens : sparta;
        var points = BattlefieldRewardPolicy.HolyWaterPoints(hostileAltar, own, other);
        await using (var take = new NpgsqlCommand(stack > 1
            ? "UPDATE public.character_items SET stack = stack - 1, updated_at = transaction_timestamp() WHERE id = @id;"
            : "DELETE FROM public.character_items WHERE id = @id;", connection, transaction))
        {
            take.Parameters.AddWithValue("id", id.Value);
            await take.ExecuteNonQueryAsync(cancellationToken);
        }
        await SetHolyWaterCountersAsync(
            connection, transaction, battlefield, start, characterId, issued, turnedIn + 1, cancellationToken);
        await AddBattlefieldTaskPointsAsync(
            connection, transaction, battlefield, start, characterId, faction, points, 0, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new HolyWaterChange(HolyWaterOutcome.Done, await GetCharacterByIdAsync(characterId, cancellationToken), points);
    }

    public async Task<FarmRewardStatus?> GetLatestFarmRewardAsync(
        int characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT round_start, outcome, character_experience, rank_overall, claimed_at IS NOT NULL
            FROM public.lelantine_farm_round_rewards
            WHERE character_id = @character ORDER BY round_start DESC LIMIT 1;
            """, connection);
        command.Parameters.AddWithValue("character", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new FarmRewardStatus(reader.GetFieldValue<DateTimeOffset>(0), (FarmRoundOutcome)reader.GetInt16(1),
            reader.GetInt32(2), reader.GetInt32(3), reader.GetBoolean(4));
    }
}
