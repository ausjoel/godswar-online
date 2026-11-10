using System.Collections.Concurrent;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.EventsNpc;

namespace Godswar.Server.Game;

/// <summary>
/// WARRIOR scrolls (F-146 b): a Magma Scorpion killed in a Warriors' Training Ground run completes the armed
/// "kill 1 Magma Scorpion" task (public.warrior_scroll_tasks) of every character in that run (party credit,
/// GENERATED). Completion is one UPDATE; payment is the handler's 20-second reward loop, which this registry nudges
/// through <see cref="TakeWarriorScrollPayDue"/> so idle players cost no query.
/// </summary>
internal sealed partial class GameSessionRegistry
{
    private readonly ConcurrentDictionary<int, byte> _warriorScrollPayDue = new();

    /// <summary>Test seam: the last completion's character ids, written before the database call.</summary>
    internal int[] LastWarriorScrollCandidates { get; private set; } = [];

    internal void CompleteWarriorScrollTasks(WorldInstanceId instanceId, byte mapId, string templateKey)
    {
        if (!WarriorTrainingGroundPolicy.IsMagmaScorpion(templateKey) ||
            !WarriorTrainingGroundPolicy.TryGetByMap(mapId, out _))
        {
            return;
        }

        var characterIds = _sessions.Values
            .Where(context => context.WorldInstanceId == instanceId && context.MapId == mapId &&
                              !context.Session.IsDisconnected)
            .Select(static context => context.CharacterId)
            .Distinct()
            .ToArray();
        LastWarriorScrollCandidates = characterIds;
        if (characterIds.Length == 0 || EventsNpcRuntime.Store is not { } store)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var done = await store.ExecuteScalarAsync("""
                    WITH done AS (
                        UPDATE public.warrior_scroll_tasks
                           SET completed_at = now(), completed_instance = @i
                         WHERE character_id = ANY(@ids) AND completed_at IS NULL
                     RETURNING character_id)
                    SELECT string_agg(character_id::text, ',') FROM done;
                    """, CancellationToken.None, ("ids", characterIds), ("i", instanceId.ToString())) as string;
                if (string.IsNullOrEmpty(done)) return;
                foreach (var id in done.Split(',')) _warriorScrollPayDue[int.Parse(id)] = 1;
                Console.WriteLine($"[warrior-scroll] completed instance={instanceId} characters={done}");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"[warrior-scroll] completion failed instance={instanceId}: {error.Message}");
            }
        });
    }

    /// <summary>True once per completion for this character (the handler then claims and pays its rows).</summary>
    internal bool TakeWarriorScrollPayDue(int characterId) => _warriorScrollPayDue.TryRemove(characterId, out _);
}
