using Godswar.Server.Application.OnlineAwards;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresOnlineAwardIntegrationChecks
{
    private static async Task AssertFoundationEvidenceAsync(
        NpgsqlDataSource dataSource,
        OnlineAwardBalanceSnapshot balance)
    {
        await using (var command = dataSource.CreateCommand(
            """
            SELECT revision.revision,
                   revision.sha256,
                   revision.entry_count,
                   revision.sealed_at IS NOT NULL,
                   upper(encode(sha256(convert_to(
                       public.online_award_balance_canonical(revision.revision),
                       'UTF8')), 'hex')),
                   publication.publication_version,
                   count(audit.publication_version)::integer
            FROM public.online_award_balance_revisions revision
            JOIN public.online_award_balance_publication publication
              ON publication.revision = revision.revision
             AND publication.balance_sha256 = revision.sha256
            LEFT JOIN public.online_award_publication_audit audit
              ON audit.revision = revision.revision
             AND audit.balance_sha256 = revision.sha256
            WHERE publication.family = 'online-award'
            GROUP BY revision.revision, revision.sha256,
                     revision.entry_count, revision.sealed_at,
                     publication.publication_version;
            """))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Check.True(
                await reader.ReadAsync() &&
                reader.GetInt64(0) == balance.Revision &&
                reader.GetString(1) == balance.Sha256 &&
                reader.GetInt16(2) == 4 && reader.GetBoolean(3) &&
                reader.GetString(4) == balance.Sha256 &&
                reader.GetInt64(5) == 1 && reader.GetInt32(6) == 1 &&
                !await reader.ReadAsync(),
                "migration 105 seals, hashes, publishes, and audits exact seed data");
        }

        await AssertSqlRejectedAsync(
            dataSource,
            """
            INSERT INTO public.online_award_balance_entries
                (revision, reward_order, item_id, quantity,
                 item_quality, bound, stack_cap)
            VALUES (1, 4, 10134, 1, 2, 0, 99);
            """,
            PostgresErrorCodes.ObjectNotInPrerequisiteState,
            "sealed balance rejects child inserts");
        await AssertSqlRejectedAsync(
            dataSource,
            "UPDATE public.online_award_balance_entries SET quantity = 6 " +
                "WHERE revision = 1 AND reward_order = 2;",
            PostgresErrorCodes.ObjectNotInPrerequisiteState,
            "balance children are immutable");
        await AssertSqlRejectedAsync(
            dataSource,
            "UPDATE public.online_award_balance_revisions SET source = 'x' " +
                "WHERE revision = 1;",
            PostgresErrorCodes.ObjectNotInPrerequisiteState,
            "sealed balance header is immutable");
        await AssertSqlRejectedAsync(
            dataSource,
            """
            UPDATE public.online_award_balance_publication
            SET updated_by = 'unfenced-direct-write'
            WHERE family = 'online-award';
            """,
            PostgresErrorCodes.CheckViolation,
            "publication pointer rejects an unfenced write");
        await AssertSqlRejectedAsync(
            dataSource,
            """
            INSERT INTO public.online_award_publication_audit (
                publication_version, previous_revision, revision,
                previous_sha256, balance_sha256, changed_at, changed_by)
            VALUES (99, 1, 1,
                'A11516DCF5A5CAC3AAAC9CECEFF416C6510789F4386BBAF4C8D7CC9FB2B704FE',
                'A11516DCF5A5CAC3AAAC9CECEFF416C6510789F4386BBAF4C8D7CC9FB2B704FE',
                now(), 'direct-write');
            """,
            PostgresErrorCodes.ObjectNotInPrerequisiteState,
            "publication audit is trigger-owned");
        await AssertTruncateRejectedAsync(
            dataSource,
            "public.online_award_balance_entries",
            cascade: false);
        await AssertTruncateRejectedAsync(
            dataSource,
            "public.online_award_balance_revisions",
            cascade: true);
        await AssertTruncateRejectedAsync(
            dataSource,
            "public.online_award_publication_audit",
            cascade: false);
        await AssertSealShapeGuardsAsync(dataSource);
        await AssertChildSealSerializationAsync(dataSource);
    }

    private static async Task AssertSealShapeGuardsAsync(
        NpgsqlDataSource dataSource)
    {
        OnlineAwardRewardEntry[] nonContiguous =
        [
            new(0, 10134, 1, 1, 0, 99),
            new(2, 11005, 1, 1, 0, 99)
        ];
        var nonContiguousHash =
            OnlineAwardBalanceSnapshot.ComputeSha256(nonContiguous);
        await AssertSqlRejectedAsync(
            dataSource,
            $"""
            INSERT INTO public.online_award_balance_revisions (
                revision, sha256, entry_count, source, created_by)
            VALUES (9000001, '{nonContiguousHash}', 2,
                'non-contiguous-check', 'protocol-check');
            INSERT INTO public.online_award_balance_entries (
                revision, reward_order, item_id, quantity,
                item_quality, bound, stack_cap)
            VALUES
                (9000001, 0, 10134, 1, 1, 0, 99),
                (9000001, 2, 11005, 1, 1, 0, 99);
            UPDATE public.online_award_balance_revisions
            SET sealed_at = transaction_timestamp()
            WHERE revision = 9000001;
            """,
            PostgresErrorCodes.CheckViolation,
            "seal rejects non-contiguous reward order");

        await AssertSqlRejectedAsync(
            dataSource,
            """
            INSERT INTO public.online_award_balance_revisions (
                revision, sha256, entry_count, source, created_by)
            VALUES (9000002,
                'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
                2, 'duplicate-identity-check', 'protocol-check');
            INSERT INTO public.online_award_balance_entries (
                revision, reward_order, item_id, quantity,
                item_quality, bound, stack_cap)
            VALUES
                (9000002, 0, 10134, 1, 1, 0, 99),
                (9000002, 1, 10134, 2, 1, 0, 99);
            """,
            PostgresErrorCodes.UniqueViolation,
            "a balance cannot contain duplicate reward identities");

        OnlineAwardRewardEntry[] excessiveSlots =
        [
            new(0, 10134, 97, 1, 0, 1)
        ];
        var excessiveSlotsHash =
            OnlineAwardBalanceSnapshot.ComputeSha256(excessiveSlots);
        await AssertSqlRejectedAsync(
            dataSource,
            $"""
            INSERT INTO public.online_award_balance_revisions (
                revision, sha256, entry_count, source, created_by)
            VALUES (9000003, '{excessiveSlotsHash}', 1,
                'capacity-check', 'protocol-check');
            INSERT INTO public.online_award_balance_entries (
                revision, reward_order, item_id, quantity,
                item_quality, bound, stack_cap)
            VALUES (9000003, 0, 10134, 97, 1, 0, 1);
            UPDATE public.online_award_balance_revisions
            SET sealed_at = transaction_timestamp()
            WHERE revision = 9000003;
            """,
            PostgresErrorCodes.CheckViolation,
            "seal rejects balances requiring more than 96 slots");
    }

    private static async Task AssertChildSealSerializationAsync(
        NpgsqlDataSource dataSource)
    {
        var reward = new OnlineAwardRewardEntry(0, 10134, 1, 1, 0, 99);
        var hash = OnlineAwardBalanceSnapshot.ComputeSha256([reward]);
        long revision;
        await using (var header = dataSource.CreateCommand(
            """
            INSERT INTO public.online_award_balance_revisions (
                revision, sha256, entry_count, source, created_by)
            SELECT max(revision) + 1000, @sha256, 1,
                   'seal-race-check', 'seal-race-check'
            FROM public.online_award_balance_revisions
            RETURNING revision;
            """))
        {
            header.Parameters.AddWithValue("sha256", hash);
            revision = Convert.ToInt64(await header.ExecuteScalarAsync());
        }

        await using var first = await dataSource.OpenConnectionAsync();
        await using var firstTransaction =
            await first.BeginTransactionAsync();
        await using (var child = new NpgsqlCommand(
            """
            INSERT INTO public.online_award_balance_entries (
                revision, reward_order, item_id, quantity,
                item_quality, bound, stack_cap)
            VALUES (@revision, 0, 10134, 1, 1, 0, 99);
            """,
            first,
            firstTransaction))
        {
            child.Parameters.AddWithValue("revision", revision);
            Check.Equal(1, await child.ExecuteNonQueryAsync(),
                "unsealed child insert acquires its parent publication lock");
        }

        await using var second = await dataSource.OpenConnectionAsync();
        await using var secondTransaction =
            await second.BeginTransactionAsync();
        await using var seal = new NpgsqlCommand(
            """
            UPDATE public.online_award_balance_revisions
            SET sealed_at = transaction_timestamp()
            WHERE revision = @revision;
            """,
            second,
            secondTransaction)
        {
            CommandTimeout = 10
        };
        seal.Parameters.AddWithValue("revision", revision);
        var sealTask = seal.ExecuteNonQueryAsync();
        var early = await Task.WhenAny(sealTask, Task.Delay(200));
        Check.True(
            early != sealTask,
            "sealing waits for an in-flight child insert");
        await firstTransaction.CommitAsync();
        Check.Equal(1, await sealTask,
            "seal resumes after the child transaction commits");
        await secondTransaction.CommitAsync();

        await using (var verify = dataSource.CreateCommand(
            """
            SELECT sealed_at IS NOT NULL,
                   sha256 = upper(encode(sha256(convert_to(
                       public.online_award_balance_canonical(revision),
                       'UTF8')), 'hex')),
                   (SELECT count(*)
                    FROM public.online_award_balance_entries entry
                    WHERE entry.revision = revision.revision)
            FROM public.online_award_balance_revisions revision
            WHERE revision = @revision;
            """))
        {
            verify.Parameters.AddWithValue("revision", revision);
            await using var reader = await verify.ExecuteReaderAsync();
            Check.True(
                await reader.ReadAsync() && reader.GetBoolean(0) &&
                reader.GetBoolean(1) && reader.GetInt64(2) == 1,
                "serialized seal includes the committed child in its digest");
        }

        await AssertSqlRejectedAsync(
            dataSource,
            $"""
            INSERT INTO public.online_award_balance_entries (
                revision, reward_order, item_id, quantity,
                item_quality, bound, stack_cap)
            VALUES ({revision}, 1, 11005, 1, 1, 0, 99);
            """,
            PostgresErrorCodes.ObjectNotInPrerequisiteState,
            "no child can commit after its parent is sealed");
    }

    private static async Task AssertTruncateRejectedAsync(
        NpgsqlDataSource dataSource,
        string exactTable,
        bool cascade)
    {
        var sql = $"TRUNCATE TABLE {exactTable}" +
            (cascade ? " CASCADE;" : ";");
        await AssertSqlRejectedAsync(
            dataSource,
            sql,
            PostgresErrorCodes.ObjectNotInPrerequisiteState,
            $"{exactTable} rejects TRUNCATE");
    }

    private static async Task AssertSqlRejectedAsync(
        NpgsqlDataSource dataSource,
        string sql,
        string expectedState,
        string description)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using var command = new NpgsqlCommand(
                sql,
                connection,
                transaction);
            await command.ExecuteNonQueryAsync();
            await transaction.RollbackAsync();
            throw new InvalidOperationException(
                $"Database guard failed: {description}.");
        }
        catch (PostgresException exception) when (
            exception.SqlState == expectedState)
        {
            await transaction.RollbackAsync();
        }
    }
}
