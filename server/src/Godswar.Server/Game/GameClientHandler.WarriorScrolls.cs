using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Infrastructure.EventsNpc;

namespace Godswar.Server.Game;

/// <summary>
/// WARRIOR scrolls (the admin F-146 option b): using a "Warriors' Training Ground" quest scroll (4838-4851) arms a hidden
/// "kill 1 Magma Scorpion" task (WarriorTrainingScrollPolicy). Behind the Warriors' Training Ground switch
/// (instance.warrior_training_entry): while it is 0 the scrolls behave exactly as before. No pop-up after the item
/// is used (standing rule): info lines only. The task is completed by the registry when a Magma Scorpion dies in a
/// Warriors' run and paid here, from the 20-second reward loop.
/// </summary>
internal sealed partial class GameClientHandler
{
    private bool _warriorScrollLoginChecked;

    private async Task<bool> TryUseWarriorScrollAsync(int itemId, CancellationToken cancellationToken)
    {
        if (_character is not { } character || !WarriorTrainingScrollPolicy.TryResolve(itemId, out var scroll) ||
            !InstanceEntrySwitches.IsWarriorTrainingOpen)
        {
            return false;
        }

        if (scroll.Camp != character.Camp)
        {
            await SendKitBagRefreshAsync(cancellationToken);
            await SendInfoLineAsync(WarriorTrainingScrollPolicy.WrongCampLine, InfoTabOther, cancellationToken);
            return true;
        }

        // 0 = armed, 1 = a task is already open, 2 = the scroll is not in the bag (or the store is down).
        var outcome = await RunEventsNpcAsync(async tx =>
        {
            if (await tx.ScalarAsync(
                    "SELECT 1 FROM public.warrior_scroll_tasks WHERE character_id = @c AND completed_at IS NULL;") is not null)
            {
                tx.Rollback();
                return 1;
            }
            if (!await tx.ConsumeAsync(itemId, 1)) { tx.Rollback(); return 2; }
            await tx.ScalarAsync(
                "INSERT INTO public.warrior_scroll_tasks (character_id, item_id, experience) VALUES (@c, @item, @exp);",
                ("item", itemId), ("exp", scroll.Experience));
            return 0;
        }, 2, cancellationToken);
        Console.WriteLine($"[warrior-scroll] use character={character.Name} item={itemId} band={scroll.BandLow}-" +
                          $"{scroll.BandHigh} exp={scroll.Experience} outcome={outcome}");
        if (outcome != 0)
        {
            await SendKitBagRefreshAsync(cancellationToken);
            if (outcome == 1)
                await SendInfoLineAsync(WarriorTrainingScrollPolicy.AlreadyArmedLine, InfoTabOther, cancellationToken);
            return true;
        }

        await RefreshAfterEventsNpcAsync([], cancellationToken); // no acquisition notice for a used item
        await SendInfoLineAsync(WarriorTrainingScrollPolicy.ArmedLine, InfoTabOther, cancellationToken);
        await SendInfoLineAsync(WarriorTrainingScrollPolicy.RewardLine(scroll), InfoTabOther, cancellationToken);
        return true;
    }

    /// <summary>
    /// Pays completed tasks: once after world entry (a kill while this handler was not the one told, or a restart),
    /// then only when the registry reports a completion. Each row is claimed (paid_at) before it is paid, so it is
    /// paid at most once; an interrupted payment is logged, not retried (as the Training Ground rewards).
    /// </summary>
    private async Task PayWarriorScrollTasksAsync(CancellationToken cancellationToken)
    {
        if (_character is not { } character || EventsNpcRuntime.Store is not { } store) return;
        var due = _registry.TakeWarriorScrollPayDue(character.Id);
        if (!due && _warriorScrollLoginChecked) return;
        _warriorScrollLoginChecked = true;
        for (var row = 0; row < 16; row++)
        {
            string? claimed;
            try
            {
                claimed = await store.ExecuteScalarAsync("""
                    WITH next AS (
                        SELECT id FROM public.warrior_scroll_tasks
                         WHERE character_id = @c AND completed_at IS NOT NULL AND paid_at IS NULL
                         ORDER BY id LIMIT 1 FOR UPDATE SKIP LOCKED)
                    UPDATE public.warrior_scroll_tasks t SET paid_at = now() FROM next WHERE t.id = next.id
                    RETURNING t.id::text || ',' || t.item_id::text || ',' || t.experience::text;
                    """, cancellationToken, ("c", character.Id)) as string;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[warrior-scroll] claim failed character={character.Name}: {error.Message}");
                return;
            }
            if (claimed is null) return;
            var parts = claimed.Split(',');
            var experience = int.Parse(parts[2]);
            try
            {
                await PayZeusGiftProgressionAsync(experience, 0, "warrior-scroll", cancellationToken);
                await SendInfoLineAsync(WarriorTrainingScrollPolicy.CompletedLine(experience), InfoTabOther,
                    cancellationToken);
                Console.WriteLine($"[warrior-scroll] paid character={character.Name} task={parts[0]} " +
                                  $"item={parts[1]} exp={experience}");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"[warrior-scroll] payment interrupted, NOT retried (claimed) " +
                                        $"character={character.Name} task={parts[0]} exp={experience}: {error.Message}");
                if (error is OperationCanceledException) throw;
            }
        }
    }
}
