using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    public MonsterRuntimeTick AdvanceMonsters(
        DateTimeOffset now,
        Func<ClientSession, long?>? lifeRevisionResolver = null)
    {
        return AdvanceMonsters(now,
            CaptureMonsterCombatTargets(Snapshot(), lifeRevisionResolver),
            lifeRevisionResolver);
    }

    public MonsterRuntimeTick AdvanceMonsters(
        DateTimeOffset now,
        IReadOnlyList<MonsterCombatTarget> combatTargets,
        Func<ClientSession, long?>? lifeRevisionResolver = null)
    {
        // Projection can wait on player vitals, so callers prepare it outside
        // the owner. Revalidate membership and life here without taking any
        // player lock; final hit commits still validate their exact authority.
        var members = Snapshot().Where(static context => context.WorldReady)
            .ToLookup(static context => context.CharacterId);
        var currentTargets = combatTargets.Where(target =>
            members[target.CharacterId].Any(context =>
                context.ObjectId == target.ObjectId &&
                context.Ownership == target.Ownership &&
                context.WorldInstanceId == target.WorldInstanceId &&
                context.WorldRevision == target.WorldRevision &&
                context.WorldMembershipEpoch == target.WorldMembershipEpoch &&
                (lifeRevisionResolver is null ||
                 lifeRevisionResolver(context.Session) == target.LifeRevision)))
            .ToArray();

        lock (_monsterRuntimeGate)
        {
            return _monsterRuntime?.Advance(now, currentTargets) ??
                new MonsterRuntimeTick(false, []);
        }
    }

    internal static IReadOnlyList<MonsterCombatTarget> CaptureMonsterCombatTargets(
        IReadOnlyList<GameSessionContext> members,
        Func<ClientSession, long?>? lifeRevisionResolver = null,
        Action? projectionStarting = null)
    {
        if (SingleOwnerMailboxExecutionContext.Current is not null)
        {
            throw new InvalidOperationException(
                "Monster target vitals must be projected outside the world owner.");
        }

        var targets = new List<MonsterCombatTarget>(members.Count);
        foreach (var context in members)
        {
            if (!context.WorldReady)
            {
                continue;
            }

            projectionStarting?.Invoke();
            lock (context.Character.VitalsSync)
            {
                var lifeRevision = lifeRevisionResolver is null
                    ? 0
                    : lifeRevisionResolver(context.Session);
                if (lifeRevision is null)
                {
                    continue;
                }

                targets.Add(new MonsterCombatTarget(
                    context.CharacterId,
                    context.Character.PositionX,
                    context.Character.PositionZ,
                    context.Character.CurrentHp > 0,
                    context.ObjectId,
                    lifeRevision.Value,
                    context.Ownership,
                    context.WorldInstanceId,
                    context.WorldRevision,
                    context.WorldMembershipEpoch,
                    context.Character.Camp)); // PORTALS: faction-aware aggro
            }
        }

        return targets.ToArray();
    }
}
