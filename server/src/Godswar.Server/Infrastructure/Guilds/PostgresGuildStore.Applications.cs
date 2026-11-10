using Npgsql;

namespace Godswar.Server.Infrastructure.Guilds;

/// <summary>What came of setting a guild's "refuse applications" switch.</summary>
internal enum GuildRefuseOutcome
{
    Updated,
    NotAMember,
    NotPermitted
}

/// <summary>Whether an application could be accepted.</summary>
internal enum GuildJoinOutcome
{
    Joined,

    /// <summary>No character carries that name.</summary>
    ApplicantNotFound,

    /// <summary>The named character already holds a membership.</summary>
    ApplicantAlreadyInGuild,

    /// <summary>The guild disappeared between the application and the answer.</summary>
    GuildMissing,

    /// <summary>The guild is at its member limit.</summary>
    GuildFull
}

/// <summary>What came of accepting an application.</summary>
internal readonly record struct GuildJoinResult(
    GuildJoinOutcome Outcome,
    int CharacterId);

/// <summary>The officers an application has to reach: the lord and the vice-lords.</summary>
internal sealed record GuildOfficer(int CharacterId, string Name, byte Duty);

/// <summary>
/// Everything an application to join needs about the guild it names.
/// </summary>
internal sealed record GuildApplicationContext(
    long GuildId,
    string Name,
    bool RefusesApplications,
    int MemberLimit,
    int MemberCount,
    IReadOnlyList<GuildOfficer> Officers);

/// <summary>
/// The guild window's two application actions: the officers' "refuse
/// applications" switch, and a guildless character's request to join.
/// </summary>
/// <remarks>
/// The switch is per guild and is what the guild-list entry's <c>+0x65</c> byte
/// carries, so every player's list greys the Join cell out while it is set. The
/// application itself carries no roster: the applicant names the guild (the
/// list's own entry key) and the server decides, from the live sessions, whether
/// anybody able to answer is online.
/// </remarks>
internal sealed partial class PostgresGuildStore
{
    /// <summary>
    /// Records a member's "refuse applications" choice.
    /// </summary>
    /// <remarks>
    /// The rule is the one the client's own refusal text states for every other
    /// management action: <c>ERROR_03C2 理事以下职位没有相应权限</c>, so duty 4
    /// (<c>Consortia_Job.ini</c>: 理事) and above may set it.
    /// </remarks>
    public async Task<GuildRefuseOutcome> SetRefuseApplicationsAsync(
        int characterId,
        bool refuse,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(
            cancellationToken);
        byte? duty = null;
        long? guildId = null;
        await using (var read = new NpgsqlCommand(
            """
            SELECT member.guild_id, member.duty
            FROM public.guild_members AS member
            WHERE member.character_id = @characterId
            FOR UPDATE OF member;
            """,
            connection))
        {
            read.Parameters.AddWithValue("characterId", characterId);
            await using var reader = await read.ExecuteReaderAsync(
                cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                guildId = reader.GetInt64(0);
                duty = checked((byte)reader.GetInt16(1));
            }
        }

        if (guildId is not { } guild || duty is not { } memberDuty)
        {
            return GuildRefuseOutcome.NotAMember;
        }

        if (memberDuty < 4)
        {
            return GuildRefuseOutcome.NotPermitted;
        }

        await using (var write = new NpgsqlCommand(
            """
            UPDATE public.guilds
            SET refuse_applications = @refuse,
                updated_at = @now
            WHERE id = @guildId;
            """,
            connection))
        {
            write.Parameters.AddWithValue("refuse", refuse);
            write.Parameters.AddWithValue("now", now);
            write.Parameters.AddWithValue("guildId", guild);
            if (await write.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The guild's refuse-applications switch was not stored.");
            }
        }

        Console.WriteLine(
            $"[guild] refuse applications character={characterId} " +
            $"guild={guild} refuse={refuse} duty={memberDuty}");
        return GuildRefuseOutcome.Updated;
    }

    /// <summary>
    /// The guild an application names, with the officers a live session has to be
    /// found for.
    /// </summary>
    /// <returns>Null when no guild carries that id.</returns>
    public async Task<GuildApplicationContext?> TryReadApplicationContextAsync(
        long guildId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(
            cancellationToken);
        long id;
        string name;
        bool refuses;
        int memberLimit;
        int memberCount;
        await using (var guild = new NpgsqlCommand(
            """
            SELECT
                guild.id,
                guild.name,
                guild.refuse_applications,
                guild.member_limit,
                (
                    SELECT count(*)
                    FROM public.guild_members AS member
                    WHERE member.guild_id = guild.id
                )
            FROM public.guilds AS guild
            WHERE guild.id = @guildId;
            """,
            connection))
        {
            guild.Parameters.AddWithValue("guildId", guildId);
            await using var reader = await guild.ExecuteReaderAsync(
                cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            id = reader.GetInt64(0);
            name = reader.GetString(1);
            refuses = reader.GetBoolean(2);
            memberLimit = reader.GetInt32(3);
            memberCount = checked((int)reader.GetInt64(4));
        }

        var officers = new List<GuildOfficer>();
        await using (var members = new NpgsqlCommand(
            """
            SELECT member.character_id, character.name, member.duty
            FROM public.guild_members AS member
            INNER JOIN public.character_base AS character
                ON character.id = member.character_id
            WHERE member.guild_id = @guildId
              AND member.duty >= 5
            ORDER BY member.duty DESC, member.character_id;
            """,
            connection))
        {
            members.Parameters.AddWithValue("guildId", guildId);
            await using var reader = await members.ExecuteReaderAsync(
                cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                officers.Add(new GuildOfficer(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    checked((byte)reader.GetInt16(2))));
            }
        }

        return new GuildApplicationContext(
            id,
            name,
            refuses,
            memberLimit,
            memberCount,
            officers);
    }

    /// <summary>
    /// Whether a character already belongs to a guild, which the one-guild rule
    /// (<c>Consortia.dat HadConsortia</c>) refuses an application for.
    /// </summary>
    public async Task<bool> IsCharacterInGuildAsync(
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM public.guild_members AS member
                WHERE member.character_id = @characterId
            );
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken)
            ?? false);
    }

    /// <summary>
    /// Seats an applicant in a guild: the membership row and the character's own
    /// guild state, in one transaction.
    /// </summary>
    /// <remarks>
    /// The officer answers with the applicant's <em>name</em> (the client's
    /// <c>10145</c> carries a 64-byte name at <c>body+4</c>), so the name is what
    /// resolves the character here. A member who joined by application starts at
    /// the lowest rank, <c>Consortia_Job.ini</c>'s 1 (见习会员); the guild's own
    /// lord keeps 6 from creation. Both sides move through
    /// <c>WriteCharacterGuildAsync</c>, so the membership row and
    /// <c>character_base</c> cannot disagree.
    /// </remarks>
    public async Task<GuildJoinResult> TryAddMemberAsync(
        long guildId,
        string applicantName,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            cancellationToken);

        int? applicantId = null;
        await using (var applicant = new NpgsqlCommand(
            """
            SELECT character.id
            FROM public.character_base AS character
            WHERE lower(character.name) = lower(@name)
              AND character.lifecycle_state = 'active'
            ORDER BY character.id
            LIMIT 1;
            """,
            connection,
            transaction))
        {
            applicant.Parameters.AddWithValue("name", applicantName);
            var value = await applicant.ExecuteScalarAsync(cancellationToken);
            if (value is int id)
            {
                applicantId = id;
            }
        }

        if (applicantId is not { } characterId)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new GuildJoinResult(GuildJoinOutcome.ApplicantNotFound, 0);
        }

        await using (var membership = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM public.guild_members AS member
                WHERE member.character_id = @characterId
            );
            """,
            connection,
            transaction))
        {
            membership.Parameters.AddWithValue("characterId", characterId);
            if ((bool)(await membership.ExecuteScalarAsync(cancellationToken)
                    ?? false))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new GuildJoinResult(
                    GuildJoinOutcome.ApplicantAlreadyInGuild,
                    characterId);
            }
        }

        int memberLimit;
        int memberCount;
        await using (var guild = new NpgsqlCommand(
            """
            SELECT
                guild.member_limit,
                (
                    SELECT count(*)
                    FROM public.guild_members AS member
                    WHERE member.guild_id = guild.id
                )
            FROM public.guilds AS guild
            WHERE guild.id = @guildId
            FOR UPDATE OF guild;
            """,
            connection,
            transaction))
        {
            guild.Parameters.AddWithValue("guildId", guildId);
            await using var reader = await guild.ExecuteReaderAsync(
                cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new GuildJoinResult(
                    GuildJoinOutcome.GuildMissing,
                    characterId);
            }

            memberLimit = reader.GetInt32(0);
            memberCount = checked((int)reader.GetInt64(1));
        }

        if (memberCount >= memberLimit)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new GuildJoinResult(GuildJoinOutcome.GuildFull, characterId);
        }

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO public.guild_members (
                character_id, guild_id, duty, joined_at)
            VALUES (
                @characterId, @guildId, @duty, @now);
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("characterId", characterId);
            insert.Parameters.AddWithValue("guildId", guildId);
            insert.Parameters.AddWithValue("duty", MemberDuty);
            insert.Parameters.AddWithValue("now", now);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The guild membership was not stored.");
            }
        }

        await WriteCharacterGuildAsync(
            connection,
            transaction,
            characterId,
            MemberDuty,
            guildId,
            contribution: null,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        Console.WriteLine(
            $"[guild] member joined guild={guildId} " +
            $"character={applicantName} id={characterId} duty={MemberDuty}");
        return new GuildJoinResult(GuildJoinOutcome.Joined, characterId);
    }

    /// <summary>The duty a member who joins by application starts at.</summary>
    private const short MemberDuty = 1;
}
