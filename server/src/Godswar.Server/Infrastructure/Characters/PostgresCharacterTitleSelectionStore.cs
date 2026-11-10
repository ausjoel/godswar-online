using System.Data;
using Godswar.Server.Application.Characters;
using Npgsql;

namespace Godswar.Server.Infrastructure.Characters;

internal sealed class PostgresCharacterTitleSelectionStore : ICharacterTitleSelectionStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresPlayerOwnershipGuard _ownership;

    public PostgresCharacterTitleSelectionStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _ownership = new(dataSource);
    }

    public async Task<CharacterTitleSelectionReceipt> SelectAsync(
        CharacterTitleSelectionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Subject.AccountId <= 0 || request.Subject.CharacterId <= 0 ||
            !request.RealmId.IsValid || request.RealmId.Value > short.MaxValue)
        {
            return Rejected(CharacterTitleSelectionStatus.CharacterUnavailable);
        }
        if (request.TitleId > int.MaxValue)
        {
            return Rejected(CharacterTitleSelectionStatus.TitleNotOwned);
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        var ownership = await _ownership.LockCurrentAsync(connection, transaction,
            request.Subject, request.Ownership, cancellationToken);
        if (!ownership.IsCurrent)
        {
            return Rejected(ownership.Status == PlayerOwnershipValidationStatus.CharacterNotFound
                ? CharacterTitleSelectionStatus.CharacterUnavailable
                : CharacterTitleSelectionStatus.OwnershipLost);
        }

        var current = await ReadCurrentAsync(connection, transaction, request, cancellationToken);
        if (current is null)
        {
            return Rejected(CharacterTitleSelectionStatus.CharacterUnavailable);
        }
        if (request.TitleId != 0 && !current.OwnedTitleIds.Contains(request.TitleId))
        {
            return Rejected(CharacterTitleSelectionStatus.TitleNotOwned);
        }
        if (current.SelectedTitleId == request.TitleId)
        {
            await transaction.CommitAsync(cancellationToken);
            return current;
        }
        if (current.RewardRevision == long.MaxValue)
        {
            return Rejected(CharacterTitleSelectionStatus.RevisionExhausted);
        }

        var revision = checked(current.RewardRevision + 1);
        await using var update = new NpgsqlCommand(
            """
            UPDATE public.character_base
            SET selected_title_id = @title, medusa_reward_revision = @revision
            WHERE id = @character AND account_id = @account AND server_id = @realm
              AND lifecycle_state = 'active'
              AND checkpoint_owner_id = @owner AND checkpoint_owner_generation = @generation
              AND medusa_reward_revision = @before;
            """, connection, transaction);
        AddSubject(update, request);
        update.Parameters.AddWithValue("title", checked((int)request.TitleId));
        update.Parameters.AddWithValue("revision", revision);
        update.Parameters.AddWithValue("before", current.RewardRevision);
        update.Parameters.AddWithValue("owner", request.Ownership.OwnerId);
        update.Parameters.AddWithValue("generation", request.Ownership.Generation);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("The locked character title selection changed unexpectedly.");
        }
        await transaction.CommitAsync(cancellationToken);
        return current with
        {
            Status = CharacterTitleSelectionStatus.Applied,
            SelectedTitleId = request.TitleId,
            RewardRevision = revision
        };
    }

    private static async Task<CharacterTitleSelectionReceipt?> ReadCurrentAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        CharacterTitleSelectionRequest request, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT selected_title_id, medusa_honor_points, medusa_reward_revision,
                ARRAY(
                    SELECT title_id FROM public.character_title_entitlements WHERE character_id = @character
                    ORDER BY title_id)
            FROM public.character_base
            WHERE id = @character AND account_id = @account AND server_id = @realm
              AND lifecycle_state = 'active';
            """, connection, transaction);
        AddSubject(command, request);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new(CharacterTitleSelectionStatus.Unchanged, checked((uint)reader.GetInt32(0)),
            reader.GetInt32(1), reader.GetInt64(2),
            Array.AsReadOnly(reader.GetFieldValue<int[]>(3).Select(value => checked((uint)value)).ToArray()));
    }

    private static void AddSubject(NpgsqlCommand command, CharacterTitleSelectionRequest request)
    {
        command.Parameters.AddWithValue("character", request.Subject.CharacterId);
        command.Parameters.AddWithValue("account", request.Subject.AccountId);
        command.Parameters.AddWithValue("realm", checked((short)request.RealmId.Value));
    }

    private static CharacterTitleSelectionReceipt Rejected(CharacterTitleSelectionStatus status) =>
        new(status, 0, 0, 0, Array.Empty<uint>());
}
