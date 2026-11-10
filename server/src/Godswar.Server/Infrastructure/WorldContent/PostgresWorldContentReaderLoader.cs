using System.Data;
using System.Diagnostics;
using Godswar.Server.Application.World;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static partial class PostgresWorldContentReaderLoader
{
    private const string PostgresWorldContentSource =
        "postgres-published-v1";

    public static async Task<IWorldContentReader> LoadAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var dataSource =
                NpgsqlDataSource.Create(connectionString);
            await using var connection =
                await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction =
                await connection.BeginTransactionAsync(
                    IsolationLevel.RepeatableRead,
                    cancellationToken);
            await using (var readOnly = new NpgsqlCommand(
                             "SET TRANSACTION READ ONLY;",
                             connection,
                             transaction))
            {
                await readOnly.ExecuteNonQueryAsync(cancellationToken);
            }

            var gameplay = await LoadPublishedGameplayContentAsync(
                connection,
                transaction,
                cancellationToken);
            gameplay = gameplay with
            {
                MonsterCombatBalances = await LoadMonsterCombatBalancesAsync(
                    connection,
                    transaction,
                    gameplay,
                    cancellationToken)
            };
            var mapIds = gameplay.Maps
                .Select(static value => value.MapId)
                .ToArray();
            var npcDefinitions = await LoadPublishedNpcDefinitionsAsync(
                connection,
                transaction,
                mapIds.ToHashSet(),
                cancellationToken);
            var npcDialogues =
                await LoadPublishedNpcDialogueDefinitionsAsync(
                    connection,
                    transaction,
                    npcDefinitions,
                    cancellationToken);
            var capturedMonsters = await LoadPublishedMonsterSpawnsAsync(
                connection,
                transaction,
                mapIds.ToHashSet(),
                cancellationToken);
            // Thermopylae and the Sparta outskirts are authored in code rather
            // than captured, so they are appended after the published monster
            // revision above has been validated on its own.
            var monsters = AppendAuthoredSpawns(
                capturedMonsters,
                gameplay);
            var enterBootstrap =
                await LoadPublishedEnterBootstrapPacketsAsync(
                connection,
                transaction,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            var reader = PinnedWorldContentReader.Create(
                PostgresWorldContentSource,
                mapIds,
                npcDefinitions,
                monsters,
                enterBootstrap,
                npcTexts: npcDialogues.Texts,
                npcDialogueRoutes: npcDialogues.Routes,
                gameplay: gameplay);
            Console.WriteLine(
                $"[monster-combat-balance] loaded rows={reader.Gameplay.MonsterCombatBalances.Count} " +
                $"revision={MonsterCombatBalanceContent.CoordinationRevision(reader.Gameplay.MonsterCombatBalances)}");
            stopwatch.Stop();
            WorldContentMetrics.RecordLoad(
                PostgresWorldContentSource,
                "success",
                stopwatch.Elapsed);
            return reader;
        }
        catch (WorldContentUnavailableException ex)
        {
            stopwatch.Stop();
            WorldContentMetrics.RecordRejection(ex.Family, ex.Reason);
            WorldContentMetrics.RecordLoad(
                PostgresWorldContentSource,
                "rejected",
                stopwatch.Elapsed);
            throw;
        }
        catch
        {
            stopwatch.Stop();
            WorldContentMetrics.RecordLoad(
                PostgresWorldContentSource,
                "error",
                stopwatch.Elapsed);
            throw;
        }
    }

}
