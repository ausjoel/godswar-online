using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Godswar.Server.Infrastructure.Characters;

/// <summary>
/// CLASSCHANGE review fix (blocker): the durable talent command's operation id is a fixed
/// hash of (account, character, talent, expected rank) and command_inbox keeps every row
/// for ever, so after a class change resets a talent to rank 0 the rebuy of rank 1 would be
/// answered as a duplicate of the first purchase. The class-change epoch of a character is
/// the number of its Class Shifter receipts; a talent command is scoped to it.
/// Epoch 0 (every character that never shifted, and every server where the class change
/// was never used) leaves the operation id byte-identical to before.
/// </summary>
internal static class ClassChangeTalentEpoch
{
    private static int _receiptsTableSeen;

    /// <summary>
    /// The character's class-change epoch. Call it inside the transaction that holds the
    /// character_base row lock: the class change takes the same lock, so the value cannot
    /// move under the caller. 0 when the receipts table does not exist (feature never used).
    /// </summary>
    public static async Task<long> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _receiptsTableSeen) == 0)
        {
            // Present AND readable by this role. A table created by another role (e.g. migration.sql
            // run as postgres without its ownership step) must not break every talent purchase:
            // then the epoch stays 0, the behaviour before this build.
            await using var probe = new NpgsqlCommand(
                "SELECT COALESCE(has_table_privilege(" +
                "to_regclass('public.character_class_change_receipts'), 'SELECT'), false);",
                connection,
                transaction)
            {
                CommandTimeout = commandTimeoutSeconds
            };
            if (await probe.ExecuteScalarAsync(cancellationToken) is not true)
            {
                return 0;
            }

            Volatile.Write(ref _receiptsTableSeen, 1);
        }

        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM public.character_class_change_receipts WHERE character_id = @id;",
            connection,
            transaction)
        {
            CommandTimeout = commandTimeoutSeconds
        };
        count.Parameters.AddWithValue("id", characterId);
        var epoch = await count.ExecuteScalarAsync(cancellationToken) is long shifts ? shifts : 0;
        // FINALISE2 (F-54): a Zeus' Baptism reset also sets talents back to rank 0, so it advances the epoch too.
        // A character that never reset adds 0, so its operation ids stay byte-identical.
        await using (var probe = new NpgsqlCommand(
            "SELECT COALESCE(has_table_privilege(to_regclass('public.finalise2_talent_reset_receipts'), 'SELECT'), false);",
            connection,
            transaction)
        {
            CommandTimeout = commandTimeoutSeconds
        })
        {
            if (await probe.ExecuteScalarAsync(cancellationToken) is not true)
            {
                return epoch;
            }
        }

        await using var resets = new NpgsqlCommand(
            "SELECT count(*) FROM public.finalise2_talent_reset_receipts WHERE character_id = @id;",
            connection,
            transaction)
        {
            CommandTimeout = commandTimeoutSeconds
        };
        resets.Parameters.AddWithValue("id", characterId);
        return epoch + (await resets.ExecuteScalarAsync(cancellationToken) is long reset ? reset : 0);
    }

    /// <summary>
    /// The operation id scoped to <paramref name="epoch"/>: unchanged at 0, otherwise
    /// SHA-256("class-epoch:" + epoch + ":" + operation id), still 32 bytes.
    /// </summary>
    public static byte[] Scope(byte[] operationId, long epoch)
    {
        ArgumentNullException.ThrowIfNull(operationId);
        if (epoch <= 0)
        {
            return operationId;
        }

        var prefix = Encoding.ASCII.GetBytes("class-epoch:");
        var buffer = new byte[prefix.Length + sizeof(long) + 1 + operationId.Length];
        prefix.CopyTo(buffer, 0);
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(prefix.Length, sizeof(long)), epoch);
        buffer[prefix.Length + sizeof(long)] = (byte)':';
        operationId.CopyTo(buffer, prefix.Length + sizeof(long) + 1);
        return SHA256.HashData(buffer);
    }
}
