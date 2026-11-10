using Npgsql;

namespace Godswar.Server.Infrastructure.Quests;

/// <summary>
/// The outcome of one appraisal attempt.
/// </summary>
/// <param name="BonusBasisPoints">
/// The bonus the character holds after the attempt: zero when the gift was
/// refused, otherwise the stored permanent bonus.
/// </param>
/// <param name="NewlyGranted">
/// True only when this attempt wrote the row, so the caller announces once.
/// </param>
internal readonly record struct QuestAppraisalResult(
    int BonusBasisPoints,
    bool NewlyGranted);

/// <summary>
/// Reads and grants the permanent quest experience appraisal.
/// </summary>
/// <remarks>
/// The appraisal lives in one row per character
/// (<c>public.character_quest_appraisal</c>) and never expires, so a read is a
/// single primary-key lookup and the grant is one idempotent insert. A repeat
/// click returns the stored bonus with <c>NewlyGranted = false</c>.
/// </remarks>
internal sealed class PostgresQuestAppraisalStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresQuestAppraisalStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <summary>The stored permanent bonus in basis points; zero when absent.</summary>
    public async Task<int> ReadBonusBasisPointsAsync(
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            """
            SELECT bonus_basis_points
            FROM public.character_quest_appraisal
            WHERE character_id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is int bonus ? bonus : 0;
    }

    /// <summary>
    /// Grants the appraisal once. The first call writes the row and reports the
    /// new bonus; every later call leaves the stored row untouched and reports it
    /// with <c>NewlyGranted = false</c>.
    /// </summary>
    public async Task<QuestAppraisalResult> GrantAsync(
        int characterId,
        int bonusBasisPoints,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bonusBasisPoints);
        await using var command = _dataSource.CreateCommand(
            """
            WITH inserted AS (
                INSERT INTO public.character_quest_appraisal (
                    character_id,
                    bonus_basis_points
                )
                VALUES (@characterId, @bonusBasisPoints)
                ON CONFLICT (character_id) DO NOTHING
                RETURNING bonus_basis_points
            )
            SELECT bonus_basis_points, true AS newly_granted FROM inserted
            UNION ALL
            SELECT bonus_basis_points, false
            FROM public.character_quest_appraisal
            WHERE character_id = @characterId
            LIMIT 1;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("bonusBasisPoints", bonusBasisPoints);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new QuestAppraisalResult(0, false);
        }

        return new QuestAppraisalResult(reader.GetInt32(0), reader.GetBoolean(1));
    }
}
