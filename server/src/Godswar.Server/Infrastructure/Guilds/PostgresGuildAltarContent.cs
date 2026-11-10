using Godswar.Server.Application.Guilds;
using Npgsql;

namespace Godswar.Server.Infrastructure.Guilds;

/// <summary>
/// The client's altar content, read from the table the seed migration filled.
/// </summary>
/// <remarks>
/// <c>guild_building_levels</c> carries the client's own
/// <c>worship_impact_type</c>/<c>worship_impact_value</c> for every altar level,
/// which is where <c>Consortia.xml</c>'s <c>BuildingImpact</c> block put them.
/// The rows never change at runtime, so they are read once and kept; a database
/// read on every attribute recomputation would sit on the combat and login paths.
///
/// A level the table has no row for reads back as "no bonus". That covers three
/// real cases with one answer: an altar the guild has not built (level 0, which
/// is never asked), a building that grants no worship bonus at all (the column is
/// <c>-1</c>), and an altar level above the content's own ceiling.
/// </remarks>
internal sealed class PostgresGuildAltarContent : IGuildAltarContent
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyDictionary<(int BuildingType, int Level), (int Type, int Value)>?
        _levels;

    public PostgresGuildAltarContent(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <summary>
    /// This altar level's raw <c>WorshipImpactType</c> and its base value, which
    /// is the shape <see cref="GuildAltarWorshipBonusPolicy.Resolve"/> wants.
    /// </summary>
    /// <remarks>
    /// The type travels raw, exactly as the client wrote it. Naming it is the
    /// policy's job, so that a type the client never puts on an altar is rejected
    /// in one place instead of being silently accepted here.
    /// </remarks>
    public (int Type, int Value) ContentOf(int buildingType, int altarLevel)
    {
        if (altarLevel <= 0)
        {
            return (NoWorshipImpactType, 0);
        }

        // The caller is on a hot path that cannot await, so the read happens the
        // first time content is needed and is deliberately blocking: it is one
        // small query, once per process.
        var levels = _levels ?? LoadAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        return levels.TryGetValue((buildingType, altarLevel), out var level)
            ? level
            : (NoWorshipImpactType, 0);
    }

    /// <summary>
    /// What the content table stores for a building level that grants no worship
    /// bonus (see the seed migration's own column default).
    /// </summary>
    internal const int NoWorshipImpactType = -1;

    private async Task<IReadOnlyDictionary<(int, int), (int, int)>>
        LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_levels is not null)
            {
                return _levels;
            }

            await using var connection = await _dataSource.OpenConnectionAsync(
                cancellationToken);
            await using var command = new NpgsqlCommand(
                """
                SELECT building_type, level, worship_impact_type, worship_impact_value
                FROM public.guild_building_levels;
                """,
                connection);
            var levels = new Dictionary<(int, int), (int, int)>();
            await using (var reader = await command.ExecuteReaderAsync(
                cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    levels[(reader.GetInt32(0), reader.GetInt16(1))] = (
                        reader.GetInt16(2),
                        reader.GetInt32(3));
                }
            }

            _levels = levels;
            Console.WriteLine(
                $"[guild] altar worship content loaded levels={levels.Count}");
            return levels;
        }
        finally
        {
            _gate.Release();
        }
    }
}
