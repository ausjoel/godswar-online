using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    internal static async Task RunPlayerSkillBookOnlyAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new CheckSkippedException($"{PlayerSkillBookContractChecks.CheckName} " +
                $"({ConnectionStringVariable} is not set)");
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var database = await ReadDatabaseNameAsync(dataSource);
        if (!DisposableDatabasePattern.IsMatch(database))
        {
            throw new CheckSkippedException($"{PlayerSkillBookContractChecks.CheckName} requires " +
                $"a disposable B03/B12 database; received '{database}'");
        }

        await new PostgresSchemaMigrationRunner(dataSource)
            .InitializeGodswarSchemaAsync();
        await PostgresRelationalContentBaselineBootstrapper.EnsureAsync(
            connectionString);
        var gameplayPublication =
            await PostgresGameplayContentPublisher.EnsurePublishedAsync(
                connectionString);
        GameplayItemContent itemContent;
        IPetContentCatalog petContent;
        await using (var store = new PostgresGameStore(connectionString))
        {
            await store.EnsureSeedDataAsync();
            itemContent = store.ItemContent;
            petContent = store.PetContent;
        }

        var gameplayRevision = gameplayPublication.Revision;
        var ownerMerge =
            await PostgresPetOwnerMergeContentBootstrapper.LoadAsync(
                dataSource);
        var learned =
            await PostgresPetLearnedSkillContentBootstrapper.LoadAsync(
                connectionString);
        var options = new PostgresOutboxDispatcherOptions();
        var executor = CreatePlayerBookExecutor(
            dataSource,
            options,
            itemContent,
            petContent,
            ownerMerge,
            learned,
            gameplayRevision);
        var restarted = CreatePlayerBookExecutor(
            dataSource,
            options,
            itemContent,
            petContent,
            ownerMerge,
            learned,
            gameplayRevision);
        var fixture = await CreateFixtureAsync(connectionString);
        var subject = new CommandSubject(
            fixture.AccountId,
            fixture.CharacterId);
        var correlation = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.LegacyTcp);

        const short tierOneSlot = 80;
        var tierOneItem = await SeedBagItemAsync(
            dataSource,
            fixture.CharacterId,
            tierOneSlot,
            itemId: 5_019,
            stack: 1);
        var tierOneEnvelope = PlayerBookEnvelope(
            subject,
            correlation,
            tierOneSlot);
        var tierOneResults = await Task.WhenAll(
            executor.ExecuteAsync(tierOneEnvelope),
            restarted.ExecuteAsync(tierOneEnvelope));
        AssertCommitAndDuplicate(
            tierOneResults,
            PetDurableReceiptStatus.PlayerSkillLearned,
            "concurrent Blood Claw I activation");
        var tierOneReceipt = tierOneResults.Single(result =>
            result.Disposition ==
                PetDurableExecutionDisposition.Committed).Receipt!;
        Check.True(
            tierOneReceipt.PlayerSkillLearn is
            {
                ItemTemplateId: 5_019,
                SkillId: 50,
                SkillLevel: 1,
                PreviousSkillId: null,
                BaseName: "Blood Claw",
                Profession: 0,
                CharacterLevel: 80
            } evidence &&
            evidence.ItemInstanceId == tierOneItem &&
            evidence.GameplayContentRevision == gameplayRevision &&
            PetDurablePersistenceCodec.Decode(
                PetDurablePersistenceCodec.Encode(tierOneReceipt)) ==
                tierOneReceipt,
            "tier-one receipt pins the item, fighter, level, and content revision");
        var tierOneState = await ReadPlayerBookStateAsync(
            dataSource,
            fixture.CharacterId);
        Check.True(
            tierOneState == new PlayerBookState(
                FamilyCount: 1,
                SkillId: 50,
                SkillLevel: 1,
                InventoryRevision: 1,
                LedgerCount: 1) &&
            await ReadItemStackOrZeroAsync(dataSource, tierOneItem) == 0,
            "tier one inserts one family row and consumes the exact singleton book");

        const short tierTwoSlot = 81;
        var tierTwoItem = await SeedBagItemAsync(
            dataSource,
            fixture.CharacterId,
            tierTwoSlot,
            itemId: 5_020,
            stack: 2);
        var tierTwoEnvelope = PlayerBookEnvelope(
            subject,
            correlation,
            tierTwoSlot);
        var tierTwo = await executor.ExecuteAsync(tierTwoEnvelope);
        var tierTwoReplay = await restarted.ExecuteAsync(tierTwoEnvelope);
        var tierTwoState = await ReadPlayerBookStateAsync(
            dataSource,
            fixture.CharacterId);
        Check.True(
            tierTwo is
            {
                Disposition: PetDurableExecutionDisposition.Committed,
                Receipt.Status: PetDurableReceiptStatus.PlayerSkillLearned,
                Receipt.PlayerSkillLearn:
                {
                    SkillId: 51,
                    SkillLevel: 2,
                    PreviousSkillId: 50
                }
            } &&
            tierTwoReplay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            tierTwoReplay.Receipt == tierTwo.Receipt &&
            tierTwoState == new PlayerBookState(1, 51, 2, 2, 2) &&
            await ReadItemStackOrZeroAsync(dataSource, tierTwoItem) == 1,
            "tier two atomically replaces tier one, decrements one stack, and replays exactly");

        var duplicate = await executor.ExecuteAsync(PlayerBookEnvelope(
            subject,
            correlation,
            tierTwoSlot));
        Check.True(
            duplicate is
            {
                Disposition:
                    PetDurableExecutionDisposition.TerminalRejected,
                Receipt.Status:
                    PetDurableReceiptStatus.PlayerSkillBookAlreadyLearned
            } &&
            await ReadItemStackOrZeroAsync(dataSource, tierTwoItem) == 1 &&
            await ReadPlayerBookStateAsync(
                dataSource,
                fixture.CharacterId) == tierTwoState,
            "equal tier is rejected without consuming or mutating the family");

        await AssertRejectedPlayerBookAsync(
            executor,
            dataSource,
            subject,
            correlation,
            fixture.CharacterId,
            bagSlot: 82,
            itemId: 5_022,
            PetDurableReceiptStatus.PlayerSkillBookPriorTierRequired,
            tierTwoState,
            "skipped tier");
        await AssertRejectedPlayerBookAsync(
            executor,
            dataSource,
            subject,
            correlation,
            fixture.CharacterId,
            bagSlot: 83,
            itemId: 5_804,
            PetDurableReceiptStatus.PlayerSkillBookInvalidState,
            tierTwoState,
            "nullable non-tier portal book");
    }

    private static PostgresPetDurableCommandExecutor
        CreatePlayerBookExecutor(
            NpgsqlDataSource dataSource,
            PostgresOutboxDispatcherOptions options,
            GameplayItemContent itemContent,
            IPetContentCatalog petContent,
            IPetOwnerMergeContentCatalog ownerMerge,
            IPetLearnedSkillContentCatalog learned,
            string gameplayRevision) =>
        new(
            dataSource,
            options,
            itemContent,
            petContent,
            ownerMerge,
            learned,
            gameplayContentRevision: gameplayRevision);

    private static CommandEnvelope<BagItemActivationCommand>
        PlayerBookEnvelope(
            CommandSubject subject,
            CommandConnectionCorrelation correlation,
            int bagSlot) =>
        PlayerOwnershipTestFences.Bind(
            BagItemActivationCommandEnvelope.CreateRawLocal(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                new BagItemActivationCommand(
                    PetCommandOperationIdentity.RawLocalServer(
                        Guid.NewGuid(),
                        correlation.ConnectionId),
                    bagSlot,
                    BagItemActivationExecutionConstraint
                        .PlayerSkillBookOnly)));

    private static async Task AssertRejectedPlayerBookAsync(
        PostgresPetDurableCommandExecutor executor,
        NpgsqlDataSource dataSource,
        CommandSubject subject,
        CommandConnectionCorrelation correlation,
        int characterId,
        int bagSlot,
        int itemId,
        PetDurableReceiptStatus expectedStatus,
        PlayerBookState expectedState,
        string scenario)
    {
        var instanceId = await SeedBagItemAsync(
            dataSource,
            characterId,
            bagSlot,
            itemId,
            stack: 1);
        var result = await executor.ExecuteAsync(PlayerBookEnvelope(
            subject,
            correlation,
            bagSlot));
        Check.True(
            result.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            result.Receipt?.Status == expectedStatus &&
            await ReadItemStackOrZeroAsync(dataSource, instanceId) == 1 &&
            await ReadPlayerBookStateAsync(dataSource, characterId) ==
                expectedState,
            $"{scenario} book is rejected without consumption or tier mutation");
    }

    private static async Task<PlayerBookState> ReadPlayerBookStateAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                count(*) FILTER (
                    WHERE skill.skill_id BETWEEN 50 AND 54),
                COALESCE(max(skill.skill_id) FILTER (
                    WHERE skill.skill_id BETWEEN 50 AND 54), -1),
                COALESCE(max(skill.skill_level) FILTER (
                    WHERE skill.skill_id BETWEEN 50 AND 54), -1),
                character.inventory_revision,
                (
                    SELECT count(*)
                    FROM public.character_inventory_ledger ledger
                    WHERE ledger.character_id = @characterId
                      AND ledger.reason_code =
                          'player_skill_book_consumed'
                )
            FROM public.character_base character
            LEFT JOIN public.character_skills skill
              ON skill.user_id = character.id
            WHERE character.id = @characterId
            GROUP BY character.inventory_revision;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The player skill-book state disappeared.");
        }
        return new PlayerBookState(
            reader.GetInt64(0),
            reader.GetInt32(1),
            reader.GetInt16(2),
            reader.GetInt64(3),
            reader.GetInt64(4));
    }

    private static async Task<short> ReadItemStackOrZeroAsync(
        NpgsqlDataSource dataSource,
        long itemInstanceId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT stack
            FROM public.character_items
            WHERE id = @itemInstanceId;
            """);
        command.Parameters.AddWithValue("itemInstanceId", itemInstanceId);
        return await command.ExecuteScalarAsync() is short stack ? stack :
            (short)0;
    }

    private sealed record PlayerBookState(
        long FamilyCount,
        int SkillId,
        short SkillLevel,
        long InventoryRevision,
        long LedgerCount);
}
