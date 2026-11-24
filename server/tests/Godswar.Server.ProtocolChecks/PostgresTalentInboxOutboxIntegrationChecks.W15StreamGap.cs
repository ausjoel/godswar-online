using Godswar.Server.Application.Talents;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Talents;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

// W15: live character 13 talent 150 had 12 talent events stuck since 31 Oct.
// gwadmin-web moved character_talents.outbox_revision forward (6 -> 7) without an
// outbox event, the next upgrade was written as version 8, and the strict
// dispatcher waited for version 7 forever. The executor now takes the version
// from the stream, so an outside bump or a deleted talent row cannot open a hole.
internal static partial class PostgresTalentInboxOutboxIntegrationChecks
{
    private static async Task AssertOutsideRevisionBumpKeepsStreamContiguousAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "w15gap",
            level: 80,
            talentPoints: 100);

        await using var source = NpgsqlDataSource.Create(connectionString);
        var first = RequireReceipt(
            await CreateExecutor(source).ExecuteAsync(
                CreateEnvelope(fixture, expectedRank: 0)),
            TalentUpgradeExecutionDisposition.Committed,
            "W15 first upgrade");
        Check.True(first.AggregateRevision == 1, "W15 first upgrade is stream version 1");

        // What gwadmin-web Undo / Restore did on live: rank kept, counter + 1, no event.
        await ExecuteFixtureSqlAsync(
            connectionString,
            fixture,
            "UPDATE public.character_talents SET outbox_revision = outbox_revision + 1 " +
            "WHERE user_id = @characterId AND talent_id = @talentId;");

        var second = RequireReceipt(
            await CreateExecutor(source).ExecuteAsync(
                CreateEnvelope(fixture, expectedRank: 1)),
            TalentUpgradeExecutionDisposition.Committed,
            "W15 upgrade after an outside revision bump");
        Check.True(
            second.AggregateRevision == 2,
            $"W15 an outside revision bump does not skip a stream version (got {second.AggregateRevision})");
        Check.True(
            (await ReadStateAsync(connectionString, fixture)).OutboxRevision == 2,
            "W15 outbox_revision follows the stream version again");

        // What gwadmin-web 'remove talent' then 'set rank 2' does: the row is re-inserted with
        // outbox_revision 0 while the stream already holds versions 1-2. The old +1 numbering
        // wrote version 1 again and the upgrade failed on uq_outbox_events_version.
        await ExecuteFixtureSqlAsync(
            connectionString,
            fixture,
            "DELETE FROM public.character_talents WHERE user_id = @characterId AND talent_id = @talentId;");
        await ExecuteFixtureSqlAsync(
            connectionString,
            fixture,
            "INSERT INTO public.character_talents (user_id, talent_id, rank) VALUES (@characterId, @talentId, 2);");
        var third = RequireReceipt(
            await CreateExecutor(source).ExecuteAsync(
                CreateEnvelope(fixture, expectedRank: 2)),
            TalentUpgradeExecutionDisposition.Committed,
            "W15 upgrade after the talent row was deleted and re-added");
        Check.True(
            third.AggregateRevision == 3,
            $"W15 an upgrade after a re-added row continues the stream (got {third.AggregateRevision})");

        var dispatcher = new PostgresOutboxDispatcher(
            source,
            [new TalentUpgradeOutboxConsumer()],
            new PostgresOutboxDispatcherOptions
            {
                GapRetryDelayMilliseconds = 50,
                PollIntervalMilliseconds = 50
            });
        for (var pass = 0; pass < 20 && await ReadUndeliveredAsync(connectionString, fixture) > 0; pass++)
        {
            await dispatcher.DispatchOnceAsync();
        }

        Check.True(
            await ReadUndeliveredAsync(connectionString, fixture) == 0,
            "W15 every talent event of the bumped stream is delivered");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var position = new NpgsqlCommand(
            "SELECT current_version FROM public.outbox_consumer_positions " +
            "WHERE consumer_key = @consumer AND aggregate_key = @aggregate;",
            connection);
        position.Parameters.AddWithValue("consumer", TalentUpgradePersistenceCodec.ConsumerKey);
        position.Parameters.AddWithValue("aggregate", AggregateKey(fixture));
        Check.True(
            await position.ExecuteScalarAsync() is long version && version == 3,
            "W15 the strict talent checkpoint reaches the newest version");
    }

    private static async Task ExecuteFixtureSqlAsync(
        string connectionString,
        TalentFixture fixture,
        string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("characterId", fixture.CharacterId);
        command.Parameters.AddWithValue("talentId", TalentId);
        Check.Equal(1, await command.ExecuteNonQueryAsync(), "W15 fixture SQL touches one talent row");
    }

    private static async Task<long> ReadUndeliveredAsync(
        string connectionString,
        TalentFixture fixture)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM public.outbox_events " +
            "WHERE consumer_key = @consumer AND aggregate_key = @aggregate AND delivered_at IS NULL;",
            connection);
        command.Parameters.AddWithValue("consumer", TalentUpgradePersistenceCodec.ConsumerKey);
        command.Parameters.AddWithValue("aggregate", AggregateKey(fixture));
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }
}
