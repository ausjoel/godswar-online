using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Npgsql;

namespace Godswar.Server.Infrastructure.Relations;

internal enum RelationAddResult
{
    Added,
    AlreadyListed,
    ListFull,
    OtherListFull,
    Unavailable
}

// Friend and blacklist rows. Like server_rates and the NPC overrides this is NOT a
// registered schema migration: idempotent DDL at start-up, so a build without the
// feature can still start against a database this build has used. Rows point at a
// character id, so a name reused after a deletion does not inherit the old rows.
internal static class RelationStore
{
    private const string SchemaSql =
        """
        CREATE TABLE IF NOT EXISTS public.character_relations (
            owner_character_id integer NOT NULL REFERENCES public.character_base (id) ON DELETE CASCADE,
            target_character_id integer NOT NULL REFERENCES public.character_base (id) ON DELETE CASCADE,
            kind smallint NOT NULL CHECK (kind IN (0, 1)),
            created_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (owner_character_id, target_character_id),
            CHECK (owner_character_id <> target_character_id)
        );
        CREATE INDEX IF NOT EXISTS ix_character_relations_target
            ON public.character_relations (target_character_id, kind);
        """;

    private static NpgsqlDataSource? _dataSource;

    public static async Task InitializeAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _dataSource ??= NpgsqlDataSource.Create(connectionString);
            await using var connection =
                await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var ddl = new NpgsqlCommand(SchemaSql, connection);
            await ddl.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[relations] schema setup failed: {error.Message}");
        }
    }

    private static NpgsqlDataSource Source =>
        _dataSource ?? throw new InvalidOperationException(
            "The relation store has not been initialized.");

    public static async Task<IReadOnlyList<(byte Kind, RelationEntry Entry)>> ListAsync(
        int ownerCharacterId,
        CancellationToken cancellationToken)
    {
        var rows = new List<(byte, RelationEntry)>();
        await using var connection =
            await Source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT r.kind, c.id, c.name, c.camp, c.profession, c.fighter_job_lv
            FROM public.character_relations r
            JOIN public.character_base c ON c.id = r.target_character_id
            WHERE r.owner_character_id = @owner AND c.lifecycle_state = 'active'
            ORDER BY r.kind, lower(c.name)
            """,
            connection);
        command.Parameters.AddWithValue("owner", ownerCharacterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((
                (byte)reader.GetInt16(0),
                ReadEntry(reader, 1)));
        }

        return rows;
    }

    public static async Task<RelationEntry?> FindActiveByNameAsync(
        int realmId,
        string name,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await Source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT id, name, camp, profession, fighter_job_lv
            FROM public.character_base
            WHERE lower(name) = lower(@name)
              AND server_id = @realm
              AND lifecycle_state = 'active'
            """,
            connection);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("realm", realmId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEntry(reader, 0) : null;
    }

    private static RelationEntry ReadEntry(NpgsqlDataReader reader, int first) =>
        new(
            reader.GetInt32(first),
            reader.GetString(first + 1),
            (byte)reader.GetInt16(first + 2),
            (byte)reader.GetInt16(first + 3),
            reader.GetInt32(first + 4),
            false);

    /// <summary>The kind of the row from owner to target, or null when there is none.</summary>
    public static async Task<byte?> GetKindAsync(
        int ownerCharacterId,
        int targetCharacterId,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await Source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT kind FROM public.character_relations
            WHERE owner_character_id = @owner AND target_character_id = @target
            """,
            connection);
        command.Parameters.AddWithValue("owner", ownerCharacterId);
        command.Parameters.AddWithValue("target", targetCharacterId);
        var kind = await command.ExecuteScalarAsync(cancellationToken);
        return kind is short value ? (byte)value : null;
    }

    public static async Task<bool> IsFullAsync(
        int ownerCharacterId,
        byte kind,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await Source.OpenConnectionAsync(cancellationToken);
        return await CountAsync(connection, null, ownerCharacterId, kind, cancellationToken) >=
               RelationProtocol.MaximumPerKind;
    }

    private static async Task<long> CountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int ownerCharacterId,
        byte kind,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM public.character_relations
            WHERE owner_character_id = @owner AND kind = @kind
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("owner", ownerCharacterId);
        command.Parameters.AddWithValue("kind", (short)kind);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task LockOwnerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int ownerCharacterId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext('character_relations'), @owner)",
            connection,
            transaction);
        command.Parameters.AddWithValue("owner", ownerCharacterId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> RowExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int ownerCharacterId,
        int targetCharacterId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT 1 FROM public.character_relations
            WHERE owner_character_id = @owner AND target_character_id = @target
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("owner", ownerCharacterId);
        command.Parameters.AddWithValue("target", targetCharacterId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int ownerCharacterId,
        int targetCharacterId,
        byte kind,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_relations
                (owner_character_id, target_character_id, kind)
            VALUES (@owner, @target, @kind)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("owner", ownerCharacterId);
        command.Parameters.AddWithValue("target", targetCharacterId);
        command.Parameters.AddWithValue("kind", (short)kind);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static Task<RelationAddResult> AddBlacklistAsync(
        int ownerCharacterId,
        int targetCharacterId,
        CancellationToken cancellationToken) =>
        AddOneSidedAsync(
            ownerCharacterId,
            targetCharacterId,
            RelationProtocol.KindBlacklist,
            cancellationToken);

    /// <summary>
    /// FINALISE (the admin 2026-11-05): friends are one-way. Adding a friend writes only
    /// the owner's row; the other character's list changes only when they add the
    /// owner themselves. Removal was already one-sided (<see cref="DeleteAsync"/>).
    /// </summary>
    public static Task<RelationAddResult> AddOneWayFriendAsync(
        int ownerCharacterId,
        int targetCharacterId,
        CancellationToken cancellationToken) =>
        AddOneSidedAsync(
            ownerCharacterId,
            targetCharacterId,
            RelationProtocol.KindFriend,
            cancellationToken);

    private static async Task<RelationAddResult> AddOneSidedAsync(
        int ownerCharacterId,
        int targetCharacterId,
        byte kind,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await Source.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await LockOwnerAsync(connection, transaction, ownerCharacterId, cancellationToken);
        if (await RowExistsAsync(
                connection,
                transaction,
                ownerCharacterId,
                targetCharacterId,
                cancellationToken))
        {
            return RelationAddResult.AlreadyListed;
        }

        if (await CountAsync(
                connection,
                transaction,
                ownerCharacterId,
                kind,
                cancellationToken) >= RelationProtocol.MaximumPerKind)
        {
            return RelationAddResult.ListFull;
        }

        await InsertAsync(
            connection,
            transaction,
            ownerCharacterId,
            targetCharacterId,
            kind,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RelationAddResult.Added;
    }

    /// <summary>
    /// Both rows or neither. The locks are taken in id order, so two characters
    /// accepting each other at once cannot deadlock.
    /// </summary>
    public static async Task<RelationAddResult> AddMutualFriendsAsync(
        int acceptingCharacterId,
        int requestingCharacterId,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await Source.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await LockOwnerAsync(
            connection,
            transaction,
            Math.Min(acceptingCharacterId, requestingCharacterId),
            cancellationToken);
        await LockOwnerAsync(
            connection,
            transaction,
            Math.Max(acceptingCharacterId, requestingCharacterId),
            cancellationToken);
        if (await RowExistsAsync(
                connection,
                transaction,
                acceptingCharacterId,
                requestingCharacterId,
                cancellationToken) ||
            await RowExistsAsync(
                connection,
                transaction,
                requestingCharacterId,
                acceptingCharacterId,
                cancellationToken))
        {
            return RelationAddResult.AlreadyListed;
        }

        if (await CountAsync(
                connection,
                transaction,
                acceptingCharacterId,
                RelationProtocol.KindFriend,
                cancellationToken) >= RelationProtocol.MaximumPerKind)
        {
            return RelationAddResult.ListFull;
        }

        if (await CountAsync(
                connection,
                transaction,
                requestingCharacterId,
                RelationProtocol.KindFriend,
                cancellationToken) >= RelationProtocol.MaximumPerKind)
        {
            return RelationAddResult.OtherListFull;
        }

        await InsertAsync(
            connection,
            transaction,
            acceptingCharacterId,
            requestingCharacterId,
            RelationProtocol.KindFriend,
            cancellationToken);
        await InsertAsync(
            connection,
            transaction,
            requestingCharacterId,
            acceptingCharacterId,
            RelationProtocol.KindFriend,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RelationAddResult.Added;
    }

    public static async Task<bool> DeleteAsync(
        int ownerCharacterId,
        string targetName,
        byte kind,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await Source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            DELETE FROM public.character_relations r
            USING public.character_base c
            WHERE r.target_character_id = c.id
              AND r.owner_character_id = @owner
              AND r.kind = @kind
              AND lower(c.name) = lower(@name)
              AND c.lifecycle_state = 'active'
            """,
            connection);
        command.Parameters.AddWithValue("owner", ownerCharacterId);
        command.Parameters.AddWithValue("kind", (short)kind);
        command.Parameters.AddWithValue("name", targetName);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>The characters with this one in their friend window.</summary>
    public static async Task<IReadOnlyList<int>> ListFriendOwnersAsync(
        int targetCharacterId,
        CancellationToken cancellationToken)
    {
        var owners = new List<int>();
        await using var connection =
            await Source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT owner_character_id FROM public.character_relations
            WHERE target_character_id = @target AND kind = 0
            """,
            connection);
        command.Parameters.AddWithValue("target", targetCharacterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            owners.Add(reader.GetInt32(0));
        }

        return owners;
    }
}
