using Godswar.Server.Application.Operators;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Operators;

// ROUND-QUESTRESET: quest history edits for the editor (issue #18). One transaction under the character row
// lock: read the camp and every quest row, plan from the pinned quest source, refuse a plan that differs
// from the one the caller previewed or that contradicts a carried quest, then write only quest rows.
// Completing grants nothing: no EXP, TP, Silver, Gold or items, so repeating it cannot duplicate rewards.
internal sealed partial class PostgresOperatorStore
{
    public async Task<OperatorItemResult> ApplyQuestChainAsync(
        int characterId,
        QuestChainOperation operation,
        int level,
        QuestChainScope scope,
        IReadOnlyList<uint> expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await LockCharacterAsync(connection, transaction, characterId, cancellationToken))
        {
            return new(OperatorItemOutcome.CharacterNotFound, "character not found");
        }

        byte camp;
        await using (var command = new NpgsqlCommand(
            "SELECT camp FROM public.character_base WHERE id = @id;", connection, transaction))
        {
            command.Parameters.AddWithValue("id", characterId);
            camp = Convert.ToByte(await command.ExecuteScalarAsync(cancellationToken));
        }

        var completed = new List<uint>();
        var carried = new List<uint>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT quest_id, state FROM public.character_quests
            WHERE character_id = @id
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", characterId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var questId = checked((uint)reader.GetInt32(0));
                (reader.GetInt16(1) == CharacterQuestStatus.Completed ? completed : carried).Add(questId);
            }
        }

        var plan = QuestChainPlanner.Plan(
            operation,
            GameClientHandler.ChainCampFor(camp),
            level,
            scope,
            completed,
            carried);
        if (!plan.Changed.SequenceEqual(expected.Order()))
        {
            return new(OperatorItemOutcome.Refused,
                "refused: the quest history changed since the preview (now " + plan.Changed.Count +
                " quests); preview again");
        }

        if (plan.CarriedConflicts.Count > 0)
        {
            return new(OperatorItemOutcome.Refused,
                "refused: the quest log holds " + string.Join(",", plan.CarriedConflicts) +
                (operation == QuestChainOperation.CompleteMainLine
                    ? ", which this would complete"
                    : ", whose prerequisite this would forget") +
                "; drop those quests first");
        }

        if (plan.Changed.Count == 0)
        {
            return new(OperatorItemOutcome.Done, "nothing to change");
        }

        var ids = plan.Changed.Select(static id => checked((int)id)).ToArray();
        await using (var command = operation == QuestChainOperation.CompleteMainLine
            ? new NpgsqlCommand(
                """
                INSERT INTO public.character_quests (character_id, quest_id, state, progress)
                SELECT @id, quest_id, @completed, 0 FROM unnest(@ids) AS incoming(quest_id)
                ON CONFLICT (character_id, quest_id) DO NOTHING;
                """, connection, transaction)
            : new NpgsqlCommand(
                """
                DELETE FROM public.character_quests
                WHERE character_id = @id AND state = @completed AND quest_id = ANY(@ids);
                """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", characterId);
            command.Parameters.AddWithValue("ids", ids);
            command.Parameters.AddWithValue("completed", CharacterQuestStatus.Completed);
            var written = await command.ExecuteNonQueryAsync(cancellationToken);
            if (written != ids.Length)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(OperatorItemOutcome.Refused,
                    "refused: wrote " + written + " of " + ids.Length + " quest rows; nothing changed");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new(OperatorItemOutcome.Done,
            (operation == QuestChainOperation.CompleteMainLine
                ? "completed " + ids.Length + " main-line quests (no rewards granted)"
                : "forgot " + ids.Length + " completed quests") +
            " ids=" + string.Join(",", ids),
            ids.Length);
    }
}
