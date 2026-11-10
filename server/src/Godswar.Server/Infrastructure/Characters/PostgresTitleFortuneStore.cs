using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Characters;

internal enum FortuneTellerStatus
{
    Done,
    Refused,
    CharacterNotFound
}

/// <summary>The row as the Fortune Teller left it.</summary>
internal readonly record struct FortuneTellerResult(
    FortuneTellerStatus Status,
    int Line,
    byte Sign = 0,
    int LuckyStatus = 0,
    DateTimeOffset? LuckyExpiresAt = null,
    int Gold = 0,
    int GoldPaid = 0);

/// <summary>
/// TITLES: title grants (Title Expert and quest titles) and the Fortune Teller's sign change and Lucky
/// Day. Titles go to the existing <c>character_title_grants</c> table (granted_by <c>title-expert</c> or
/// <c>quest-title</c>), which the login snapshot and the 10198 title selection already read through
/// <c>character_title_entitlements</c>. The Fortune Teller is one ownership-fenced transaction: lock the
/// row, count this window's free uses in <c>character_fortune_teller_log</c>, decide, take the gold,
/// write <c>zodiac_type</c> or <c>zodiac_lucky_status/expires_at</c>, log.
/// </summary>
/// <remarks>
/// The log table is created on first use with idempotent DDL (the server_rates / economy pattern), not as
/// a registered migration, so an older binary still starts after a rollback. <c>migration.sql</c> holds
/// the same DDL to run before release with nobody online.
/// </remarks>
internal sealed class PostgresTitleFortuneStore(NpgsqlDataSource dataSource)
{
    internal const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS public.character_fortune_teller_log (
            id bigserial PRIMARY KEY,
            character_id integer NOT NULL REFERENCES public.character_base(id) ON DELETE CASCADE,
            action text NOT NULL CHECK (action IN ('sign-free','sign-gold','lucky-free','lucky-gold')),
            gold_paid integer NOT NULL DEFAULT 0 CHECK (gold_paid >= 0),
            from_sign smallint NOT NULL,
            to_sign smallint NOT NULL,
            lucky_expires_at timestamptz NULL,
            created_at timestamptz NOT NULL DEFAULT transaction_timestamp()
        );
        CREATE INDEX IF NOT EXISTS character_fortune_teller_log_free
            ON public.character_fortune_teller_log (character_id, action, created_at);
        COMMENT ON TABLE public.character_fortune_teller_log IS
            'TITLES build: Fortune Teller sign changes and Lucky Days; free uses per week/month are counted from it.';
        """;

    private readonly NpgsqlDataSource _dataSource = dataSource ??
        throw new ArgumentNullException(nameof(dataSource));
    private readonly PostgresPlayerOwnershipGuard _ownershipGuard = new(dataSource);
    private int _schemaReady;

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _schemaReady) == 1)
        {
            return;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using (var exists = new NpgsqlCommand(
            "SELECT to_regclass('public.character_fortune_teller_log') IS NOT NULL;", connection))
        {
            if (await exists.ExecuteScalarAsync(cancellationToken) is not true)
            {
                await using var create = new NpgsqlCommand(SchemaSql, connection);
                await create.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        Volatile.Write(ref _schemaReady, 1);
    }

    /// <summary>Inserts the titles not yet granted; returns the ones this call added.</summary>
    public async Task<IReadOnlyList<uint>> GrantTitlesAsync(
        int characterId, IReadOnlyList<uint> titleIds, string grantedBy, string note,
        CancellationToken cancellationToken)
    {
        if (titleIds.Count == 0)
        {
            return [];
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_title_grants (character_id, title_id, granted_by, note)
            SELECT @character, t.id, @by, @note
            FROM unnest(@titles) AS t(id)
            WHERE EXISTS (SELECT 1 FROM public.editor_title_catalog c WHERE c.title_id = t.id)
              AND NOT EXISTS (SELECT 1 FROM public.character_title_entitlements e
                              WHERE e.character_id = @character AND e.title_id = t.id)
            ON CONFLICT (character_id, title_id) DO NOTHING
            RETURNING title_id;
            """,
            connection);
        command.Parameters.AddWithValue("character", characterId);
        command.Parameters.AddWithValue("titles", titleIds.Select(static id => checked((int)id)).ToArray());
        command.Parameters.AddWithValue("by", grantedBy);
        command.Parameters.AddWithValue("note", note.Length > 500 ? note[..500] : note);
        var granted = new List<uint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            granted.Add((uint)reader.GetInt32(0));
        }

        return granted;
    }

    public async Task<FortuneTellerResult> ApplyFortuneAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        FortuneTellerAction action,
        byte targetSign,
        int price,
        DateTimeOffset now,
        TimeSpan realmOffset,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var fence = await _ownershipGuard.LockCurrentAsync(
            connection, transaction, subject, ownership, cancellationToken);
        if (fence.Status == PlayerOwnershipValidationStatus.CharacterNotFound)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(FortuneTellerStatus.CharacterNotFound, FortuneTellerPolicy.IncorrectLine);
        }

        fence.RequireCurrent();
        string name;
        FortuneTellerPolicy.FortuneState state;
        await using (var read = new NpgsqlCommand(
            """
            SELECT name, zodiac_type, zodiac_lucky_status, zodiac_lucky_expires_at, "Stone",
                   EXISTS (SELECT 1 FROM public.character_fortune_teller_log l
                           WHERE l.character_id = @id AND l.action = @free AND l.created_at >= @window)
            FROM public.character_base
            WHERE id = @id AND account_id = @account AND lifecycle_state = 'active'
            FOR UPDATE;
            """,
            connection,
            transaction))
        {
            read.Parameters.AddWithValue("id", subject.CharacterId);
            read.Parameters.AddWithValue("account", subject.AccountId);
            read.Parameters.AddWithValue(
                "free",
                action is FortuneTellerAction.FreeSignChange or FortuneTellerAction.GoldSignChange
                    ? "sign-free"
                    : "lucky-free");
            read.Parameters.Add(new NpgsqlParameter("window", NpgsqlDbType.TimestampTz)
            {
                Value = FortuneTellerPolicy.FreeWindowStart(action, now, realmOffset).UtcDateTime
            });
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.DisposeAsync();
                await transaction.RollbackAsync(cancellationToken);
                return new(FortuneTellerStatus.CharacterNotFound, FortuneTellerPolicy.IncorrectLine);
            }

            name = reader.GetString(0);
            state = new(
                (byte)Math.Clamp((int)reader.GetInt16(1), 0, 255),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetInt32(4),
                reader.GetBoolean(5));
        }

        var decision = FortuneTellerPolicy.Decide(action, targetSign, state, price, now);
        if (!decision.ChangesSign && !decision.StartsLucky)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(FortuneTellerStatus.Refused, decision.Line, state.Sign, state.LuckyStatus,
                state.LuckyExpiresAt, state.Gold);
        }

        var newSign = decision.ChangesSign ? targetSign : state.Sign;
        var luckyStatus = decision.StartsLucky ? 1 : state.LuckyStatus;
        var luckyExpires = decision.StartsLucky ? now + FortuneTellerPolicy.LuckyDuration : state.LuckyExpiresAt;
        await using (var update = new NpgsqlCommand(
            """
            UPDATE public.character_base
            SET zodiac_type = @sign,
                zodiac_lucky_status = @lucky,
                zodiac_lucky_expires_at = @expires,
                "Stone" = "Stone" - @cost,
                wallet_revision = wallet_revision + CASE WHEN @cost > 0 THEN 1 ELSE 0 END
            WHERE id = @id AND "Stone" >= @cost;
            """,
            connection,
            transaction))
        {
            update.Parameters.AddWithValue("sign", (short)newSign);
            update.Parameters.AddWithValue("lucky", luckyStatus);
            update.Parameters.Add(new NpgsqlParameter("expires", NpgsqlDbType.TimestampTz)
            {
                Value = luckyExpires is { } expires ? expires.UtcDateTime : DBNull.Value
            });
            update.Parameters.AddWithValue("cost", decision.GoldCost);
            update.Parameters.AddWithValue("id", subject.CharacterId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(FortuneTellerStatus.Refused, FortuneTellerPolicy.NotEnoughGoldLine, state.Sign,
                    state.LuckyStatus, state.LuckyExpiresAt, state.Gold);
            }
        }

        await using (var log = new NpgsqlCommand(
            """
            INSERT INTO public.character_fortune_teller_log
                (character_id, action, gold_paid, from_sign, to_sign, lucky_expires_at, created_at)
            VALUES (@id, @action, @cost, @from, @to, @expires, @now);
            INSERT INTO public.operator_log (actor_account_id, actor_character, command, target, detail)
            VALUES (@account, @name, 'fortune-teller', @target, @detail);
            """,
            connection,
            transaction))
        {
            log.Parameters.AddWithValue("id", subject.CharacterId);
            log.Parameters.AddWithValue("action", FortuneTellerPolicy.LogAction(action));
            log.Parameters.AddWithValue("cost", decision.GoldCost);
            log.Parameters.AddWithValue("from", (short)state.Sign);
            log.Parameters.AddWithValue("to", (short)newSign);
            log.Parameters.Add(new NpgsqlParameter("expires", NpgsqlDbType.TimestampTz)
            {
                Value = decision.StartsLucky && luckyExpires is { } lucky ? lucky.UtcDateTime : DBNull.Value
            });
            log.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            log.Parameters.AddWithValue("account", subject.AccountId);
            log.Parameters.AddWithValue("name", name);
            log.Parameters.AddWithValue("target", subject.CharacterId.ToString());
            log.Parameters.AddWithValue(
                "detail",
                $"{FortuneTellerPolicy.LogAction(action)} sign {state.Sign}->{newSign} gold={decision.GoldCost}" +
                (decision.StartsLucky ? $" lucky-until={luckyExpires:O}" : string.Empty));
            await log.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new(FortuneTellerStatus.Done, decision.Line, newSign, luckyStatus, luckyExpires,
            state.Gold - decision.GoldCost, decision.GoldCost);
    }
}

/// <summary>Start-up wiring (from CharacterIdentityRuntime); never stops the server.</summary>
internal static class TitleFortuneControls
{
    public static PostgresTitleFortuneStore? Store { get; set; }

    public static void Initialize(string connectionString)
    {
        try
        {
            Store = new PostgresTitleFortuneStore(NpgsqlDataSource.Create(connectionString));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine(
                "[titles] start-up failed; Title Expert and Fortune Teller unavailable: " + error.Message);
        }
    }
}
