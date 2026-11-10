using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.OnlineAwards;
using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresOnlineAwardIntegrationChecks
{
    private static async Task<OnlineAwardFixture>
        CreateOnlineAwardFixtureAsync(
        NpgsqlDataSource dataSource,
        string scenario,
        bool fillBag = false)
    {
        var token = Guid.NewGuid().ToString("N")[..10];
        var shortScenario = scenario[..Math.Min(8, scenario.Length)];
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        int accountId;
        await using (var account = new NpgsqlCommand(
            """
            INSERT INTO public.accounts (username, password)
            VALUES (@username, '') RETURNING id;
            """,
            connection,
            transaction))
        {
            account.Parameters.AddWithValue(
                "username",
                $"oa_{shortScenario}_{token}");
            accountId = Convert.ToInt32(await account.ExecuteScalarAsync());
        }

        var realmId = Convert.ToInt32(await new NpgsqlCommand(
            "SELECT id FROM public.server ORDER BY id LIMIT 1;",
            connection,
            transaction).ExecuteScalarAsync());
        int characterId;
        await using (var character = new NpgsqlCommand(
            """
            INSERT INTO public.character_base (
                account_id, server_id, name, camp, profession,
                fighter_job_lv, fighter_job_exp, "SkillPoint",
                "Money", "Stone", "BindingGold")
            VALUES (@accountId, @realmId, @name, 1, 0,
                    80, 0, 0, 0, 0, 0)
            RETURNING id;
            """,
            connection,
            transaction))
        {
            character.Parameters.AddWithValue("accountId", accountId);
            character.Parameters.AddWithValue("realmId", realmId);
            character.Parameters.AddWithValue(
                "name",
                $"OA{shortScenario}{token}");
            characterId = Convert.ToInt32(
                await character.ExecuteScalarAsync());
        }

        if (fillBag)
        {
            await using var fill = new NpgsqlCommand(
                """
                INSERT INTO public.character_items (
                    user_id, item_location, slot_index, prop_id,
                    item_quality, item_grade, bound, stack,
                    item_exp, holy_suit_code)
                SELECT @characterId, 1, slot::smallint, 10150,
                       1, 1, 0, 1, 0, 0
                FROM generate_series(0, 95) slot;
                """,
                connection,
                transaction);
            fill.Parameters.AddWithValue("characterId", characterId);
            Check.Equal(96, await fill.ExecuteNonQueryAsync(),
                "Online Award full-bag fixture owns all slots");
        }

        Check.True(
            await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                accountId,
                characterId,
                30,
                CancellationToken.None),
            "Online Award fixture captures an economy baseline");
        var ownership = await PlayerOwnershipTestFences.InstallAsync(
            connection,
            transaction,
            accountId,
            characterId);
        await transaction.CommitAsync();
        return new(
            accountId,
            characterId,
            realmId,
            new CommandSubject(accountId, characterId),
            ownership);
    }

    private static CommandEnvelope<OnlineAwardCommand> CreateEnvelope(
        OnlineAwardFixture fixture,
        RealmCalendar calendar,
        Guid operationId,
        DateTimeOffset receivedAt,
        int npcId = (int)OnlineAwardProtocol.AthensNpcId)
    {
        var identity = OnlineAwardOperationIdentity.SecureClient(operationId);
        if (!OnlineAwardCommandEnvelope.TryCreate(
                identity,
                fixture.RealmId,
                npcId,
                49,
                calendar.GetDay(receivedAt).DayNumber,
                out var command))
        {
            throw new InvalidOperationException(
                "The Online Award fixture command is invalid.");
        }
        return CommandEnvelopeContract.BindOwnership(
            OnlineAwardCommandEnvelope.Create(
                fixture.Subject,
                new CommandConnectionCorrelation(
                    Guid.NewGuid(),
                    CommandTransportKind.SecureTlsLegacy),
                receivedAt,
                command),
            fixture.Ownership);
    }

    private sealed record OnlineAwardFixture(
        int AccountId,
        int CharacterId,
        int RealmId,
        CommandSubject Subject,
        PlayerOwnershipFence Ownership);

    private sealed record HistoricalClaim(
        CommandEnvelope<OnlineAwardCommand> Envelope,
        OnlineAwardExecutionReceipt Receipt);
}
