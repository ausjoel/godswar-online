using System.Collections.Immutable;
using System.Data;
using Godswar.Server.Application.Progression;
using Godswar.Server.Infrastructure.Database;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Progression;

internal sealed class PostgresExperienceBoostStateReader :
    IExperienceBoostStateReader
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string? _gameplayContentRevision;

    public PostgresExperienceBoostStateReader(
        NpgsqlDataSource dataSource,
        string? gameplayContentRevision = null)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
        _gameplayContentRevision =
            PostgresGameplayContentBinding.ValidateOptional(
                gameplayContentRevision);
    }

    public async Task<ExperienceBoostSnapshot> ReadAsync(
        ExperienceBoostReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ExperienceBoostContract.ValidateRequest(request);
        request = request with
        {
            ReadAtUtc = CanonicalizeUtc(request.ReadAtUtc)
        };
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);
        await SetReadOnlyAsync(
            connection,
            transaction,
            cancellationToken);

        var boosts = await ReadBoostsAsync(
            connection,
            transaction,
            request,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var snapshot = new ExperienceBoostSnapshot(
            boosts
                .OrderBy(static boost => boost.Kind)
                .ToImmutableArray());
        ExperienceBoostContract.ValidateSnapshot(
            snapshot,
            request.ReadAtUtc);
        return snapshot;
    }

    private async Task<List<ExperienceBoostEntry>> ReadBoostsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExperienceBoostReadRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            BoostProjectionQuery,
            connection,
            transaction);
        AddReadParameters(command, request);
        PostgresGameplayContentBinding.AddParameter(
            command,
            _gameplayContentRevision);
        var boosts = new List<ExperienceBoostEntry>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (boosts.Count >=
                ExperienceBoostContract.MaximumActiveBoosts)
            {
                throw new InvalidDataException(
                    "The PostgreSQL experience-boost projection exceeded its row bound.");
            }

            var expiresAt = reader.IsDBNull(5)
                ? (DateTimeOffset?)null
                : AddTicksChecked(
                    request.ReadAtUtc,
                    reader.GetInt64(5));
            boosts.Add(reader.GetInt16(0) switch
            {
                ProjectionTypes.Standard =>
                    new ExperienceBoostEntry(
                        reader.GetInt32(1),
                        reader.GetInt32(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        expiresAt,
                        reader.GetString(6)),
                ProjectionTypes.Donator =>
                    CreateDonatorBoost(
                        (DonatorTier)reader.GetInt16(7),
                        expiresAt),
                ProjectionTypes.BattlePass =>
                    new ExperienceBoostEntry(
                        ExperienceStatusIds.PremiumBattlePass,
                        ExperienceBoostKinds.BattlePass,
                        BattlePassBenefits.ExperienceBonusBasisPoints,
                        1,
                        expiresAt,
                        "entitlement:battle_pass"),
                var projectionType => throw new InvalidDataException(
                    $"Unknown PostgreSQL experience-boost projection type {projectionType}.")
            });
        }

        return boosts;
    }

    private static ExperienceBoostEntry CreateDonatorBoost(
        DonatorTier tier,
        DateTimeOffset? expiresAt)
    {
        if (tier is < DonatorTier.KijinPatron or
            > DonatorTier.OctagramPatron)
        {
            throw new InvalidDataException(
                $"Unknown PostgreSQL donator tier {(short)tier}.");
        }

        return new ExperienceBoostEntry(
            DonatorBenefits.StatusId(tier),
            ExperienceBoostKinds.Donator,
            DonatorBenefits.ExperienceBonusBasisPoints(tier),
            (int)tier,
            expiresAt,
            $"donator:{tier.ToString().ToLowerInvariant()}");
    }

    private static void AddReadParameters(
        NpgsqlCommand command,
        ExperienceBoostReadRequest request)
    {
        command.Parameters.AddWithValue("accountId", request.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            request.CharacterId);
        command.Parameters.AddWithValue("camp", (short)request.Camp);
        command.Parameters.AddWithValue("mapId", request.MapId);
        command.Parameters.Add(
            "readAt",
            NpgsqlDbType.TimestampTz).Value =
            request.ReadAtUtc.UtcDateTime;
    }

    private static async Task SetReadOnlyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SET TRANSACTION READ ONLY;",
            connection,
            transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DateTimeOffset AddTicksChecked(
        DateTimeOffset readAtUtc,
        long remainingTicks)
    {
        if (remainingTicks <= 0)
        {
            throw new InvalidDataException(
                "An active experience boost has no remaining duration.");
        }

        try
        {
            return readAtUtc.AddTicks(remainingTicks);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidDataException(
                "An experience-boost duration exceeds the timestamp range.",
                ex);
        }
    }

    private static DateTimeOffset CanonicalizeUtc(DateTimeOffset value)
    {
        var ticks = value.UtcDateTime.Ticks;
        return new DateTimeOffset(ticks - ticks % 10, TimeSpan.Zero);
    }

    private static class ProjectionTypes
    {
        public const short Standard = 0;
        public const short Donator = 1;
        public const short BattlePass = 2;
    }

    private const string BoostProjectionQuery =
        """
        WITH requested_character AS (
            SELECT
                character.id,
                character.account_id,
                character.server_id,
                account.donator_tier,
                account.donator_expires_at
            FROM public.character_base character
            JOIN public.accounts account
              ON account.id = character.account_id
            WHERE character.account_id = @accountId
              AND character.id = @characterId
              AND character.lifecycle_state = 'active'
        ),
        personal AS (
            SELECT DISTINCT ON (modifier.kind)
                modifier.status_id,
                modifier.kind,
                modifier.bonus_basis_points,
                modifier.priority,
                COALESCE(
                    modifier.remaining_online_ticks,
                    CASE
                        WHEN modifier.expires_at IS NULL THEN NULL
                        ELSE GREATEST(
                            0,
                            ROUND(EXTRACT(EPOCH FROM (
                                modifier.expires_at -
                                modifier.activated_at
                            )) * 10000000)::bigint
                        )
                    END
                ) AS remaining_online_ticks,
                modifier.source
            FROM public.character_experience_modifiers modifier
            JOIN requested_character character
              ON character.id = modifier.character_id
            WHERE modifier.activated_at <= @readAt
              AND modifier.kind NOT IN (1008, 1009, 1010)
              AND (
                  modifier.expires_at IS NULL
                  AND modifier.remaining_online_ticks IS NULL
                  OR COALESCE(
                      modifier.remaining_online_ticks,
                      GREATEST(
                          0,
                          ROUND(EXTRACT(EPOCH FROM (
                              modifier.expires_at -
                              modifier.activated_at
                          )) * 10000000)::bigint
                      )
                  ) > 0
              )
            ORDER BY
                modifier.kind,
                modifier.priority DESC,
                modifier.bonus_basis_points DESC,
                modifier.status_id
        ),
        battle_pass AS (
            SELECT
                BOOL_OR(entitlement.expires_at IS NULL) AS is_permanent,
                MAX(entitlement.expires_at) AS expires_at
            FROM public.account_entitlements entitlement
            JOIN requested_character character
              ON character.account_id = entitlement.account_id
            WHERE entitlement.entitlement_key = 'battle_pass'
              AND entitlement.starts_at <= @readAt
              AND entitlement.revoked_at IS NULL
              AND (
                  entitlement.expires_at IS NULL
                  OR entitlement.expires_at > @readAt
              )
            HAVING COUNT(*) > 0
        )
        SELECT
            projection_type,
            status_id,
            kind,
            bonus_basis_points,
            priority,
            remaining_ticks,
            source,
            donator_tier
        FROM (
            SELECT
                0::smallint AS projection_type,
                personal.status_id,
                personal.kind,
                personal.bonus_basis_points,
                personal.priority,
                personal.remaining_online_ticks AS remaining_ticks,
                personal.source,
                NULL::smallint AS donator_tier
            FROM personal
            UNION ALL
            SELECT
                0::smallint,
                1504,
                1009,
                control.bonus_basis_points,
                1,
                ROUND(EXTRACT(EPOCH FROM (
                    control.expires_at - @readAt
                )) * 10000000)::bigint,
                'world-boss:' || control.boss_template_key,
                NULL::smallint
            FROM requested_character character
            CROSS JOIN public.faction_area_experience_control control
            JOIN public.gameplay_world_boss_definitions area
              ON area.map_id = control.map_id
             AND area.template_key = control.boss_template_key
             AND area.revision = COALESCE(
                 @gameplayContentRevision,
                 (
                     SELECT publication.revision
                     FROM public.gameplay_content_publication publication
                     WHERE publication.family = 'gameplay'
                 )
             )
            WHERE control.map_id = @mapId
              AND control.realm_id = character.server_id
              AND control.controlling_camp = @camp
              AND control.activated_at <= @readAt
              AND control.expires_at > @readAt
            UNION ALL
            SELECT
                1::smallint,
                0,
                1008,
                0,
                character.donator_tier::integer,
                CASE
                    WHEN character.donator_expires_at IS NULL THEN NULL
                    ELSE ROUND(EXTRACT(EPOCH FROM (
                        character.donator_expires_at - @readAt
                    )) * 10000000)::bigint
                END,
                '',
                character.donator_tier
            FROM requested_character character
            WHERE character.donator_tier BETWEEN 1 AND 5
              AND (
                  character.donator_expires_at IS NULL
                  OR character.donator_expires_at > @readAt
              )
            UNION ALL
            SELECT
                2::smallint,
                0,
                1010,
                0,
                1,
                CASE
                    WHEN battle_pass.is_permanent THEN NULL
                    ELSE ROUND(EXTRACT(EPOCH FROM (
                        battle_pass.expires_at - @readAt
                    )) * 10000000)::bigint
                END,
                '',
                NULL::smallint
            FROM battle_pass
        ) projection
        ORDER BY kind
        LIMIT 67;
        """;
}
