using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresFactionCrierCommandIntegrationChecks
{
    private static async Task<CrierFixture> CreateFixtureAsync(
        NpgsqlDataSource dataSource,
        string scenario,
        int silver,
        int gold,
        int bindingGold,
        IReadOnlyList<NameplateStack>? items = null,
        int level = 80)
    {
        var token = Guid.NewGuid().ToString("N")[..10];
        var shortScenario = scenario[..Math.Min(7, scenario.Length)];
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        int accountId;
        await using (var account = new NpgsqlCommand(
            """
            INSERT INTO public.accounts (username, password)
            VALUES (@username, '')
            RETURNING id;
            """,
            connection,
            transaction))
        {
            account.Parameters.AddWithValue(
                "username",
                $"fc_{shortScenario}_{token}");
            accountId = Convert.ToInt32(
                await account.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The Faction Crier fixture account has no identity."));
        }

        int realmId;
        await using (var realm = new NpgsqlCommand(
            "SELECT id FROM public.server ORDER BY id LIMIT 1;",
            connection,
            transaction))
        {
            realmId = Convert.ToInt32(
                await realm.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The Faction Crier fixture has no realm."));
        }

        int characterId;
        await using (var character = new NpgsqlCommand(
            """
            INSERT INTO public.character_base (
                account_id, server_id, name, camp, profession,
                fighter_job_lv, fighter_job_exp, "SkillPoint",
                "Money", "Stone", "BindingGold")
            VALUES (
                @accountId, @realmId, @name, 1, 0,
                @level, 0, 0, @silver, @gold, @bindingGold)
            RETURNING id;
            """,
            connection,
            transaction))
        {
            character.Parameters.AddWithValue("accountId", accountId);
            character.Parameters.AddWithValue("realmId", realmId);
            character.Parameters.AddWithValue(
                "name",
                $"FC{shortScenario}{token}"[..Math.Min(
                    32,
                    2 + shortScenario.Length + token.Length)]);
            character.Parameters.AddWithValue("level", level);
            character.Parameters.AddWithValue("silver", silver);
            character.Parameters.AddWithValue("gold", gold);
            character.Parameters.AddWithValue("bindingGold", bindingGold);
            characterId = Convert.ToInt32(
                await character.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The Faction Crier fixture character has no identity."));
        }

        foreach (var item in items ?? [])
        {
            await InsertNameplateAsync(
                connection,
                transaction,
                characterId,
                item);
        }

        Check.True(
            await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                accountId,
                characterId,
                commandTimeoutSeconds: 30,
                CancellationToken.None),
            "Faction Crier fixture captures an economy baseline");
        var ownership = await PlayerOwnershipTestFences.InstallAsync(
            connection,
            transaction,
            accountId,
            characterId);
        await transaction.CommitAsync();
        return new CrierFixture(
            accountId,
            characterId,
            realmId,
            new CommandSubject(accountId, characterId),
            ownership,
            silver,
            gold,
            bindingGold);
    }

    private static async Task InsertNameplateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        NameplateStack item)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack,
                item_exp, holy_suit_code)
            VALUES (
                @characterId, 1, @slot, @itemId,
                1, 1, 1, @stack, 0, 0);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slot", item.Slot);
        command.Parameters.AddWithValue("itemId", item.ItemId);
        command.Parameters.AddWithValue("stack", item.Stack);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            $"insert Nameplate {item.ItemId} fixture stack");
    }

    private static string CompactNameplate(int itemId, short stack) =>
        CompactItemEntry.Parse(
            $"[{itemId},,,,,,1,1,1,{stack},0,0," +
            ",,,,,0,,,,,,,,,,,,]").ToCompactString();

    private sealed record CrierFixture(
        int AccountId,
        int CharacterId,
        int RealmId,
        CommandSubject Subject,
        PlayerOwnershipFence Ownership,
        int InitialSilver,
        int InitialGold,
        int InitialBindingGold);

    private readonly record struct NameplateStack(
        int ItemId,
        short Slot,
        short Stack = 1);
}
