using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetDurableCommandIntegrationChecks
{
    public const string ExtractionOwnerStatsCheckName =
        "PostgreSQL Extraction learned healing and owner Merge composition";

    public static async Task RunExtractionOwnerStatsAsync()
    {
        var connectionString = ReadRequiredConnectionString();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await AssertDisposableDatabaseAsync(dataSource);
        await new PostgresSchemaMigrationRunner(dataSource).InitializeGodswarSchemaAsync();
        GameplayItemContent items;
        IPetContentCatalog pets;
        await using (var store = new PostgresGameStore(connectionString))
        {
            await store.EnsureSeedDataAsync();
            items = store.ItemContent;
            pets = store.PetContent;
        }
        var learned = await PostgresPetLearnedSkillContentBootstrapper.LoadAsync(connectionString);
        var merge = await PostgresPetOwnerMergeContentBootstrapper.LoadAsync(dataSource);
        var fixture = await CreateFixtureAsync(connectionString, PetAptitude.Smart);
        var executor = new PostgresPetDurableCommandExecutor(dataSource,
            new PostgresOutboxDispatcherOptions(), items, pets, merge, learned,
            new FixedPetHatchRankRollSource(89));
        var correlation = new CommandConnectionCorrelation(Guid.NewGuid(), CommandTransportKind.LegacyTcp);
        var hatch = await executor.ExecuteAsync(PlayerOwnershipTestFences.Bind(
            BagItemActivationCommandEnvelope.CreateRawLocal(
                new CommandSubject(fixture.AccountId, fixture.CharacterId), correlation, DateTimeOffset.UtcNow,
                new BagItemActivationCommand(PetCommandOperationIdentity.RawLocalServer(
                    Guid.NewGuid(), correlation.ConnectionId), fixture.EggSlot))));
        Check.True(hatch.Disposition == PetDurableExecutionDisposition.Committed &&
            hatch.Receipt?.Status == PetDurableReceiptStatus.EggHatched,
            "Extraction projection fixture starts from an authoritative hatched pet");
        var petId = hatch.Receipt!.PetId;
        var excluded = learned.Curves.First(curve =>
            curve.Effect == 34 && curve.FamilyType is not (341 or 428));
        await PrepareExtractionOwnerStatsAsync(dataSource, petId,
            excluded.FirstRuntimeSkillId, excluded.Priority);

        async Task<(int Flat, int Percentage)> ReadAsync() =>
            await ReadExtractionOwnerStatsAsync(dataSource, items, learned,
                fixture.AccountId, fixture.CharacterId);
        var baseline = await ReadAsync();
        Check.Equal((0, 0), baseline,
            "inactive Extraction and an active unreviewed same-effect family grant no life absorption");
        foreach (var (rank, expected) in new[]
                 { (1m, 180), (17.99m, 180), (18m, 193), (24m, 208), (29.99m, 208), (30m, 225) })
        {
            await SetExtractionOwnerStateAsync(dataSource, petId, true, rank, true, false);
            Check.Equal((expected, 0), await ReadAsync(),
                $"Extraction II at pet rank {rank} supplies its published flat value without percentage conversion");
        }
        await SetExtractionOwnerStateAsync(dataSource, petId, false, 30m, true, false);
        Check.Equal(baseline, await ReadAsync(), "an uncarried pet cannot supply Extraction healing");
        await SetExtractionOwnerStateAsync(dataSource, petId, true, 30m, false, false);
        Check.Equal(baseline, await ReadAsync(), "deactivating the learned skill removes Extraction healing");
        await SetExtractionOwnerStateAsync(dataSource, petId, true, 30m, true, true);
        Check.Equal((669, 0), await ReadAsync(),
            "learned 225 healing adds exactly once to a separate 444 owner-Merge contribution");
        await SetExtractionOwnerStateAsync(dataSource, petId, true, 30m, false, true);
        Check.Equal((444, 0), await ReadAsync(),
            "disabling Extraction leaves the independent owner-Merge flat healing intact");
    }

    private static async Task PrepareExtractionOwnerStatsAsync(NpgsqlDataSource source, long petId,
        int excludedSkillId, short excludedPriority)
    {
        await using var command = source.CreateCommand(
            """
            UPDATE public.character_pets
            SET rank = 1, is_carried = true, is_summoned = false,
                contributes_to_character = false, opened_skill_slots = 2, available_skill_slots = 2
            WHERE id = @petId;
            DELETE FROM public.character_pet_skills WHERE pet_id = @petId;
            INSERT INTO public.character_pet_skills
                (pet_id, skill_id, slot_index, skill_rank, skill_experience, is_active, revision)
            VALUES (@petId, 2018, 0, 2, 0, false, 0),
                   (@petId, @excludedSkillId, 1, @excludedPriority, 0, true, 0);
            INSERT INTO public.character_pet_character_bonuses (pet_id, effect_code, effect_value, revision)
            VALUES (@petId, 34, 444, 0)
            ON CONFLICT (pet_id, effect_code) DO UPDATE SET effect_value = EXCLUDED.effect_value;
            """);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("excludedSkillId", excludedSkillId);
        command.Parameters.AddWithValue("excludedPriority", excludedPriority);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetExtractionOwnerStateAsync(NpgsqlDataSource source, long petId,
        bool carried, decimal rank, bool active, bool merged)
    {
        await using var command = source.CreateCommand(
            """
            UPDATE public.character_pets
            SET is_carried = @carried, rank = @rank,
                is_summoned = @merged, contributes_to_character = @merged
            WHERE id = @petId;
            UPDATE public.character_pet_skills SET is_active = @active
            WHERE pet_id = @petId AND skill_id = 2018;
            """);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("carried", carried);
        command.Parameters.AddWithValue("rank", rank);
        command.Parameters.AddWithValue("active", active);
        command.Parameters.AddWithValue("merged", merged);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(int Flat, int Percentage)> ReadExtractionOwnerStatsAsync(
        NpgsqlDataSource source, GameplayItemContent items, IPetLearnedSkillContentCatalog learned,
        int accountId, int characterId)
    {
        await using var command = source.CreateCommand(
            PostgresCharacterRuntimeItemProjectionSql.CalculatedStatsForCharacter);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("itemContentRevision", items.Templates.Revision.Sha256);
        command.Parameters.AddWithValue(PostgresGameplayContentBinding.ParameterName,
            NpgsqlDbType.Varchar, DBNull.Value);
        command.Parameters.AddWithValue(PostgresPetLearnedSkillContentBinding.ParameterName,
            learned.Revision.Sha256);
        PostgresHolySpiritBalanceBinding.AddParameters(command,
            await PostgresHolySpiritBalanceSnapshotReader.LoadAsync(source));
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "full character projection includes the Extraction owner");
        return (reader.GetInt32(reader.GetOrdinal("life_absorption_flat")),
            reader.GetInt32(reader.GetOrdinal("life_absorption")));
    }
}
