using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    public async Task<PetCareDecayResult> DrainSummonedCareAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        int satietyPoints,
        int lifetimePoints,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(satietyPoints);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lifetimePoints);

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        (await _ownershipGuard.LockCurrentAsync(
            connection,
            transaction,
            subject,
            ownership,
            cancellationToken)).RequireCurrent();

        var summoned = await LockSummonedPetCareRowAsync(
            connection,
            transaction,
            subject.CharacterId,
            cancellationToken);
        if (summoned is null)
        {
            await transaction.CommitAsync(cancellationToken);
            await RequireCurrentOwnershipAsync(
                subject,
                ownership,
                cancellationToken);
            return new PetCareDecayResult(
                PetCareDecayStatus.NoSummonedPet,
                PetId: 0,
                Satiety: 0,
                Amity: 0,
                RemainingLifetime: 0,
                PetRevision: 0);
        }

        var satiety = PetCareDecayPolicy.Decay(
            summoned.Satiety,
            satietyPoints);
        var lifetime = Math.Max(
            PetCareDecayPolicy.MinimumLifetime,
            summoned.RemainingLifetime - lifetimePoints);
        if (satiety == summoned.Satiety &&
            lifetime == summoned.RemainingLifetime)
        {
            await transaction.CommitAsync(cancellationToken);
            await RequireCurrentOwnershipAsync(
                subject,
                ownership,
                cancellationToken);
            return new PetCareDecayResult(
                PetCareDecayStatus.AlreadyExhausted,
                summoned.PetId,
                summoned.Satiety,
                summoned.Amity,
                summoned.RemainingLifetime,
                summoned.Revision);
        }

        await using var update = CreateCommand(
            """
            UPDATE public.character_pets
            SET satiety = @satiety,
                remaining_lifetime = @lifetime,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId
              AND revision = @expectedRevision
              AND is_summoned
            RETURNING revision;
            """,
            connection,
            transaction);
        update.Parameters.AddWithValue("satiety", satiety);
        update.Parameters.AddWithValue("lifetime", lifetime);
        update.Parameters.AddWithValue("petId", summoned.PetId);
        update.Parameters.AddWithValue(
            "characterId",
            subject.CharacterId);
        update.Parameters.AddWithValue(
            "expectedRevision",
            summoned.Revision);
        var nextRevision =
            await update.ExecuteScalarAsync(cancellationToken) as long? ??
            throw new InvalidDataException(
                "The summoned pet changed during pet-care decay.");

        await transaction.CommitAsync(cancellationToken);
        await RequireCurrentOwnershipAsync(
            subject,
            ownership,
            cancellationToken);
        return new PetCareDecayResult(
            PetCareDecayStatus.Decayed,
            summoned.PetId,
            satiety,
            summoned.Amity,
            lifetime,
            nextRevision);
    }

    private async Task<SummonedPetCareRow?> LockSummonedPetCareRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT id, satiety, amity, remaining_lifetime, revision
            FROM public.character_pets
            WHERE user_id = @characterId
              AND activity_state = 'owned'
              AND is_summoned
            ORDER BY id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        var rows = new List<SummonedPetCareRow>(2);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new SummonedPetCareRow(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt64(4));
            if (row.Satiety < PetCareDecayPolicy.MinimumSatiety ||
                row.Satiety > PetCareDecayPolicy.MaximumSatiety ||
                row.Amity < PetCareDecayPolicy.MinimumAmity ||
                row.Amity > PetCareDecayPolicy.MaximumAmity ||
                row.RemainingLifetime < PetCareDecayPolicy.MinimumLifetime)
            {
                throw new InvalidDataException(
                    "The summoned pet has invalid care state.");
            }
            rows.Add(row);
        }

        return rows.Count switch
        {
            0 => null,
            1 => rows[0],
            _ => throw new InvalidDataException(
                "A character has multiple summoned pets.")
        };
    }

    private sealed record SummonedPetCareRow(
        long PetId,
        int Satiety,
        int Amity,
        int RemainingLifetime,
        long Revision);
}
