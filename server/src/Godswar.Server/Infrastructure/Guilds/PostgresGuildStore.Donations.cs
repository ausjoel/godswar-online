using Godswar.Server.Application.Guilds;
using Npgsql;

namespace Godswar.Server.Infrastructure.Guilds;

/// <summary>Which purse a guild donation comes out of.</summary>
/// <remarks>
/// The altar's page four offers three donation rows, but the guild holds two
/// accounts (<c>guilds.gold</c> and <c>guilds.silver</c>), and the character
/// carries the matching pair (<c>character_base."Stone"</c> and
/// <c>"Money"</c>). Bound gold has no guild column, so its row is left alone until
/// one exists.
/// </remarks>
internal enum GuildDonationKind
{
    /// <summary>金币: the character's <c>Stone</c> into the guild's <c>gold</c>.</summary>
    Gold,

    /// <summary>银币: the character's <c>Money</c> into the guild's <c>silver</c>.</summary>
    Silver
}

internal enum GuildDonationOutcome
{
    Accepted,
    InsufficientFunds,
    NotAMember
}

/// <summary>What one donation did, and the balances it left behind.</summary>
internal sealed record GuildDonationResult(
    GuildDonationOutcome Outcome,
    int CharacterBalance,
    long GuildBalance,
    long Contribution);

/// <summary>
/// The guild's contribution accounts: a member's donation, and the guild's own
/// balance, moved together.
/// </summary>
/// <remarks>
/// A donation touches four numbers that must agree: the character's purse, the
/// guild's account, the membership's contribution column and the character's own
/// contribution attribute (which the attribute panel reads). All four move inside
/// one transaction, and the wallet's revision is bumped, which is this server's
/// record that a purse changed.
/// </remarks>
internal sealed partial class PostgresGuildStore
{
    public async Task<GuildDonationResult> TryDonateAsync(
        int characterId,
        GuildDonationKind kind,
        int amount,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                "A donation has to be a positive amount.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            cancellationToken);

        var walletColumn = kind == GuildDonationKind.Gold ? "Stone" : "Money";
        var guildColumn = kind == GuildDonationKind.Gold ? "gold" : "silver";
        var contributionColumn = kind == GuildDonationKind.Gold
            ? "contribution_gold"
            : "contribution_silver";

        // The client's own donation texts give the rate: 1 gold is worth 100 guild
        // contribution, 1 silver is worth 1 (LuaText.lua, NF_L0_GH88 and GH89).
        // The purse and the guild account move by the amount; the contribution
        // moves by the rate, which is why the two are not the same number.
        var contribution = checked(
            (long)amount * (kind == GuildDonationKind.Gold ? 100 : 1));

        int balance;
        long? guildId;
        await using (var read = new NpgsqlCommand(
            $"""
            SELECT c."{walletColumn}", m.guild_id
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
                    "The donating character does not exist.");
            }

            balance = reader.GetInt32(0);
            guildId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        }

        if (guildId is not { } guild)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new GuildDonationResult(
                GuildDonationOutcome.NotAMember,
                balance,
                0,
                0);
        }

        if (balance < amount)
        {
            // Nothing is written: a refused donation leaves every number as it was.
            await transaction.RollbackAsync(cancellationToken);
            return new GuildDonationResult(
                GuildDonationOutcome.InsufficientFunds,
                balance,
                0,
                0);
        }

        await using (var purse = new NpgsqlCommand(
            $"""
            UPDATE public.character_base
            SET "{walletColumn}" = "{walletColumn}" - @amount,
                wallet_revision = wallet_revision + 1,
                consortia_contribute = consortia_contribute + @contribution
            WHERE id = @characterId;
            """,
            connection,
            transaction))
        {
            purse.Parameters.AddWithValue("amount", amount);
            purse.Parameters.AddWithValue("contribution", contribution);
            purse.Parameters.AddWithValue("characterId", characterId);
            if (await purse.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The donating character's purse was not written.");
            }
        }

        long guildBalance;
        await using (var account = new NpgsqlCommand(
            $"""
            UPDATE public.guilds
            SET {guildColumn} = {guildColumn} + @amount,
                updated_at = @now
            WHERE id = @guildId
            RETURNING {guildColumn};
            """,
            connection,
            transaction))
        {
            account.Parameters.AddWithValue("amount", amount);
            account.Parameters.AddWithValue("guildId", guild);
            account.Parameters.AddWithValue("now", now);
            guildBalance = (long)(await account.ExecuteScalarAsync(
                cancellationToken)
                ?? throw new InvalidDataException(
                    "The guild account was not written."));
        }

        await using (var member = new NpgsqlCommand(
            $"""
            UPDATE public.guild_members
            SET {contributionColumn} = {contributionColumn} + @contribution
            WHERE character_id = @characterId
            RETURNING {contributionColumn};
            """,
            connection,
            transaction))
        {
            member.Parameters.AddWithValue("contribution", contribution);
            member.Parameters.AddWithValue("characterId", characterId);
            contribution = (long)(await member.ExecuteScalarAsync(
                cancellationToken)
                ?? throw new InvalidDataException(
                    "The member's contribution was not written."));
        }

        await transaction.CommitAsync(cancellationToken);
        Console.WriteLine(
            $"[guild] donation character={characterId} kind={kind} " +
            $"amount={amount} guild={guild} balance={guildBalance} " +
            $"contribution={contribution}");
        return new GuildDonationResult(
            GuildDonationOutcome.Accepted,
            balance - amount,
            guildBalance,
            contribution);
    }
}
