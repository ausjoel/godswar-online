using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetDurableCommandIntegrationChecks
{
    public const string VampiricOwnerStatsCheckName =
        "PostgreSQL Vampiric percentage healing, tier selection and flat composition";

    public static async Task RunVampiricOwnerStatsAsync()
    {
        var connectionString = ReadRequiredConnectionString();
        await using var source = NpgsqlDataSource.Create(connectionString);
        await AssertDisposableDatabaseAsync(source);
        await new PostgresSchemaMigrationRunner(source).InitializeGodswarSchemaAsync();
        GameplayItemContent items;
        IPetContentCatalog pets;
        await using (var store = new PostgresGameStore(connectionString))
        {
            await store.EnsureSeedDataAsync();
            items = store.ItemContent;
            pets = store.PetContent;
        }
        var learned = await PostgresPetLearnedSkillContentBootstrapper.LoadAsync(connectionString);
        var merge = await PostgresPetOwnerMergeContentBootstrapper.LoadAsync(source);
        var fixture = await CreateFixtureAsync(connectionString, PetAptitude.Smart);
        var executor = new PostgresPetDurableCommandExecutor(source,
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
            "Vampiric projection starts from a valid authoritative hatched pet");
        var petId = hatch.Receipt!.PetId;
        await PrepareVampiricOwnerStatsAsync(source, petId);
        async Task<(int Flat, int Percentage)> ReadAsync() =>
            await ReadExtractionOwnerStatsAsync(source, items, learned,
                fixture.AccountId, fixture.CharacterId);
        Check.Equal((225, 0), await ReadAsync(),
            "native Extraction remains a flat contribution before Vampiric is learned");
        var percentages = new[] { 600, 800, 1100, 1400, 1700, 2000 };
        for (short tier = 1; tier <= 6; tier++)
        {
            await SetVampiricTierAsync(source, petId, tier);
            Check.Equal((225, percentages[tier - 1]), await ReadAsync(),
                $"Vampiric tier {tier} projects fractional content into basis points without changing flat healing");
        }
        await ExecuteVampiricFixtureSqlAsync(source, petId,
            """
            INSERT INTO public.character_pet_skills
                (pet_id, skill_id, slot_index, skill_rank, skill_experience, is_active, revision)
            VALUES (@petId, 6400, 2, 1, 0, true, 0);
            """);
        Check.Equal((225, 2000), await ReadAsync(),
            "simultaneously active lower and higher Vampiric tiers cannot stack");
        await ExecuteVampiricFixtureSqlAsync(source, petId,
            "UPDATE public.character_pet_skills SET is_active = false WHERE pet_id = @petId AND skill_id = 6405;");
        Check.Equal((225, 600), await ReadAsync(),
            "an inactive higher tier does not suppress the active lower Vampiric tier");
        await ExecuteVampiricFixtureSqlAsync(source, petId,
            "UPDATE public.character_pet_skills SET is_active = false WHERE pet_id = @petId AND skill_id = 6400;");
        Check.Equal((225, 0), await ReadAsync(),
            "inactive Vampiric supplies no percentage healing while native flat healing remains");
        await SetVampiricTierAsync(source, petId, 6);
        await ExecuteVampiricFixtureSqlAsync(source, petId,
            "UPDATE public.character_pets SET is_carried = false WHERE id = @petId;");
        Check.Equal((0, 0), await ReadAsync(),
            "uncarried pets supply neither learned flat healing nor Vampiric percentage healing");
        await ExecuteVampiricFixtureSqlAsync(source, petId,
            """
            UPDATE public.character_pets
            SET is_carried = true, is_summoned = true, contributes_to_character = true
            WHERE id = @petId;
            """);
        Check.Equal((669, 2000), await ReadAsync(),
            "225 native learned healing and 444 Merge healing remain flat alongside 20 percent Vampiric");
        await ExecuteVampiricFixtureSqlAsync(source, petId,
            "UPDATE public.character_pet_skills SET is_active = false WHERE pet_id = @petId AND skill_id = 2018;");
        Check.Equal((444, 2000), await ReadAsync(),
            "Vampiric works independently of Extraction and preserves Strength-based Merge healing");
        await ExecuteVampiricFixtureSqlAsync(source, petId,
            "UPDATE public.character_pets SET contributes_to_character = false, is_summoned = false WHERE id = @petId;");
        Check.Equal((0, 2000), await ReadAsync(),
            "a carried recalled pet retains Vampiric without requiring owner Merge");
    }

    private static async Task PrepareVampiricOwnerStatsAsync(NpgsqlDataSource source, long petId) =>
        await ExecuteVampiricFixtureSqlAsync(source, petId,
            """
            UPDATE public.character_pets
            SET rank = 30, is_carried = true, is_summoned = false,
                contributes_to_character = false, opened_skill_slots = 3, available_skill_slots = 3
            WHERE id = @petId;
            DELETE FROM public.character_pet_skills WHERE pet_id = @petId;
            INSERT INTO public.character_pet_skills
                (pet_id, skill_id, slot_index, skill_rank, skill_experience, is_active, revision)
            VALUES (@petId, 2018, 0, 2, 0, true, 0);
            INSERT INTO public.character_pet_character_bonuses (pet_id, effect_code, effect_value, revision)
            VALUES (@petId, 34, 444, 0)
            ON CONFLICT (pet_id, effect_code) DO UPDATE SET effect_value = EXCLUDED.effect_value;
            """);

    private static async Task SetVampiricTierAsync(NpgsqlDataSource source, long petId, short tier)
    {
        await using var command = source.CreateCommand(
            """
            DELETE FROM public.character_pet_skills WHERE pet_id = @petId AND skill_id BETWEEN 6400 AND 6405;
            INSERT INTO public.character_pet_skills
                (pet_id, skill_id, slot_index, skill_rank, skill_experience, is_active, revision)
            VALUES (@petId, @skillId, 1, @tier, 0, true, 0);
            """);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("skillId", 6399 + tier);
        command.Parameters.AddWithValue("tier", tier);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteVampiricFixtureSqlAsync(NpgsqlDataSource source, long petId, string sql)
    {
        await using var command = source.CreateCommand(sql);
        command.Parameters.AddWithValue("petId", petId);
        await command.ExecuteNonQueryAsync();
    }
}
