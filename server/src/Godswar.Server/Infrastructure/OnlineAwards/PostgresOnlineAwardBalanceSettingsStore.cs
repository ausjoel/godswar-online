using Godswar.Server.Application.Pets;
using System.Data;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.OnlineAwards;
using Npgsql;

namespace Godswar.Server.Infrastructure.OnlineAwards;

internal sealed class PostgresOnlineAwardBalanceSettingsStore :
    IOnlineAwardBalanceSettingsStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IItemTemplateCatalog _templates;
    private readonly IPetContentCatalog _pets;

    public PostgresOnlineAwardBalanceSettingsStore(
        NpgsqlDataSource dataSource,
        IItemTemplateCatalog templates,
        IPetContentCatalog pets)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
        _templates = templates ??
            throw new ArgumentNullException(nameof(templates));
        _pets = pets ?? throw new ArgumentNullException(nameof(pets));
    }

    public async Task<OnlineAwardBalanceUpdateResult> TryPublishSuccessorAsync(
        OnlineAwardBalanceUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!IsValidActor(update.UpdatedBy) ||
            update.ExpectedRevision <= 0)
        {
            return new(OnlineAwardBalanceUpdateStatus.Invalid, null);
        }

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var current = await ReadCurrentRevisionAsync(
            connection,
            transaction,
            cancellationToken);
        if (current.Revision != update.ExpectedRevision)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(
                OnlineAwardBalanceUpdateStatus.RevisionConflict,
                null);
        }

        var normalized = ValidateAgainstTemplates(update.Rewards);
        if (normalized is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(OnlineAwardBalanceUpdateStatus.Invalid, null);
        }

        var sha256 = OnlineAwardBalanceSnapshot.ComputeSha256(normalized);
        if (string.Equals(sha256, current.Sha256, StringComparison.Ordinal))
        {
            var unchanged = new OnlineAwardBalanceSnapshot(
                current.Revision,
                current.Sha256,
                normalized);
            await transaction.RollbackAsync(cancellationToken);
            return new(OnlineAwardBalanceUpdateStatus.Unchanged, unchanged);
        }

        var revision = checked(current.Revision + 1);
        var snapshot = new OnlineAwardBalanceSnapshot(
            revision,
            sha256,
            normalized);
        snapshot.Validate();
        await InsertRevisionAsync(
            connection,
            transaction,
            snapshot,
            update.UpdatedBy,
            cancellationToken);
        var changed = await CompareAndSwapPublicationAsync(
            connection,
            transaction,
            current.Revision,
            revision,
            update.UpdatedBy,
            cancellationToken);
        if (!changed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(
                OnlineAwardBalanceUpdateStatus.RevisionConflict,
                null);
        }

        await transaction.CommitAsync(cancellationToken);
        return new(OnlineAwardBalanceUpdateStatus.Updated, snapshot);
    }

    private static bool IsValidActor(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        !value.Any(char.IsControl);

    private static async Task<CurrentPublication> ReadCurrentRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT revision, balance_sha256
            FROM public.online_award_balance_publication
            WHERE family = 'online-award'
            FOR UPDATE;
            """,
            connection,
            transaction);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "The Online Award publication pointer is missing.");
        }
        var result = new CurrentPublication(
            reader.GetInt64(0),
            reader.GetString(1));
        if (result.Revision <= 0 ||
            result.Sha256.Length != 64 ||
            await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "The Online Award publication pointer is invalid.");
        }
        return result;
    }

    private OnlineAwardRewardEntry[]? ValidateAgainstTemplates(
        IReadOnlyList<OnlineAwardRewardEntry> rewards)
    {
        if (rewards is null ||
            rewards.Count is < 1 or >
                OnlineAwardBalanceSnapshot.MaximumRewardRows)
        {
            return null;
        }

        var normalized = new OnlineAwardRewardEntry[rewards.Count];
        for (var index = 0; index < rewards.Count; index++)
        {
            var reward = rewards[index];
            if (reward.Order != index || reward.ItemId <= 0 ||
                reward.Quantity <= 0 || reward.ItemQuality <= 0 ||
                reward.Bound is < 0 or > 1)
            {
                return null;
            }

            if (!OnlineAwardPinnedItemPolicy.IsValid(
                    _templates,
                    _pets,
                    reward))
            {
                return null;
            }
            normalized[index] = reward;
        }

        try
        {
            new OnlineAwardBalanceSnapshot(
                1,
                OnlineAwardBalanceSnapshot.ComputeSha256(normalized),
                normalized).Validate();
        }
        catch (Exception exception) when (exception is
            InvalidDataException or OverflowException)
        {
            return null;
        }
        return normalized;
    }

    private static async Task InsertRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OnlineAwardBalanceSnapshot snapshot,
        string updatedBy,
        CancellationToken cancellationToken)
    {
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO public.online_award_balance_revisions (
                revision, sha256, entry_count, source, created_by)
            VALUES (@revision, @sha256, @entryCount,
                    'management-successor', @createdBy);
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("revision", snapshot.Revision);
            command.Parameters.AddWithValue("sha256", snapshot.Sha256);
            command.Parameters.AddWithValue(
                "entryCount",
                checked((short)snapshot.Rewards.Count));
            command.Parameters.AddWithValue("createdBy", updatedBy);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The Online Award revision insert was not exact.");
            }
        }

        await using var entry = new NpgsqlCommand(
            """
            INSERT INTO public.online_award_balance_entries (
                revision, reward_order, item_id, quantity,
                item_quality, bound, stack_cap)
            VALUES (@revision, @order, @itemId, @quantity,
                    @itemQuality, @bound, @stackCap);
            """,
            connection,
            transaction);
        foreach (var reward in snapshot.Rewards)
        {
            entry.Parameters.Clear();
            entry.Parameters.AddWithValue("revision", snapshot.Revision);
            entry.Parameters.AddWithValue("order", reward.Order);
            entry.Parameters.AddWithValue("itemId", reward.ItemId);
            entry.Parameters.AddWithValue("quantity", reward.Quantity);
            entry.Parameters.AddWithValue(
                "itemQuality",
                reward.ItemQuality);
            entry.Parameters.AddWithValue("bound", reward.Bound);
            entry.Parameters.AddWithValue("stackCap", reward.StackCap);
            if (await entry.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "An Online Award reward insert was not exact.");
            }
        }
    }

    private static async Task<bool> CompareAndSwapPublicationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long expectedRevision,
        long successorRevision,
        string updatedBy,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE public.online_award_balance_publication
            SET revision = @successor,
                balance_sha256 = @sha256,
                publication_version = publication_version + 1,
                updated_by = @updatedBy,
                updated_at = now()
            WHERE family = 'online-award'
              AND revision = @expected;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("successor", successorRevision);
        command.Parameters.AddWithValue(
            "sha256",
            await ReadRevisionHashAsync(
                connection,
                transaction,
                successorRevision,
                cancellationToken));
        command.Parameters.AddWithValue("expected", expectedRevision);
        command.Parameters.AddWithValue("updatedBy", updatedBy);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task<string> ReadRevisionHashAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long revision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT sha256
            FROM public.online_award_balance_revisions
            WHERE revision = @revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision", revision);
        return await command.ExecuteScalarAsync(cancellationToken) as string ??
            throw new InvalidDataException(
                "The Online Award successor identity is missing.");
    }

    private sealed record CurrentPublication(long Revision, string Sha256);
}
