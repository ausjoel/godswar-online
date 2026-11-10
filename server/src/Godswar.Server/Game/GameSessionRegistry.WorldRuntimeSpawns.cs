using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

/// <summary>A staged runtime spawn bound to the world instance the session was on when it was staged.</summary>
internal sealed record WorldRuntimeSpawnTicket(
    WorldInstanceRuntime Instance,
    WorldRuntimeSpawnStage Stage);

/// <summary>ROUND3 summons: a staged group of runtime spawns, bound to one world instance.</summary>
internal sealed record WorldRuntimeSpawnGroupTicket(
    WorldInstanceRuntime Instance,
    IReadOnlyList<WorldRuntimeSpawnStage> Stages);

// ROUND3 runtime spawns (2026-11-09): the session-facing side of the world-map runtime spawn service.
// Usage (see Development/runtime-spawns/README.md): stage -> do the paid part (consume the item) -> attach;
// remove if the paid part fails after attaching. Kills go through the ordinary combat, EXP and corpse-loot
// paths; PrepareMonsterLoot swaps the template's loot table for the spawn's own drops.
internal sealed partial class GameSessionRegistry
{
    internal bool TryStageWorldRuntimeSpawn(ClientSession session, WorldRuntimeSpawnBlueprint blueprint,
        DateTimeOffset now, out WorldRuntimeSpawnTicket ticket, out string refusal)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(blueprint);
        ticket = null!;
        if (!_sessions.TryGetValue(session, out var context) || !context.WorldReady ||
            !TryGetWorldInstance(context, out var runtime))
        {
            refusal = "not in the world";
            return false;
        }

        var staged = InvokeWorldOwner(runtime, map =>
        {
            var ok = map.TryStageWorldRuntimeSpawn(blueprint, now, out var stage, out var reason);
            return (Ok: ok, Stage: stage, Reason: reason);
        });
        refusal = staged.Reason;
        if (!staged.Ok)
        {
            return false;
        }
        ticket = new WorldRuntimeSpawnTicket(runtime, staged.Stage);
        return true;
    }

    internal bool TryAttachWorldRuntimeSpawn(ClientSession session, WorldRuntimeSpawnTicket ticket,
        DateTimeOffset now, out string refusal)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (!_sessions.TryGetValue(session, out var context) || !context.WorldReady ||
            !TryGetWorldInstance(context, out var runtime) || !ReferenceEquals(runtime, ticket.Instance))
        {
            refusal = "left the map before the spawn";
            return false;
        }
        var attached = InvokeWorldOwner(runtime, map =>
        {
            var ok = map.TryAttachWorldRuntimeSpawn(ticket.Stage, now, out var reason);
            return (Ok: ok, Reason: reason);
        });
        refusal = attached.Reason;
        return attached.Ok;
    }

    internal void RemoveWorldRuntimeSpawn(WorldRuntimeSpawnTicket ticket, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        InvokeWorldOwner(ticket.Instance, map => map.RemoveWorldRuntimeSpawn(ticket.Stage.Definition.ObjectId, now));
    }

    /// <summary>W2 F-27 TROY (slice 2): a runtime spawn's presence on the asking session's world instance.</summary>
    internal WorldRuntimeSpawnPresence GetWorldRuntimeSpawnPresence(ClientSession session, uint objectId,
        Guid runtimeInstanceId)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!_sessions.TryGetValue(session, out var context) || !context.WorldReady ||
            !TryGetWorldInstance(context, out var runtime))
        {
            return WorldRuntimeSpawnPresence.Unknown;
        }
        return InvokeWorldOwner(runtime, map => map.GetWorldRuntimeSpawnPresence(objectId, runtimeInstanceId));
    }

    // ROUND3 summons (2026-11-09): groups (treasure-dig boss sets, Totem Pole soldiers), all or nothing.
    internal bool TryStageWorldRuntimeSpawnGroup(ClientSession session,
        IReadOnlyList<WorldRuntimeSpawnBlueprint> blueprints, DateTimeOffset now,
        out WorldRuntimeSpawnGroupTicket ticket, out string refusal)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(blueprints);
        ticket = null!;
        if (!_sessions.TryGetValue(session, out var context) || !context.WorldReady ||
            !TryGetWorldInstance(context, out var runtime))
        {
            refusal = "not in the world";
            return false;
        }

        var staged = InvokeWorldOwner(runtime, map =>
        {
            var ok = map.TryStageWorldRuntimeSpawnGroup(blueprints, now, out var stages, out var reason);
            return (Ok: ok, Stages: stages, Reason: reason);
        });
        refusal = staged.Reason;
        if (!staged.Ok)
        {
            return false;
        }
        ticket = new WorldRuntimeSpawnGroupTicket(runtime, staged.Stages);
        return true;
    }

    internal bool TryAttachWorldRuntimeSpawnGroup(ClientSession session, WorldRuntimeSpawnGroupTicket ticket,
        DateTimeOffset now, out string refusal)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (!_sessions.TryGetValue(session, out var context) || !context.WorldReady ||
            !TryGetWorldInstance(context, out var runtime) || !ReferenceEquals(runtime, ticket.Instance))
        {
            refusal = "left the map before the spawn";
            return false;
        }
        var attached = InvokeWorldOwner(runtime, map =>
        {
            var ok = map.TryAttachWorldRuntimeSpawnGroup(ticket.Stages, now, out var reason);
            return (Ok: ok, Reason: reason);
        });
        refusal = attached.Reason;
        return attached.Ok;
    }

    internal void RemoveWorldRuntimeSpawnGroup(WorldRuntimeSpawnGroupTicket ticket, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        InvokeWorldOwner(ticket.Instance, map =>
        {
            foreach (var stage in ticket.Stages)
            {
                map.RemoveWorldRuntimeSpawn(stage.Definition.ObjectId, now);
            }
            return true;
        });
    }

    /// <summary>
    /// ROUND3 summons: living enemy players near the session's character that it could lawfully attack right now
    /// (the basic-attack PvP gate: faction PvP map, safe zones, peace time). A Totem Pole needs one, so its
    /// summons are never sent where PvP is off.
    /// </summary>
    internal int CountAttackableEnemiesNear(ClientSession session, float radius, DateTimeOffset now)
    {
        if (!_sessions.TryGetValue(session, out var context) || !context.WorldReady ||
            !TryGetWorldInstance(context, out var runtime))
        {
            return 0;
        }
        var owner = context.Character;
        var enemies = InvokeWorldOwner(runtime, map =>
            map.EnemyPlayersNear(owner.Camp, owner.PositionX, owner.PositionZ, radius));
        return enemies.Count(enemy => EvaluatePvpBasicAttack(owner, enemy, now).Allowed);
    }

    /// <summary>
    /// ROUND3 summons, the admin F-150: the source of a killed runtime spawn that pays no kill EXP (Totem Pole soldiers,
    /// treasure guardians), or null when the monster is not a runtime spawn or keeps its EXP.
    /// </summary>
    internal string? ResolveNoExperienceRuntimeSpawnSource(ClientSession session, MonsterDamageResult damage)
    {
        if (!WorldRuntimeSpawnOverlay.IsRuntimeSpawnObjectId(damage.ObjectId) ||
            !_sessions.TryGetValue(session, out var context) || !TryGetWorldInstance(context, out var runtime))
        {
            return null;
        }
        return InvokeWorldOwner(runtime, map =>
            map.TryGetWorldRuntimeSpawn(damage.ObjectId, damage.Monster.RuntimeInstanceId, out var spec) &&
            !spec.PaysKillExperience
                ? spec.Source
                : null);
    }

    /// <summary>The drops of a killed runtime spawn, or null when the monster is not one.</summary>
    private IReadOnlyList<WorldRuntimeSpawnDrop>? ResolveWorldRuntimeSpawnDrops(
        WorldInstanceRuntime runtime, MonsterDamageResult damage)
    {
        if (!WorldRuntimeSpawnOverlay.IsRuntimeSpawnObjectId(damage.ObjectId))
        {
            return null;
        }
        return InvokeWorldOwner(runtime, map =>
            map.TryGetWorldRuntimeSpawn(damage.ObjectId, damage.Monster.RuntimeInstanceId, out var spec)
                ? spec.Drops
                : null);
    }
}
