using Npgsql;

namespace Godswar.Server.Infrastructure.Guilds;

/// <summary>What came of an offering to an altar.</summary>
internal enum GuildWorshipOutcome
{
    Accepted,

    /// <summary>The character does not hold enough guild contribution for the offering.</summary>
    InsufficientContribution,

    /// <summary>The character holds no membership, so it has no altar to offer at.</summary>
    NotAMember
}

/// <summary>What one offering did, and the balances it left behind.</summary>
internal sealed record GuildWorshipResult(
    GuildWorshipOutcome Outcome,
    int Contribution,
    long Points);

/// <summary>
/// The altar's offering: a member's guild contribution turned into offering points
/// on one altar.
/// </summary>
/// <remarks>
/// The client's own text fixes both numbers: <c>NF_L0_GH68</c> says one offering
/// point costs one point of guild contribution, and <c>NF_L0_GH1010</c> names the
/// refusal when the contribution is short ("你的贡献點不够，所以供奉失败。").
/// Contribution is spent and points are written in one transaction, so a refused
/// offering leaves both untouched, and a repeated offering adds to the balance the
/// member already holds on that altar.
/// </remarks>
internal sealed partial class PostgresGuildStore
{
    public async Task<GuildWorshipResult> TryWorshipAsync(
        int characterId,
        int buildingType,
        int points,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (points <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(points),
                "An offering has to be a positive number of points.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            cancellationToken);

        int contribution;
        long? guildId;
        await using (var read = new NpgsqlCommand(
            """
            SELECT c.consortia_contribute, m.guild_id
            FROM public.character_base AS c
            LEFT JOIN public.guild_members AS m
                ON m.character_id = c.id
            WHERE c.id = @characterId
            FOR UPDATE OF c;
            """,
            connection,
            transaction))
        {
            read.Parameters.AddWithValue("characterId", characterId);
            await using var reader = await read.ExecuteReaderAsync(
                cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException(
                    "The offering character does not exist.");
            }

            contribution = reader.GetInt32(0);
            guildId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        }

        if (guildId is not { } guild)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new GuildWorshipResult(
                GuildWorshipOutcome.NotAMember,
                contribution,
                0);
        }

        if (contribution < points)
        {
            // Nothing is written: a refused offering leaves the contribution and
            // the altar's balance as they were.
            await transaction.RollbackAsync(cancellationToken);
            return new GuildWorshipResult(
                GuildWorshipOutcome.InsufficientContribution,
                contribution,
                0);
        }

        await using (var spend = new NpgsqlCommand(
            """
            UPDATE public.character_base
            SET consortia_contribute = consortia_contribute - @points
            WHERE id = @characterId;
            """,
            connection,
            transaction))
        {
            spend.Parameters.AddWithValue("points", points);
            spend.Parameters.AddWithValue("characterId", characterId);
            if (await spend.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The offering character's contribution was not written.");
            }
        }

        // The new row's settled_at is the offering's own moment, so a fresh altar
        // owes no drain. An offering that adds to an existing balance leaves that
        // row's settled_at alone: the hours already owed are still owed, and moving
        // the mark would let a player reset the drain by topping the altar up.
        long balance;
        await using (var offering = new NpgsqlCommand(
            """
            INSERT INTO public.guild_altar_worship (
                guild_id, character_id, building_type, points, updated_at,
                settled_at)
            VALUES (
                @guildId, @characterId, @buildingType, @points, @now, @now)
            ON CONFLICT (guild_id, character_id, building_type)
            DO UPDATE SET
                points = public.guild_altar_worship.points + EXCLUDED.points,
                updated_at = EXCLUDED.updated_at
            RETURNING points;
            """,
            connection,
            transaction))
        {
            offering.Parameters.AddWithValue("guildId", guild);
            offering.Parameters.AddWithValue("characterId", characterId);
            offering.Parameters.AddWithValue("buildingType", buildingType);
            offering.Parameters.AddWithValue("points", points);
            offering.Parameters.AddWithValue("now", now);
            balance = (int)(await offering.ExecuteScalarAsync(
                cancellationToken)
                ?? throw new InvalidDataException(
                    "The offering points were not written."));
        }

        await transaction.CommitAsync(cancellationToken);
        Console.WriteLine(
            $"[guild] offering character={characterId} guild={guild} " +
            $"building={buildingType} points={points} balance={balance} " +
            $"contribution={contribution - points}");
        return new GuildWorshipResult(
            GuildWorshipOutcome.Accepted,
            contribution - points,
            balance);
    }
}
