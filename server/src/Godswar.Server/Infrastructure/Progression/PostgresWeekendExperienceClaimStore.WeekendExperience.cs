using Godswar.Server.State;
using System.Globalization;
using Godswar.Server.Application.Progression;
using Godswar.Server.Domain.World.Instances;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Progression;

internal sealed partial class PostgresWeekendExperienceClaimStore
{
    public async Task<WeekendExperienceClaimStatus>
        ClaimWeekendExperienceAsync(
            int accountId,
            int characterId,
            RealmId realmId,
            DateOnly claimDay,
            DateTimeOffset claimedAtUtc,
            CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(characterId);
        if (!realmId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(realmId));
        }
        if (!WeekendExperienceClaimRules.IsWeekend(claimDay))
        {
            throw new ArgumentOutOfRangeException(
                nameof(claimDay),
                "Weekend EXP can only be claimed on Saturday or Sunday.");
        }
        if (claimedAtUtc == default ||
            claimedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The claim timestamp must be non-default UTC.",
                nameof(claimedAtUtc));
        }

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(ClaimSql, connection);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("realmId", realmId.Value);
        command.Parameters.AddWithValue(
            "source",
            "weekend:" + claimDay.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture));
        command.Parameters.Add(
            "claimedAt",
            NpgsqlDbType.TimestampTz).Value = claimedAtUtc.UtcDateTime;
        command.Parameters.AddWithValue(
            "remainingOnlineTicks",
            WeekendExperienceClaimRules.Duration.Ticks);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is int status &&
            Enum.IsDefined(typeof(WeekendExperienceClaimStatus), status)
                ? (WeekendExperienceClaimStatus)status
                : throw new InvalidDataException(
                    "The weekend EXP claim returned an invalid status.");
    }

    private const string ClaimSql =
        """
        WITH eligible_character AS MATERIALIZED (
            SELECT id
            FROM public.character_base
            WHERE account_id = @accountId
              AND id = @characterId
              AND server_id = @realmId
              AND lifecycle_state = 'active'
        ),
        claimed AS (
            INSERT INTO public.character_experience_modifiers AS modifier (
                character_id,
                status_id,
                kind,
                bonus_basis_points,
                priority,
                source,
                activated_at,
                expires_at,
                remaining_online_ticks
            )
            SELECT
                id,
                511,
                22,
                20000,
                1,
                @source,
                @claimedAt,
                @claimedAt + INTERVAL '8 hours',
                @remainingOnlineTicks
            FROM eligible_character
            ON CONFLICT (character_id, kind) DO UPDATE
            SET status_id = EXCLUDED.status_id,
                bonus_basis_points = EXCLUDED.bonus_basis_points,
                priority = EXCLUDED.priority,
                source = EXCLUDED.source,
                activated_at = EXCLUDED.activated_at,
                expires_at = EXCLUDED.expires_at,
                remaining_online_ticks = EXCLUDED.remaining_online_ticks
            WHERE modifier.source IS DISTINCT FROM EXCLUDED.source
            RETURNING 1
        )
        SELECT CASE
            WHEN NOT EXISTS (SELECT 1 FROM eligible_character) THEN 0
            WHEN EXISTS (SELECT 1 FROM claimed) THEN 1
            ELSE 2
        END;
        """;
}
