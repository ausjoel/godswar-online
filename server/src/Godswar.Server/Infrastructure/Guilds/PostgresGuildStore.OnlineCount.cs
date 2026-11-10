using Npgsql;

namespace Godswar.Server.Infrastructure.Guilds;

/// <summary>
/// The guild state the window's own numbers come from.
/// </summary>
internal sealed partial class PostgresGuildStore
{
    /// <summary>
    /// Records the online member count the window was just answered with.
    /// </summary>
    /// <remarks>
    /// The count is recomputed from the live sessions on every answer; this
    /// stores the number that was actually sent so the guild's own row carries
    /// what the last window showed.
    /// </remarks>
    public async Task UpdateOnlineCountAsync(
        long guildId,
        int onlineCount,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(
            cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            UPDATE public.guilds
            SET online_count = @onlineCount,
                updated_at = @now
            WHERE id = @guildId;
            """,
            connection);
        command.Parameters.AddWithValue("guildId", guildId);
        command.Parameters.AddWithValue("onlineCount", onlineCount);
        command.Parameters.AddWithValue("now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
