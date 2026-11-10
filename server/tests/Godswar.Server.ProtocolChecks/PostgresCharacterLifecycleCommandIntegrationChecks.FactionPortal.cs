using Godswar.Server.Domain.Characters;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresCharacterLifecycleCommandIntegrationChecks
{
    private static async Task AssertFactionPortalCreationAsync(
        string connectionString,
        NpgsqlDataSource dataSource,
        PostgresCharacterLifecycleCommandExecutor executor,
        CommandConnectionCorrelation correlation,
        string token,
        int spartaCharacterId)
    {
        await AssertOnlyFactionCapitalPortalAsync(
            dataSource,
            spartaCharacterId,
            GameDefaults.SpartaCamp);

        var athensAccount = await CreateAccountAsync(connectionString);
        var athensCreate = await executor.ExecuteAsync(
            CreateEnvelope(
                athensAccount.Id,
                correlation,
                Guid.NewGuid(),
                $"Athens{token}",
                GameDefaults.AthensCamp));
        Check.True(
            athensCreate is
            {
                Disposition:
                    CharacterLifecycleExecutionDisposition.Committed,
                Receipt.Status:
                    CharacterLifecycleReceiptStatus.Created
            },
            "Athens faction-portal fixture creates successfully");
        await AssertOnlyFactionCapitalPortalAsync(
            dataSource,
            athensCreate.Receipt!.CharacterId,
            GameDefaults.AthensCamp);
    }

    private static async Task AssertOnlyFactionCapitalPortalAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        byte camp)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT skill_id, skill_level, source
            FROM public.character_skills
            WHERE user_id = @characterId
              AND skill_id IN (3060, 3061, 3062, 3063)
            ORDER BY skill_id;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync();
        var portals = new List<(uint SkillId, short Level, string Source)>();
        while (await reader.ReadAsync())
        {
            portals.Add((
                checked((uint)reader.GetInt32(0)),
                reader.GetInt16(1),
                reader.GetString(2)));
        }

        var expectedSkillId =
            FactionPortalSkillPolicy.ResolveCapitalPortalSkillId(camp);
        Check.True(
            portals is
            [
                {
                    SkillId: var skillId,
                    Level: 1,
                    Source: "faction-starter"
                }
            ] && skillId == expectedSkillId,
            $"camp {camp} creation persists only its level-one capital portal");
    }
}
