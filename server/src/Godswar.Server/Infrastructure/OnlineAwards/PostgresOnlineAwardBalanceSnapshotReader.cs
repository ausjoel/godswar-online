using Godswar.Server.Application.Pets;
using System.Data;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.OnlineAwards;
using Npgsql;

namespace Godswar.Server.Infrastructure.OnlineAwards;

internal sealed class PostgresOnlineAwardBalanceSnapshotReader
{
    private const int MaximumRows =
        OnlineAwardBalanceSnapshot.MaximumRewardRows + 1;
    private readonly NpgsqlDataSource _dataSource;
    private readonly IItemTemplateCatalog _templates;
    private readonly IPetContentCatalog _pets;

    public PostgresOnlineAwardBalanceSnapshotReader(
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

    public static async Task<OnlineAwardBalanceSnapshot> LoadAsync(
        string connectionString,
        IItemTemplateCatalog templates,
        IPetContentCatalog pets,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        return await new PostgresOnlineAwardBalanceSnapshotReader(
            dataSource,
            templates,
            pets)
            .ReadAsync(cancellationToken);
    }

    public async Task<OnlineAwardBalanceSnapshot> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT
                release.revision,
                release.sha256,
                release.entry_count,
                release.sealed_at IS NOT NULL,
                entry.reward_order,
                entry.item_id,
                entry.quantity,
                entry.item_quality,
                entry.bound,
                entry.stack_cap
            FROM public.online_award_balance_publication publication
            JOIN public.online_award_balance_revisions release
              ON release.revision = publication.revision
             AND release.sha256 = publication.balance_sha256
            JOIN public.online_award_balance_entries entry
              ON entry.revision = release.revision
            WHERE publication.family = 'online-award'
            ORDER BY entry.reward_order
            LIMIT @maximumRows;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("maximumRows", MaximumRows);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        long revision = 0;
        string? sha256 = null;
        short expectedCount = 0;
        var rewards = new List<OnlineAwardRewardEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (revision == 0)
            {
                revision = reader.GetInt64(0);
                sha256 = reader.GetString(1);
                expectedCount = reader.GetInt16(2);
                if (!reader.GetBoolean(3))
                {
                    throw new InvalidDataException(
                        "The Online Award publication is unsealed.");
                }
            }
            else if (revision != reader.GetInt64(0) ||
                     !string.Equals(
                         sha256,
                         reader.GetString(1),
                         StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The Online Award publication is not singular.");
            }

            rewards.Add(new OnlineAwardRewardEntry(
                reader.GetInt16(4),
                reader.GetInt32(5),
                reader.GetInt16(6),
                reader.GetInt16(7),
                reader.GetInt16(8),
                reader.GetInt16(9)));
        }

        await reader.DisposeAsync();

        var snapshot = new OnlineAwardBalanceSnapshot(
            revision,
            sha256 ?? string.Empty,
            rewards);
        snapshot.Validate();
        if (snapshot.Rewards.Count != expectedCount ||
            snapshot.Rewards.Any(reward =>
                !OnlineAwardPinnedItemPolicy.IsValid(
                    _templates,
                    _pets,
                    reward)))
        {
            throw new InvalidDataException(
                "The Online Award balance does not match pinned item content.");
        }
        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }
}
