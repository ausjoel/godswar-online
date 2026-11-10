using Godswar.Server.Game;
using Npgsql;

namespace Godswar.Server.Infrastructure.Guilds;

/// <summary>
/// What came of a create attempt.
/// </summary>
internal enum GuildCreateOutcome
{
    Created,

    /// <summary>The character already holds a membership.</summary>
    AlreadyInGuild,

    /// <summary>Another guild already uses the name.</summary>
    NameTaken,

    /// <summary>The character does not carry the Guild Stone the client demands.</summary>
    MissingGuildStone,

    /// <summary>The name is empty or longer than the client's field allows.</summary>
    InvalidName
}

internal readonly record struct GuildCreateResult(
    GuildCreateOutcome Outcome,
    long GuildId,
    string Name,
    short? ConsumedKitBagSlot = null);

/// <summary>
/// Creates guilds and their first membership.
/// </summary>
/// <remarks>
/// The create runs as one transaction: the membership probe, the name probe, the
/// Guild Stone consumption, the guild row and the founder's membership row are all
/// or nothing, so a refused attempt never costs the item and a successful one never
/// leaves a guild without its lord.
///
/// The Guild Stone is the item the client's own refusal text names
/// (<c>ERROR_035E 缺少道具&lt;天堂令&gt;</c>) and its template is
/// <c>GuildStone</c> (id 4100, the one the client ships). The founder is written
/// with duty 6, which <c>Consortia.dat</c> maps to 会长, and the guild starts at
/// the member cap <c>Consortia.xml</c> declares for a guild with no houses built.
/// </remarks>
internal sealed partial class PostgresGuildStore : GuildAltarSettlementStore
{
    /// <summary>The Guild Stone template the create consumes.</summary>
    public const int GuildStoneItemId = 4100;

    /// <summary>The client's duty id for a guild lord.</summary>
    public const short LordDuty = 6;

    /// <summary>
    /// The member cap a fresh guild starts with, from the client's own default.
    /// </summary>
    public const int StartingMemberLimit = 100;

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresGuildAltarContent _altarContent;

    public PostgresGuildStore(
        NpgsqlDataSource dataSource,
        PostgresGuildAltarContent altarContent)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(altarContent);
        _dataSource = dataSource;
        _altarContent = altarContent;
    }

    public async Task<GuildCreateResult> TryCreateAsync(
        int accountId,
        int characterId,
        string name,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > 64)
        {
            return new GuildCreateResult(
                GuildCreateOutcome.InvalidName,
                0,
                trimmed);
        }

        await using var connection = await _dataSource.OpenConnectionAsync(
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            cancellationToken);

        await using (var membership = new NpgsqlCommand(
            """
            SELECT 1 FROM public.guild_members WHERE character_id = @characterId;
            """,
            connection,
            transaction))
        {
            membership.Parameters.AddWithValue("characterId", characterId);
            var existing = await membership.ExecuteScalarAsync(cancellationToken);
            if (existing is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new GuildCreateResult(
                    GuildCreateOutcome.AlreadyInGuild,
                    0,
                    trimmed);
            }
        }

        await using (var duplicate = new NpgsqlCommand(
            """
            SELECT 1 FROM public.guilds WHERE lower(name::text) = lower(@name);
            """,
            connection,
            transaction))
        {
            duplicate.Parameters.AddWithValue("name", trimmed);
            var taken = await duplicate.ExecuteScalarAsync(cancellationToken);
            if (taken is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new GuildCreateResult(
                    GuildCreateOutcome.NameTaken,
                    0,
                    trimmed);
            }
        }

        var consumption = await TryConsumeGuildStoneAsync(
            connection,
            transaction,
            accountId,
            characterId,
            trimmed,
            cancellationToken);
        if (consumption is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new GuildCreateResult(
                GuildCreateOutcome.MissingGuildStone,
                0,
                trimmed);
        }

        long guildId;
        await using (var guild = new NpgsqlCommand(
            """
            INSERT INTO public.guilds (
                name, level, member_limit, lord_character_id,
                created_at, updated_at)
            VALUES (
                @name, 1, @memberLimit, @lordCharacterId,
                @now, @now)
            RETURNING id;
            """,
            connection,
            transaction))
        {
            guild.Parameters.AddWithValue("name", trimmed);
            guild.Parameters.AddWithValue("memberLimit", StartingMemberLimit);
            guild.Parameters.AddWithValue("lordCharacterId", characterId);
            guild.Parameters.AddWithValue("now", now);
            guildId = (long)(await guild.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    "The guild insert returned no identity."));
        }

        await using (var member = new NpgsqlCommand(
            """
            INSERT INTO public.guild_members (
                character_id, guild_id, duty, joined_at)
            VALUES (
                @characterId, @guildId, @duty, @now);
            """,
            connection,
            transaction))
        {
            member.Parameters.AddWithValue("characterId", characterId);
            member.Parameters.AddWithValue("guildId", guildId);
            member.Parameters.AddWithValue("duty", LordDuty);
            member.Parameters.AddWithValue("now", now);
            await member.ExecuteNonQueryAsync(cancellationToken);
        }

        // The character carries its own guild state, in both generations of
        // columns: this server's guild_duty, and the pair the original server
        // filled (consortia = the guild it belongs to, consortia_job = its duty).
        // The attribute panel reads the persisted character attribute, not the
        // member list, so all of them are written in this one transaction.
        await using (var duty = new NpgsqlCommand(
            """
            UPDATE public.character_base
            SET guild_duty = @duty,
                consortia_job = @duty,
                consortia = @guildId
            WHERE id = @characterId;
            """,
            connection,
            transaction))
        {
            duty.Parameters.AddWithValue("duty", LordDuty);
            duty.Parameters.AddWithValue("guildId", guildId);
            duty.Parameters.AddWithValue("characterId", characterId);
            if (await duty.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The guild founder's character duty was not stored.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new GuildCreateResult(
            GuildCreateOutcome.Created,
            guildId,
            trimmed,
            consumption.Value.KitBagSlot);
    }
}

