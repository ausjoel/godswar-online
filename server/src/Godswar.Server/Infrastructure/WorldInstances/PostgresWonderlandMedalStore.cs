using System.Data;
using Godswar.Server.Application.WorldInstances;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldInstances;

/// <summary>
/// ECONOMY r10: pays Wonderland island-clear Medals exactly once per (run, island, character).
/// </summary>
/// <remarks>
/// Like server_rates, the table is NOT a registered schema migration: it is created with idempotent DDL on first use,
/// so a binary without this feature still starts against a database this build has used (the migration runner
/// refuses a history it does not know). The award row and the balance credit commit together, so a retry after a
/// crash or a lost reply can never pay twice. wallet_revision is bumped like every other wallet writer.
/// </remarks>
internal sealed class PostgresWonderlandMedalStore(NpgsqlDataSource dataSource) : IWonderlandMedalStore
{
    internal const string SchemaSql =
        """
        CREATE TABLE IF NOT EXISTS public.wonderland_medal_awards (
            world_instance_id uuid NOT NULL,
            island smallint NOT NULL CHECK (island BETWEEN 1 AND 8),
            character_id integer NOT NULL REFERENCES public.character_base(id) ON DELETE CASCADE,
            account_id integer NOT NULL,
            medals integer NOT NULL CHECK (medals >= 0),
            medals_after integer NOT NULL CHECK (medals_after >= 0),
            policy_revision text NOT NULL,
            awarded_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (world_instance_id, island, character_id)
        );
        COMMENT ON TABLE public.wonderland_medal_awards IS
            'ECONOMY r10: Medals paid per Wonderland island clear (IGG guide stage table); one row per run, island and character.';
        """;

    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private int _ensured;
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);

    public async Task<IReadOnlyList<WonderlandMedalAward>> AwardAsync(
        WonderlandTitleRequest request,
        int medalsPerMember,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (medalsPerMember <= 0) return [];
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        if (Volatile.Read(ref _ensured) == 0)
        {
            // One DDL run at a time (concurrent CREATE ... IF NOT EXISTS can deadlock), across processes too.
            await SchemaGate.WaitAsync(cancellationToken);
            try
            {
                if (Volatile.Read(ref _ensured) == 0)
                {
                    await using var schema = await connection.BeginTransactionAsync(cancellationToken);
                    await using (var guard = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key);", connection, schema))
                    {
                        guard.Parameters.AddWithValue("key", 0x57444D45444C); // "WDMEDL"
                        await guard.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await using (var ddl = new NpgsqlCommand(SchemaSql, connection, schema))
                        await ddl.ExecuteNonQueryAsync(cancellationToken);
                    await schema.CommitAsync(cancellationToken);
                    Volatile.Write(ref _ensured, 1);
                }
            }
            finally
            {
                SchemaGate.Release();
            }
        }

        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var awards = new List<WonderlandMedalAward>();
        foreach (var member in request.FrozenMembers.OrderBy(member => member.CharacterId))
        {
            int? existingAfter = null;
            await using (var read = new NpgsqlCommand(
                """
                SELECT medals_after FROM public.wonderland_medal_awards
                WHERE world_instance_id = @instance AND island = @island AND character_id = @character;
                """, connection, transaction))
            {
                read.Parameters.AddWithValue("instance", request.WorldInstanceId.Value);
                read.Parameters.AddWithValue("island", checked((short)request.IslandNumber));
                read.Parameters.AddWithValue("character", member.CharacterId);
                if (await read.ExecuteScalarAsync(cancellationToken) is int after) existingAfter = after;
            }

            if (existingAfter is { } replayed)
            {
                awards.Add(new(member.AccountId, member.CharacterId, medalsPerMember, replayed, false));
                continue;
            }

            int? credited = null;
            await using (var update = new NpgsqlCommand(
                """
                UPDATE public.character_base
                SET exchange_medal = LEAST(COALESCE(exchange_medal, 0)::bigint + @medals, 2147483647)::integer,
                    wallet_revision = wallet_revision + 1
                WHERE id = @character AND account_id = @account AND lifecycle_state = 'active'
                RETURNING exchange_medal;
                """, connection, transaction))
            {
                update.Parameters.AddWithValue("medals", medalsPerMember);
                update.Parameters.AddWithValue("character", member.CharacterId);
                update.Parameters.AddWithValue("account", member.AccountId);
                if (await update.ExecuteScalarAsync(cancellationToken) is int after) credited = after;
            }

            if (credited is not { } medalsAfter)
            {
                // A character deleted since the clear is not paid; nothing is recorded for it.
                continue;
            }

            await using (var insert = new NpgsqlCommand(
                """
                INSERT INTO public.wonderland_medal_awards
                    (world_instance_id, island, character_id, account_id, medals, medals_after, policy_revision)
                VALUES (@instance, @island, @character, @account, @medals, @after, @policy);
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue("instance", request.WorldInstanceId.Value);
                insert.Parameters.AddWithValue("island", checked((short)request.IslandNumber));
                insert.Parameters.AddWithValue("character", member.CharacterId);
                insert.Parameters.AddWithValue("account", member.AccountId);
                insert.Parameters.AddWithValue("medals", medalsPerMember);
                insert.Parameters.AddWithValue("after", medalsAfter);
                insert.Parameters.AddWithValue("policy", WonderlandMedalPolicy.Revision);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            awards.Add(new(member.AccountId, member.CharacterId, medalsPerMember, medalsAfter, true));
        }

        await transaction.CommitAsync(cancellationToken);
        return awards;
    }
}
