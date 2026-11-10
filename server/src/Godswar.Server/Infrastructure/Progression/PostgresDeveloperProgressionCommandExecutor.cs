using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Progression;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Progression;

internal sealed class PostgresDeveloperProgressionCommandExecutor(
    NpgsqlDataSource dataSource) : IDeveloperProgressionCommandExecutor
{
    internal const string FighterLevelUpdateSql = """
        UPDATE public.character_base
        SET fighter_job_lv = @fighterLevel,
            fighter_job_exp = @fighterExperience,
            progression_reward_revision = @newRevision
        WHERE id = @characterId
          AND account_id = @accountId
          AND server_id = @realmId
          AND lifecycle_state = 'active'
          AND fighter_job_lv = @expectedFighterLevel
          AND fighter_job_exp = @expectedFighterExperience
          AND "SkillPoint" = @expectedTalentPoints
          AND zodiac_level = @expectedZodiacLevel
          AND zodiac_energy = @expectedZodiacEnergy
          AND zodiac_energy_remainder_x100 = @expectedZodiacRemainder
          AND progression_reward_revision = @expectedRevision;
        """;

    internal const string TalentPointsUpdateSql = """
        UPDATE public.character_base
        SET "SkillPoint" = @talentPoints,
            progression_reward_revision = @newRevision
        WHERE id = @characterId
          AND account_id = @accountId
          AND server_id = @realmId
          AND lifecycle_state = 'active'
          AND fighter_job_lv = @expectedFighterLevel
          AND fighter_job_exp = @expectedFighterExperience
          AND "SkillPoint" = @expectedTalentPoints
          AND zodiac_level = @expectedZodiacLevel
          AND zodiac_energy = @expectedZodiacEnergy
          AND zodiac_energy_remainder_x100 = @expectedZodiacRemainder
          AND progression_reward_revision = @expectedRevision;
        """;

    internal const string ZodiacEnergyUpdateSql = """
        UPDATE public.character_base
        SET zodiac_energy = @zodiacEnergy,
            zodiac_energy_remainder_x100 = @zodiacRemainder,
            progression_reward_revision = @newRevision
        WHERE id = @characterId
          AND account_id = @accountId
          AND server_id = @realmId
          AND lifecycle_state = 'active'
          AND fighter_job_lv = @expectedFighterLevel
          AND fighter_job_exp = @expectedFighterExperience
          AND "SkillPoint" = @expectedTalentPoints
          AND zodiac_level = @expectedZodiacLevel
          AND zodiac_energy = @expectedZodiacEnergy
          AND zodiac_energy_remainder_x100 = @expectedZodiacRemainder
          AND progression_reward_revision = @expectedRevision;
        """;

    private readonly NpgsqlDataSource _dataSource = dataSource ??
        throw new ArgumentNullException(nameof(dataSource));
    private readonly PostgresPlayerOwnershipGuard _ownershipGuard =
        new(dataSource);

    public async Task<DeveloperProgressionMutationResult> ExecuteAsync(
        DeveloperProgressionCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        var ownership = await _ownershipGuard.LockCurrentAsync(
            connection,
            transaction,
            request.Subject,
            request.Ownership,
            cancellationToken);
        if (ownership.Status ==
            PlayerOwnershipValidationStatus.CharacterNotFound)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DeveloperProgressionMutationResult(
                DeveloperProgressionMutationStatus.CharacterNotFound,
                Previous: null,
                Current: null);
        }

        ownership.RequireCurrent();
        var before = await ReadProjectionAsync(
            connection,
            transaction,
            request,
            cancellationToken);
        if (before is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DeveloperProgressionMutationResult(
                DeveloperProgressionMutationStatus.CharacterNotFound,
                Previous: null,
                Current: null);
        }

        var result = DeveloperProgressionMutation.Apply(
            before.Value,
            request.Command);
        if (!result.Changed)
        {
            await transaction.RollbackAsync(cancellationToken);
            if (result.IsSuccess)
            {
                (await _ownershipGuard.ValidateCurrentAsync(
                    request.Subject,
                    request.Ownership,
                    cancellationToken)).RequireCurrent();
            }

            return result;
        }

        var after = result.Current ??
            throw new InvalidDataException(
                "A committed developer progression mutation has no projection.");
        await UpdateProjectionAsync(
            connection,
            transaction,
            request,
            before.Value,
            after,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        (await _ownershipGuard.ValidateCurrentAsync(
            request.Subject,
            request.Ownership,
            cancellationToken)).RequireCurrent();
        return result;
    }

    private static async Task<DeveloperProgressionProjection?>
        ReadProjectionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            DeveloperProgressionCommandRequest request,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                fighter_job_lv,
                fighter_job_exp,
                "SkillPoint",
                zodiac_level,
                zodiac_energy,
                zodiac_energy_remainder_x100,
                progression_reward_revision
            FROM public.character_base
            WHERE id = @characterId
              AND account_id = @accountId
              AND server_id = @realmId
              AND lifecycle_state = 'active';
            """,
            connection,
            transaction);
        AddIdentityParameters(command, request);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var projection = new DeveloperProgressionProjection(
            reader.GetInt32(0),
            reader.GetInt64(1),
            reader.GetInt32(2),
            checked((byte)reader.GetInt16(3)),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt64(6));
        DeveloperProgressionMutation.ValidateProjection(projection);
        return projection;
    }

    private static async Task UpdateProjectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DeveloperProgressionCommandRequest request,
        DeveloperProgressionProjection before,
        DeveloperProgressionProjection after,
        CancellationToken cancellationToken)
    {
        var sql = request.Command.Operation switch
        {
            DeveloperProgressionOperation.SetFighterLevel or
                DeveloperProgressionOperation.AdjustFighterLevel =>
                FighterLevelUpdateSql,
            DeveloperProgressionOperation.AddTalentPoints =>
                TalentPointsUpdateSql,
            DeveloperProgressionOperation.AddZodiacEnergy =>
                ZodiacEnergyUpdateSql,
            _ => throw new ArgumentOutOfRangeException(
                nameof(request.Command))
        };

        await using var command =
            new NpgsqlCommand(sql, connection, transaction);
        AddIdentityParameters(command, request);
        command.Parameters.AddWithValue(
            "expectedFighterLevel",
            before.FighterLevel);
        command.Parameters.AddWithValue(
            "expectedFighterExperience",
            before.FighterExperience);
        command.Parameters.AddWithValue(
            "expectedTalentPoints",
            before.TalentPoints);
        command.Parameters.AddWithValue(
            "expectedZodiacLevel",
            checked((short)before.ZodiacLevel));
        command.Parameters.AddWithValue(
            "expectedZodiacEnergy",
            before.ZodiacEnergy);
        command.Parameters.AddWithValue(
            "expectedZodiacRemainder",
            before.ZodiacEnergyRemainderX100);
        command.Parameters.AddWithValue(
            "expectedRevision",
            before.ProgressionRevision);
        command.Parameters.AddWithValue(
            "newRevision",
            after.ProgressionRevision);

        switch (request.Command.Operation)
        {
            case DeveloperProgressionOperation.SetFighterLevel:
            case DeveloperProgressionOperation.AdjustFighterLevel:
                command.Parameters.AddWithValue(
                    "fighterLevel",
                    after.FighterLevel);
                command.Parameters.AddWithValue(
                    "fighterExperience",
                    after.FighterExperience);
                break;
            case DeveloperProgressionOperation.AddTalentPoints:
                command.Parameters.AddWithValue(
                    "talentPoints",
                    after.TalentPoints);
                break;
            case DeveloperProgressionOperation.AddZodiacEnergy:
                command.Parameters.AddWithValue(
                    "zodiacEnergy",
                    after.ZodiacEnergy);
                command.Parameters.AddWithValue(
                    "zodiacRemainder",
                    after.ZodiacEnergyRemainderX100);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(request.Command));
        }

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The developer progression mutation was not exact.");
        }
    }

    private static void AddIdentityParameters(
        NpgsqlCommand command,
        DeveloperProgressionCommandRequest request)
    {
        command.Parameters.AddWithValue(
            "accountId",
            request.Subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            request.Subject.CharacterId);
        command.Parameters.AddWithValue(
            "realmId",
            request.RealmId.Value);
    }
}
