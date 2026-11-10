using Npgsql;

namespace Godswar.Server.Infrastructure.Guilds;

/// <summary>What a Member Advisor claim did.</summary>
internal enum GuildDoubleClaimOutcome
{
    Granted,
    NotAMember,
    NotEnoughTime,
    StillActive
}

/// <summary>A member's double EXP / TP week as the advisor reports it.</summary>
internal readonly record struct GuildDoubleWeek(
    bool InGuild,
    int ExperienceHoursUsed,
    int TalentHoursUsed,
    bool ExperienceActive = false,
    bool TalentActive = false);

/// <summary>
/// GUILD (2026-11-03): the guild services the IGG guide describes
/// (igg-acid=67 "Guild Quests" and "Guild Welfare"): the Member Advisor's weekly
/// double EXP / TP claims and the Quest Supervisor's daily quest release.
/// </summary>
/// <remarks>
/// <para>
/// Two small tables, created on first use with idempotent DDL (the pattern the
/// economy and server_rates code use) instead of a registered migration, so an
/// older binary still starts against a database this build has used and the
/// patch can be rolled back alone. <c>Development/build/guild/migration.sql</c>
/// holds the same DDL for running ahead of the release. No foreign keys to
/// <c>character_base</c>, so creating them never locks the live character table.
/// </para>
/// <para>
/// The boost itself is an ordinary <c>character_experience_modifiers</c> row
/// (kind 100 = guild double EXP, 101 = guild double TP, the client's own
/// <c>Status.ini</c> kinds), counted down by online time exactly like the
/// weekend EXP claim.
/// </para>
/// </remarks>
internal sealed partial class PostgresGuildStore
{
    internal const string ServicesSchemaSql =
        """
        CREATE TABLE IF NOT EXISTS public.guild_double_claims (
            character_id integer PRIMARY KEY,
            week_start date NOT NULL,
            experience_hours integer NOT NULL DEFAULT 0
                CHECK (experience_hours >= 0),
            talent_hours integer NOT NULL DEFAULT 0
                CHECK (talent_hours >= 0),
            updated_at timestamptz NOT NULL DEFAULT now()
        );
        CREATE TABLE IF NOT EXISTS public.guild_quest_releases (
            guild_id bigint NOT NULL,
            quest_kind smallint NOT NULL CHECK (quest_kind IN (1, 2)),
            release_day date NOT NULL,
            released_by integer NOT NULL,
            released_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (guild_id, quest_kind, release_day)
        );
        """;

    private static readonly SemaphoreSlim ServicesSchemaGate = new(1, 1);

    private static volatile bool _servicesSchemaReady;

    private async Task EnsureServicesSchemaAsync(CancellationToken cancellationToken)
    {
        if (_servicesSchemaReady)
        {
            return;
        }

        await ServicesSchemaGate.WaitAsync(cancellationToken);
        try
        {
            if (_servicesSchemaReady)
            {
                return;
            }

            await using var connection = await _dataSource.OpenConnectionAsync(
                cancellationToken);
            await using var command = new NpgsqlCommand(ServicesSchemaSql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
            _servicesSchemaReady = true;
        }
        finally
        {
            ServicesSchemaGate.Release();
        }
    }

    /// <summary>The member's double hours used this week (0 for a new week).</summary>
    public async Task<GuildDoubleWeek> ReadDoubleWeekAsync(
        int characterId,
        DateOnly weekStart,
        CancellationToken cancellationToken)
    {
        await EnsureServicesSchemaAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(
            cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT
                EXISTS (SELECT 1 FROM public.guild_members
                        WHERE character_id = @characterId),
                COALESCE((SELECT experience_hours FROM public.guild_double_claims
                          WHERE character_id = @characterId
                            AND week_start = @weekStart), 0),
                COALESCE((SELECT talent_hours FROM public.guild_double_claims
                          WHERE character_id = @characterId
                            AND week_start = @weekStart), 0),
                EXISTS (SELECT 1 FROM public.character_experience_modifiers m
                        WHERE m.character_id = @characterId AND m.kind = 100
                          AND COALESCE(m.remaining_online_ticks, 1) > 0),
                EXISTS (SELECT 1 FROM public.character_experience_modifiers m
                        WHERE m.character_id = @characterId AND m.kind = 101
                          AND COALESCE(m.remaining_online_ticks, 1) > 0);
            """,
            connection);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("weekStart", weekStart);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new GuildDoubleWeek(
            reader.GetBoolean(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetBoolean(3),
            reader.GetBoolean(4));
    }

    /// <summary>
    /// Claims <paramref name="hours"/> of double EXP (<paramref name="talent"/>
    /// false) or double TP out of this week's allowance, in one transaction.
    /// </summary>
    public async Task<GuildDoubleClaimOutcome> ClaimDoubleAsync(
        int characterId,
        bool talent,
        int hours,
        int statusId,
        int weeklyAllowance,
        DateOnly weekStart,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hours);
        await EnsureServicesSchemaAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            cancellationToken);
        var kind = talent ? 101 : 100;
        int used;
        bool member;
        bool active;
        await using (var upsert = new NpgsqlCommand(
            """
            INSERT INTO public.guild_double_claims (character_id, week_start)
            VALUES (@characterId, @weekStart)
            ON CONFLICT (character_id) DO UPDATE
            SET week_start = EXCLUDED.week_start,
                experience_hours = CASE
                    WHEN guild_double_claims.week_start = EXCLUDED.week_start
                    THEN guild_double_claims.experience_hours ELSE 0 END,
                talent_hours = CASE
                    WHEN guild_double_claims.week_start = EXCLUDED.week_start
                    THEN guild_double_claims.talent_hours ELSE 0 END;
            """,
            connection,
            transaction))
        {
            upsert.Parameters.AddWithValue("characterId", characterId);
            upsert.Parameters.AddWithValue("weekStart", weekStart);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var read = new NpgsqlCommand(
            """
            SELECT
                CASE WHEN @talent THEN c.talent_hours ELSE c.experience_hours END,
                EXISTS (SELECT 1 FROM public.guild_members
                        WHERE character_id = @characterId),
                EXISTS (
                    SELECT 1 FROM public.character_experience_modifiers m
                    WHERE m.character_id = @characterId
                      AND m.kind = @kind
                      AND COALESCE(
                          m.remaining_online_ticks,
                          CASE WHEN m.expires_at IS NULL THEN 1
                               ELSE GREATEST(0, ROUND(EXTRACT(EPOCH FROM (
                                   m.expires_at - m.activated_at)) * 10000000)::bigint)
                          END) > 0)
            FROM public.guild_double_claims c
            WHERE c.character_id = @characterId
            FOR UPDATE OF c;
            """,
            connection,
            transaction))
        {
            read.Parameters.AddWithValue("characterId", characterId);
            read.Parameters.AddWithValue("weekStart", weekStart);
            read.Parameters.AddWithValue("talent", talent);
            read.Parameters.AddWithValue("kind", kind);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            used = reader.GetInt32(0);
            member = reader.GetBoolean(1);
            active = reader.GetBoolean(2);
        }

        var outcome = !member
            ? GuildDoubleClaimOutcome.NotAMember
            : active
                ? GuildDoubleClaimOutcome.StillActive
                : used + hours > weeklyAllowance
                    ? GuildDoubleClaimOutcome.NotEnoughTime
                    : GuildDoubleClaimOutcome.Granted;
        if (outcome != GuildDoubleClaimOutcome.Granted)
        {
            await transaction.RollbackAsync(cancellationToken);
            return outcome;
        }

        await using (var grant = new NpgsqlCommand(
            """
            INSERT INTO public.character_experience_modifiers AS modifier (
                character_id, status_id, kind, bonus_basis_points, priority,
                source, activated_at, expires_at, remaining_online_ticks)
            VALUES (
                @characterId, @statusId, @kind, 10000, 1, @source,
                @now, @now + make_interval(hours => @hours), @ticks)
            ON CONFLICT (character_id, kind) DO UPDATE
            SET status_id = EXCLUDED.status_id,
                bonus_basis_points = EXCLUDED.bonus_basis_points,
                priority = EXCLUDED.priority,
                source = EXCLUDED.source,
                activated_at = EXCLUDED.activated_at,
                expires_at = EXCLUDED.expires_at,
                remaining_online_ticks = EXCLUDED.remaining_online_ticks;
            UPDATE public.guild_double_claims
            SET experience_hours = experience_hours + CASE WHEN @talent THEN 0 ELSE @hours END,
                talent_hours = talent_hours + CASE WHEN @talent THEN @hours ELSE 0 END,
                updated_at = @now
            WHERE character_id = @characterId;
            """,
            connection,
            transaction))
        {
            grant.Parameters.AddWithValue("characterId", characterId);
            grant.Parameters.AddWithValue("statusId", statusId);
            grant.Parameters.AddWithValue("kind", kind);
            grant.Parameters.AddWithValue(
                "source",
                $"guild:{(talent ? "tp" : "exp")}:{weekStart:yyyy-MM-dd}");
            grant.Parameters.AddWithValue("now", now);
            grant.Parameters.AddWithValue("hours", hours);
            grant.Parameters.AddWithValue("ticks", TimeSpan.FromHours(hours).Ticks);
            grant.Parameters.AddWithValue("talent", talent);
            await grant.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        Console.WriteLine(
            $"[guild] double claim character={characterId} " +
            $"kind={(talent ? "tp" : "exp")} hours={hours} status={statusId} " +
            $"used={used + hours}/{weeklyAllowance} week={weekStart:yyyy-MM-dd}");
        return GuildDoubleClaimOutcome.Granted;
    }

    /// <summary>
    /// Records an officer's release of one guild quest kind for the day.
    /// </summary>
    /// <returns>The guild id, or null when the character is in no guild.</returns>
    public async Task<long?> ReleaseGuildQuestAsync(
        int characterId,
        int questKind,
        DateOnly day,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await EnsureServicesSchemaAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(
            cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.guild_quest_releases (
                guild_id, quest_kind, release_day, released_by, released_at)
            SELECT guild_id, @kind, @day, @characterId, @now
            FROM public.guild_members
            WHERE character_id = @characterId
            ON CONFLICT (guild_id, quest_kind, release_day) DO NOTHING;
            """,
            connection);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("kind", (short)questKind);
        command.Parameters.AddWithValue("day", day);
        command.Parameters.AddWithValue("now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await using var guild = new NpgsqlCommand(
            "SELECT guild_id FROM public.guild_members WHERE character_id = @characterId;",
            connection);
        guild.Parameters.AddWithValue("characterId", characterId);
        var value = await guild.ExecuteScalarAsync(cancellationToken);
        return value is long guildId ? guildId : null;
    }

    /// <summary>Whether the character is in a guild.</summary>
    public async Task<bool> IsGuildMemberAsync(
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(
            cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM public.guild_members WHERE character_id = @characterId);",
            connection);
        command.Parameters.AddWithValue("characterId", characterId);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <summary>Whether the character's guild released this quest kind today.</summary>
    public async Task<bool> IsGuildQuestReleasedAsync(
        int characterId,
        int questKind,
        DateOnly day,
        CancellationToken cancellationToken)
    {
        await EnsureServicesSchemaAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(
            cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM public.guild_members m
                JOIN public.guild_quest_releases r
                  ON r.guild_id = m.guild_id
                WHERE m.character_id = @characterId
                  AND r.quest_kind = @kind
                  AND r.release_day = @day);
            """,
            connection);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("kind", (short)questKind);
        command.Parameters.AddWithValue("day", day);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <summary>
    /// Pays a guild quest's guild half: Guild Gold and Guild Fund (silver) into
    /// the guild purse, Contribution to the member's spendable guild
    /// contribution (what offerings spend). One transaction.
    /// </summary>
    /// <returns>False when the character is no longer in a guild.</returns>
    public async Task<bool> CreditGuildQuestAsync(
        int characterId,
        long guildGold,
        long guildFund,
        int contribution,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            WITH member AS (
                SELECT guild_id FROM public.guild_members
                WHERE character_id = @characterId
            ),
            purse AS (
                UPDATE public.guilds g
                SET gold = g.gold + @gold,
                    silver = g.silver + @fund,
                    updated_at = @now
                FROM member
                WHERE g.id = member.guild_id
                RETURNING g.id
            )
            UPDATE public.character_base c
            SET consortia_contribute = c.consortia_contribute + @contribution
            FROM purse
            WHERE c.id = @characterId
            RETURNING purse.id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("gold", guildGold);
        command.Parameters.AddWithValue("fund", guildFund);
        command.Parameters.AddWithValue("contribution", contribution);
        command.Parameters.AddWithValue("now", now);
        var paid = await command.ExecuteScalarAsync(cancellationToken) is long;
        await transaction.CommitAsync(cancellationToken);
        return paid;
    }
}
