using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static partial class PostgresWorldContentReaderLoader
{
    private const int MaximumMonsterCombatBalanceRows = 4_096;

    /// <summary>
    /// Reads mutable tuning after the immutable gameplay revision is verified.
    /// The caller retains the same read-only repeatable-read transaction. The
    /// returned startup snapshot changes only when the server reloads content;
    /// these rows never participate in the sealed gameplay revision hash.
    /// </summary>
    internal static async Task<MonsterCombatBalanceDefinition[]>
        LoadMonsterCombatBalancesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            GameplayContentCatalog gameplay,
            CancellationToken cancellationToken)
    {
        var values = new List<MonsterCombatBalanceDefinition>();
        await using var command = new NpgsqlCommand(
            """
            SELECT map_id, template_key, critical_resistance
            FROM public.monster_combat_balance
            ORDER BY map_id, template_key
            LIMIT @maximumRows;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "maximumRows", MaximumMonsterCombatBalanceRows + 1);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                values.Add(new(
                    reader.GetInt16(0),
                    reader.GetString(1),
                    reader.GetInt32(2)));
            }
            return MonsterCombatBalanceContent.Pin(
                values, gameplay.MonsterTemplates).ToArray();
        }
        catch (Exception error) when (
            error is InvalidDataException or InvalidCastException or OverflowException)
        {
            throw new WorldContentUnavailableException(
                "monster_combat_balance",
                WorldContentFailureReason.Invalid,
                "Monster combat balance contains invalid ratings or published boss identities.",
                error);
        }
    }
}
