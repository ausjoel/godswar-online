using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Diagnostic: runs the item acquisition policy the reward and loot stores use
/// against a live database, for the item ids a quest reward refused.
/// </summary>
/// <remarks>
/// The store answers an item it cannot read with <c>Unsupported</c> and writes
/// nothing, so this check exists to show whether the policy - not the bag, the
/// claim or the character - is what refused. Point
/// <c>GODSWAR_REWARD_DIAGNOSTIC_CONNECTION_STRING</c> at the database.
/// </remarks>
internal static class PostgresItemAcquisitionPolicyDiagnosticChecks
{
    public const string CheckName = "PostgreSQL item acquisition policy diagnostic";

    private const string ConnectionStringVariable =
        "GODSWAR_REWARD_DIAGNOSTIC_CONNECTION_STRING";

    private static readonly uint[] ProbedItems =
        [2100u, 3876u, 1800u, 1808u, 4233u, 4529u];

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new CheckSkippedException(
                $"{CheckName} ({ConnectionStringVariable} is not set)");
        }

        if (Environment.GetEnvironmentVariable(
                "GODSWAR_REWARD_DIAGNOSTIC_APPLY_MIGRATIONS") == "1")
        {
            try
            {
                await Infrastructure.Database.PostgresSchemaStartup
                    .InitializeAsync(connectionString);
                Console.WriteLine(
                    "[reward-diagnostic] migrations applied");
            }
            catch (Exception exception)
            {
                Console.WriteLine(
                    "[reward-diagnostic] migration failure: " +
                    exception.GetType().Name + ": " + exception.Message);
                throw;
            }
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var itemId in ProbedItems)
        {
            var policy = await PostgresItemAcquisitionPolicy
                .ReadLootItemPolicyAsync(
                    connection,
                    transaction,
                    itemId,
                    CancellationToken.None);
            Console.WriteLine(
                $"[reward-diagnostic] item={itemId} policy=" +
                (policy is { } value
                    ? $"stackCap={value.StackCap} bound={value.Bound}"
                    : "NONE"));
        }

        await using (var command = dataSource.CreateCommand(
            "SELECT count(*)::text FROM public.official_item_template_content"))
        {
            Console.WriteLine(
                "[reward-diagnostic] official rows=" +
                await command.ExecuteScalarAsync());
        }

        await using (var command = dataSource.CreateCommand(
            "SELECT stats IS NULL, length(stats::text) FROM public.official_item_template_content WHERE id = 2100"))
        {
            await using var reader = await command.ExecuteReaderAsync();
            Console.WriteLine(
                await reader.ReadAsync()
                    ? $"[reward-diagnostic] 2100 stats_null={reader.GetBoolean(0)} len={reader.GetInt32(1)}"
                    : "[reward-diagnostic] 2100 has no row");
        }

        await transaction.RollbackAsync();

        await CheckLiveGrantAsync(dataSource);
    }

    /// <summary>
    /// Runs the reward grant itself against the live database on a throwaway
    /// account, which is the only way to tell a policy miss apart from anything
    /// else the store refuses. The fixture is deleted afterwards.
    /// </summary>
    private static string Nullable(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? "-" : reader.GetInt16(ordinal).ToString();

    private static async Task CheckLiveGrantAsync(NpgsqlDataSource dataSource)
    {
        var token = Guid.NewGuid().ToString("N")[..10];
        int accountId;
        await using (var account = dataSource.CreateCommand(
            """
            INSERT INTO public.accounts (username, password)
            VALUES (@username, '')
            RETURNING id;
            """))
        {
            account.Parameters.AddWithValue("username", $"reward_diag_{token}");
            accountId = Convert.ToInt32(await account.ExecuteScalarAsync());
        }

        int characterId;
        await using (var character = dataSource.CreateCommand(
            """
            INSERT INTO public.character_base (
                account_id, server_id, name, camp, profession, fighter_job_lv,
                "Map", "Pos_X", "Pos_Z", lifecycle_state, lifecycle_version)
            SELECT @accountId, (SELECT id FROM public.server ORDER BY id LIMIT 1),
                   @name, 0, 3, 1, 0, 165, -97, 'active', 1
            RETURNING id;
            """))
        {
            character.Parameters.AddWithValue("accountId", accountId);
            character.Parameters.AddWithValue("name", $"RewardDiag{token}");
            characterId = Convert.ToInt32(await character.ExecuteScalarAsync());
        }

        // Every ledger row this store writes is fenced by the character's economy
        // baseline (fk_character_inventory_ledger_baseline), so the fixture needs
        // one exactly like a created character does.
        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var baseline = await connection.BeginTransactionAsync())
        {
            var captured = await Infrastructure.Inventory
                .PostgresCharacterEconomyBaseline.EnsureAsync(
                    connection,
                    baseline,
                    accountId,
                    characterId,
                    commandTimeoutSeconds: 30,
                    CancellationToken.None);
            await baseline.CommitAsync();
            Console.WriteLine(
                $"[reward-diagnostic] baseline ensured={captured}");
        }

        try
        {
            var store = new Infrastructure.Rewards
                .PostgresMonsterRewardExtrasStore(
                    dataSource,
                    static (_, _) => Task.FromResult<
                        Godswar.Server.State.GameCharacter?>(null));
            foreach (var itemId in new uint[] { 2100u, 3876u, 1808u })
            {
                var result = await store.GrantQuestRewardItemAsync(
                    accountId,
                    characterId,
                    520u,
                    slotIndex: (int)(itemId % 7),
                    itemId,
                    quantity: 1,
                    new Godswar.Server.State.ItemGrantAttributes(
                        10, 12, 24, 5, 133, null, 90, null, null, null, null, null));
                Console.WriteLine(
                    $"[reward-diagnostic] live grant item={itemId} " +
                    $"status={result.Status}");
            }

            await using var count = dataSource.CreateCommand(
                "SELECT count(*)::text FROM public.character_items WHERE user_id = @characterId");
            count.Parameters.AddWithValue("characterId", characterId);
            Console.WriteLine(
                "[reward-diagnostic] live bag rows=" +
                await count.ExecuteScalarAsync());

            await using var attributes = dataSource.CreateCommand(
                """
                SELECT prop_id, item_quality, item_grade,
                       attribute1, attribute_level1, attribute2, attribute3,
                       attribute4, attribute5
                FROM public.character_items
                WHERE user_id = @characterId
                ORDER BY id;
                """);
            attributes.Parameters.AddWithValue("characterId", characterId);
            await using var attributeReader = await attributes.ExecuteReaderAsync();
            while (await attributeReader.ReadAsync())
            {
                // An unset slot has to print as "-", which is what NULL looks
                // like: id 0 is the real AttackA the client draws as a phantom
                // "physical attack I" line.
                Console.WriteLine(
                    $"[reward-diagnostic] bag item={attributeReader.GetInt32(0)} " +
                    $"quality={attributeReader.GetInt16(1)} grade={attributeReader.GetInt16(2)} " +
                    $"attr1={Nullable(attributeReader, 3)}/{Nullable(attributeReader, 4)} " +
                    $"attr2={Nullable(attributeReader, 5)} attr3={Nullable(attributeReader, 6)} " +
                    $"attr4={Nullable(attributeReader, 7)} attr5={Nullable(attributeReader, 8)}");
            }

            // The handler holds the broad game store rather than the dedicated one,
            // and that store implements the reward interface by forwarding: a member
            // it forgets to forward falls back to the interface default, which
            // refuses the work. This call therefore goes through the object the
            // handler actually uses, which is the path a quest reward takes - with
            // the pinned item content the server pins at startup, since the store
            // refuses gameplay work before that.
            await using var gameStore =
                new Godswar.Server.State.PostgresGameStore(
                    Environment.GetEnvironmentVariable(
                        ConnectionStringVariable)!,
                    new Godswar.Server.State.GameplayItemContent(
                        await Godswar.Server.Infrastructure.Items
                            .PostgresItemTemplateContentBootstrapper.LoadAsync(
                                dataSource)));
            var forwarded = await gameStore.GrantQuestRewardItemAsync(
                accountId,
                characterId,
                519u,
                1,
                1808u,
                1,
                Godswar.Server.State.ItemGrantAttributes.None);
            Console.WriteLine(
                "[reward-diagnostic] game-store forwarding status=" +
                forwarded.Status);
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand(
                "DELETE FROM public.accounts WHERE id = @accountId;");
            cleanup.Parameters.AddWithValue("accountId", accountId);
            await cleanup.ExecuteNonQueryAsync();
            Console.WriteLine("[reward-diagnostic] fixture deleted");
        }
    }
}
