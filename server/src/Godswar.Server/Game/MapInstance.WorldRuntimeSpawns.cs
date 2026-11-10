using Godswar.Server.State;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game;

/// <summary>What a caller asks for: a template at a point, with the spawn's owner, lifetime and drops.</summary>
internal sealed record WorldRuntimeSpawnBlueprint(
    GameplayMonsterTemplateDefinition Template,
    float X,
    float Z,
    float Facing,
    uint Level,
    uint MaximumHealth,
    WorldRuntimeSpawnSpec Spec);

/// <summary>W2 F-27 TROY (slice 2): what <see cref="MapInstance.GetWorldRuntimeSpawnPresence"/> found. Unknown = the
/// asking session is not in the world.</summary>
internal enum WorldRuntimeSpawnPresence { Unknown, Gone, Dead, Alive }

/// <summary>A validated, built, not yet visible runtime spawn (see <see cref="MapInstance.TryAttachWorldRuntimeSpawn"/>).</summary>
internal sealed record WorldRuntimeSpawnStage(
    IMonsterMapRuntime Runtime,
    CapturedMonsterSpawn Definition,
    WorldRuntimeSpawnSpec Spec);

// ROUND3 runtime spawns (2026-11-09): stage / attach / remove on open-world maps. Staging does every check and
// builds the single-monster runtime; attaching is the publication point (the next monster tick shows it).
internal sealed partial class MapInstance
{
    private WorldRuntimeSpawnOverlay? _worldRuntimeSpawns;

    /// <summary>Only open-world maps whose monster runtime is the plain published roster take runtime spawns.</summary>
    private string? WorldRuntimeSpawnRefusal()
    {
        if (InstanceKind != InstanceKind.OpenWorld || DynamicDungeonContentMapPolicy.IsDynamicDungeonMap(MapId))
        {
            return "not an open-world map";
        }
        if (_atlantisMonsters is not null || _wonderlandMonsters is not null ||
            _medusaInstanceOwner is not null || _medusaMonsterAttachment is not null)
        {
            return "map runtime is owned by an instance";
        }
        if (_monsterRuntime is null)
        {
            return "map monsters are not initialised yet";
        }
        if (_worldRuntimeSpawns is not null && !ReferenceEquals(_monsterRuntime, _worldRuntimeSpawns) &&
            !ReferenceEquals(_monsterRuntime, _worldRuntimeSpawns.Base))
        {
            return "map monster runtime was replaced";
        }
        return null;
    }

    internal bool TryStageWorldRuntimeSpawn(WorldRuntimeSpawnBlueprint blueprint, DateTimeOffset now,
        out WorldRuntimeSpawnStage stage, out string refusal)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        var ok = TryStageWorldRuntimeSpawnGroup([blueprint], now, out var stages, out refusal);
        stage = ok ? stages[0] : null!;
        return ok;
    }

    /// <summary>
    /// ROUND3 summons: stages several spawns at once (all or nothing). The map limit and each owner/source-family
    /// limit are checked for the whole group, and every spawn gets its own object id.
    /// </summary>
    internal bool TryStageWorldRuntimeSpawnGroup(IReadOnlyList<WorldRuntimeSpawnBlueprint> blueprints,
        DateTimeOffset now, out IReadOnlyList<WorldRuntimeSpawnStage> stages, out string refusal)
    {
        ArgumentNullException.ThrowIfNull(blueprints);
        stages = [];
        if (blueprints.Count == 0)
        {
            refusal = "empty group";
            return false;
        }
        lock (_monsterRuntimeGate)
        {
            if (WorldRuntimeSpawnRefusal() is { } notHere)
            {
                refusal = notHere;
                return false;
            }

            var overlay = _worldRuntimeSpawns ??= new WorldRuntimeSpawnOverlay(_monsterRuntime!, CampOfCharacter);
            if (overlay.SpawnCount + blueprints.Count > WorldRuntimeSpawnOverlay.MaximumPerMap)
            {
                refusal = "map limit reached";
                return false;
            }
            foreach (var family in blueprints.GroupBy(blueprint =>
                         (blueprint.Spec.OwnerCharacterId, blueprint.Spec.SourceFamily)))
            {
                var limit = family.Min(blueprint => WorldRuntimeSpawnOverlay.OwnerLimitOf(blueprint.Spec));
                if (overlay.CountForOwner(family.Key.OwnerCharacterId, family.Key.SourceFamily) + family.Count() >
                    limit)
                {
                    refusal = "owner limit reached";
                    return false;
                }
            }

            var roster = overlay.Base.Snapshot();
            var taken = roster.Select(static monster => monster.ObjectId)
                .Concat(_npcCatalogDefinitions.Select(static npc => npc.ObjectId))
                .Concat(_sessions.Values.Select(static context => context.ObjectId))
                .ToHashSet();
            var built = new List<WorldRuntimeSpawnStage>(blueprints.Count);
            foreach (var blueprint in blueprints)
            {
                ArgumentNullException.ThrowIfNull(blueprint);
                if (!overlay.TryAllocateObjectId(taken, now, out var objectId))
                {
                    refusal = "no free object id";
                    return false;
                }
                taken.Add(objectId);

                // Height: the client places the mob from packet Y. There is no player Y on the server, so the
                // nearest published monster's height is the estimate (0 on a map with no roster).
                var nearest = roster
                    .OrderBy(monster => (monster.HomeX - blueprint.X) * (monster.HomeX - blueprint.X) +
                        (monster.HomeZ - blueprint.Z) * (monster.HomeZ - blueprint.Z))
                    .FirstOrDefault();
                var published = roster.FirstOrDefault(monster =>
                    string.Equals(monster.Definition.TemplateKey, blueprint.Template.TemplateKey, StringComparison.Ordinal));
                try
                {
                    var packet = WorldRuntimeSpawnPackets.Build(published?.Definition.Packet, MapId, objectId,
                        blueprint.Template.TemplateKey, blueprint.Level, blueprint.MaximumHealth, blueprint.X,
                        nearest?.Y ?? 0f, blueprint.Z, blueprint.Facing, blueprint.Spec.Camp);
                    var definition = new CapturedMonsterSpawn(MapId,
                        string.IsNullOrEmpty(blueprint.Template.SceneKey) ? published?.Definition.SceneKey ?? "" : blueprint.Template.SceneKey,
                        blueprint.Template.TemplateKey, blueprint.Template.DisplayName, objectId, blueprint.X, blueprint.Z,
                        packet);
                    definition.Validate(MapId);
                    var runtime = MonsterMapRuntimeFactory.Create(_monsterRuntimeMode, MapId, [definition], now,
                        respawnPolicy: MonsterRespawnPolicy.Never, monsterCombatProfiles: _monsterCombatProfiles);
                    built.Add(new WorldRuntimeSpawnStage(runtime, definition, blueprint.Spec));
                }
                catch (Exception error) when (error is ArgumentException or InvalidDataException or
                    InvalidOperationException or OverflowException)
                {
                    refusal = $"spawn rejected: {error.Message}";
                    return false;
                }
            }
            stages = built;
            refusal = string.Empty;
            return true;
        }
    }

    /// <summary>ROUND3 summons: the camp of a character on this map, for allied spawns.</summary>
    private int? CampOfCharacter(int characterId) =>
        _sessions.Values.FirstOrDefault(context => context.CharacterId == characterId)?.Character.Camp;

    /// <summary>The publication point: installs the overlay on first use and attaches the staged spawn.</summary>
    internal bool TryAttachWorldRuntimeSpawn(WorldRuntimeSpawnStage stage, DateTimeOffset now, out string refusal)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return TryAttachWorldRuntimeSpawnGroup([stage], now, out refusal);
    }

    /// <summary>ROUND3 summons: attaches staged spawns all or nothing (the single attach is a group of one).</summary>
    internal bool TryAttachWorldRuntimeSpawnGroup(IReadOnlyList<WorldRuntimeSpawnStage> stages, DateTimeOffset now,
        out string refusal)
    {
        ArgumentNullException.ThrowIfNull(stages);
        lock (_membershipGate)
        {
            lock (_monsterRuntimeGate)
            {
                if (WorldRuntimeSpawnRefusal() is { } notHere || _worldRuntimeSpawns is null)
                {
                    refusal = WorldRuntimeSpawnRefusal() ?? "spawn was not staged on this map";
                    return false;
                }
                foreach (var stage in stages)
                {
                    ArgumentNullException.ThrowIfNull(stage);
                    var objectId = stage.Definition.ObjectId;
                    if (_npcCatalogDefinitions.Any(npc => npc.ObjectId == objectId) ||
                        _sessions.Values.Any(context => context.ObjectId == objectId))
                    {
                        refusal = "object id collides with an NPC or player";
                        return false;
                    }
                }
                var attached = stages.Count == 1
                    ? _worldRuntimeSpawns.TryAttach(stages[0].Runtime, stages[0].Spec, now, out refusal)
                    : _worldRuntimeSpawns.TryAttachGroup(
                        stages.Select(static stage => (stage.Runtime, stage.Spec)).ToArray(), now, out refusal);
                if (!attached)
                {
                    return false;
                }
                _monsterRuntime = _worldRuntimeSpawns;
                foreach (var stage in stages)
                {
                    Console.WriteLine($"[runtime-spawn] spawned map={MapId} object={stage.Definition.ObjectId} " +
                        $"template={stage.Definition.TemplateKey} x={stage.Definition.X:F1} z={stage.Definition.Z:F1} " +
                        $"owner={stage.Spec.OwnerCharacterId} ownerOnly={stage.Spec.OwnerOnly} " +
                        $"camp={(stage.Spec.Camp is { } camp ? camp : -1)} " +
                        $"lifetime={stage.Spec.Lifetime.TotalMinutes:F1}m source={stage.Spec.Source}");
                }
                return true;
            }
        }
    }

    /// <summary>ROUND3 summons: living players of another camp within <paramref name="radius"/> of a point.</summary>
    internal GameCharacter[] EnemyPlayersNear(int camp, float x, float z, float radius)
    {
        var limit = (double)radius * radius;
        return _sessions.Values.Where(context =>
        {
            if (!context.WorldReady || context.Character.Camp == camp || context.Character.CurrentHp <= 0)
            {
                return false;
            }
            double dx = context.Character.PositionX - x;
            double dz = context.Character.PositionZ - z;
            return dx * dx + dz * dz <= limit;
        }).Select(static context => context.Character).ToArray();
    }

    internal bool RemoveWorldRuntimeSpawn(uint objectId, DateTimeOffset now)
    {
        lock (_monsterRuntimeGate)
        {
            return _worldRuntimeSpawns?.Remove(objectId, now) == true;
        }
    }

    /// <summary>W2 F-27 TROY (slice 2): whether a runtime spawn is still attached, and whether it is alive.</summary>
    internal WorldRuntimeSpawnPresence GetWorldRuntimeSpawnPresence(uint objectId, Guid runtimeInstanceId)
    {
        lock (_monsterRuntimeGate)
        {
            if (_worldRuntimeSpawns is null || !ReferenceEquals(_monsterRuntime, _worldRuntimeSpawns) ||
                !_worldRuntimeSpawns.TryGetSpawn(objectId, runtimeInstanceId, out _))
            {
                return WorldRuntimeSpawnPresence.Gone;
            }
            return _worldRuntimeSpawns.TryGetSnapshot(objectId, out var monster) && monster.IsAlive
                ? WorldRuntimeSpawnPresence.Alive
                : WorldRuntimeSpawnPresence.Dead;
        }
    }

    internal bool TryGetWorldRuntimeSpawn(uint objectId, Guid runtimeInstanceId, out WorldRuntimeSpawnSpec spec)
    {
        lock (_monsterRuntimeGate)
        {
            if (_worldRuntimeSpawns is not null && ReferenceEquals(_monsterRuntime, _worldRuntimeSpawns))
            {
                return _worldRuntimeSpawns.TryGetSpawn(objectId, runtimeInstanceId, out spec);
            }
            spec = null!;
            return false;
        }
    }
}
