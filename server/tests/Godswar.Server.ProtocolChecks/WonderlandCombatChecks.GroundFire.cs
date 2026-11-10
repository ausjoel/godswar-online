using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Networking;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    public const string GroundFireCheckName =
        "Wonderland island-wide ground fire hydrates its native source and fences delayed visuals";

    public static async Task RunGroundFireAsync()
    {
        foreach (var monsters in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        foreach (var players in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
        {
            await CheckTerrainFireAsync(monsters, players);
            await CheckGroundFireFromDistantClearPointAsync(monsters, players);
            foreach (var rejection in new[] { "life", "ownership", "island", "cancel", "source" })
                await CheckDelayedGroundFireAsync(monsters, players, rejection);
        }
    }

    private static bool IsGroundFireImpact(byte[] packet) => packet.Length == 24 &&
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 10046 &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12)) == 589;

    private static async Task CheckGroundFireFromDistantClearPointAsync(MonsterRuntimeMode monsters,
        PlayerRuntimeMode players)
    {
        await using var f = await Fixture.CreateAsync(6, monsters, players);
        var geometry = WonderlandTerrainPolicy.GetIsland(6);
        var source = f.Monster("platinum");
        // The captured entrance is now inside the dragon's ordinary AOI.
        // Exercise the source-visibility exception at the island's western
        // edge, clear of every authored fire circle and still inside its bounds.
        var clearPoint = new WonderlandPosition(geometry.Bounds.MinimumX + 6, 0, geometry.Entrance.Z);
        MovePlayer(f, clearPoint.X, clearPoint.Z);
        Check.True(geometry.Bounds.Contains(clearPoint.X, clearPoint.Z) &&
            geometry.GroundFirePositions.All(point => WonderlandTerrainPolicy.DistanceSquared(point,
                clearPoint.X, clearPoint.Z) > WonderlandBossAbilityPolicy.TerrainFire.Radius * WonderlandBossAbilityPolicy.TerrainFire.Radius),
            "the distant test position belongs to island six and lies outside every fire circle");
        Check.True(WorldSectorVisibilityTracker<int>.TryGetCell(source.X, source.Z, out var sourceCell) &&
            WorldSectorVisibilityTracker<int>.TryGetCell(clearPoint.X, clearPoint.Z, out var playerCell) &&
            !WorldSectorVisibilityTracker<int>.IsNeighbor(sourceCell, playerCell) &&
            !f.Runtime.Map.IsMonsterVisibleTo(f.Session, source.ObjectId),
            "the distant viewer starts outside the actual dragon AOI with no hydrated source");
        var before = f.Transport.ReadLegacyPackets().Count;
        var hp = f.Character.CurrentHp;
        await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now, CancellationToken.None);
        await f.FlushAsync();
        var frames = f.Transport.ReadLegacyPackets().Skip(before).ToArray();
        var appearance = Array.FindIndex(frames, packet => packet.Length == source.Definition.Packet.Length &&
            packet.AsSpan(0, 20).SequenceEqual(source.Definition.Packet.AsSpan(0, 20)));
        Check.True(appearance >= 0 && appearance < Array.FindIndex(frames, IsCastWarning) &&
            frames.Count(IsCastWarning) == 3 && f.Runtime.Map.IsMonsterVisibleTo(f.Session, source.ObjectId),
            "native dragon appearance precedes all coordinate fire warnings outside ordinary AOI");
        await using (var visibility = await f.Runtime.Map.BeginMonsterVisibilityTransitionAsync(f.Session,
                         f.Character.PositionX, f.Character.PositionZ, CancellationToken.None)
                     ?? throw new InvalidOperationException("Ground-fire visibility unavailable."))
        {
            Check.True(visibility.IsDesiredVisible(source.ObjectId) &&
                !visibility.Delta.Leaving.Contains(source.ObjectId) &&
                visibility.Delta.Entering.All(monster => monster.ObjectId != source.ObjectId),
                "ordinary AOI refresh retains the hydrated fire source without duplicate dragon spawns");
            visibility.Commit();
        }
        await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime,
            f.Now + WonderlandBossAbilityPolicy.TerrainFire.Windup, CancellationToken.None);
        await f.FlushAsync();
        Check.True(f.Transport.ReadLegacyPackets().Skip(before).Count(IsGroundFireImpact) == 3 &&
            !f.Transport.ReadLegacyPackets().Skip(before).Any(IsDamagePacket) && f.Character.CurrentHp == hp,
            "all three empty circles render at impact while the distant viewer's HP remains untouched");
        var seventh = WonderlandTerrainPolicy.GetIsland(7);
        MovePlayer(f, seventh.Entrance.X, seventh.Entrance.Z);
        await using (var visibility = await f.Runtime.Map.BeginMonsterVisibilityTransitionAsync(f.Session,
                         f.Character.PositionX, f.Character.PositionZ, CancellationToken.None)
                     ?? throw new InvalidOperationException("Leaving fire island visibility unavailable."))
        {
            Check.True(!visibility.IsDesiredVisible(source.ObjectId) && visibility.Delta.Leaving.Contains(source.ObjectId),
                "the fire source visibility exception ends as soon as the player leaves island six");
            visibility.Commit();
        }
    }

    private static async Task CheckDelayedGroundFireAsync(MonsterRuntimeMode monsters,
        PlayerRuntimeMode players, string rejection)
    {
        await using var f = await Fixture.CreateAsync(6, monsters, players);
        var entrance = WonderlandTerrainPolicy.GetIsland(6).Entrance;
        MovePlayer(f, entrance.X, entrance.Z);
        var before = f.Transport.ReadLegacyPackets().Count;
        var visibility = await f.Runtime.Map.BeginMonsterVisibilityTransitionAsync(f.Session,
            entrance.X, entrance.Z, CancellationToken.None)
            ?? throw new InvalidOperationException("Delayed fire visibility unavailable.");
        var pending = f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now, CancellationToken.None);
        await using var replacement = new ClientSession(new FactionCrierCaptureTransport());
        try
        {
            Check.True(!pending.IsCompleted, "fire delivery waits asynchronously behind an existing visibility transition");
            switch (rejection)
            {
                case "life": f.Registry.AdvancePlayerLifeRevision(f.Session, f.Now); break;
                case "ownership":
                    GameHandlerOwnershipTestFences.Bind(f.Registry, replacement, f.Character.AccountId, f.Character);
                    break;
                case "island":
                    var other = WonderlandTerrainPolicy.GetIsland(5).Entrance;
                    MovePlayer(f, other.X, other.Z);
                    break;
                case "cancel": f.Runtime.Map.CancelWonderland(f.Now); break;
                case "source":
                    var source = f.Monster("platinum");
                    Check.True(f.Runtime.Map.TryApplyMonsterDamageGuarded(source.ObjectId, source.CurrentHealth,
                        f.Character.Id, source.SpawnGeneration, source.HealthRevision, f.Now, out _),
                        "the real dragon can die while its uncommitted fire visual is queued");
                    break;
            }
        }
        finally { await visibility.DisposeAsync(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        // A later actual detonation may legitimately reach a newly revived
        // living player. This assertion concerns the queued pre-revive warning.
        if (rejection != "life")
            await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime,
                f.Now + WonderlandBossAbilityPolicy.TerrainFire.Windup, CancellationToken.None);
        await f.FlushAsync();
        var frames = f.Transport.ReadLegacyPackets().Skip(before).ToArray();
        Check.True(!frames.Any(IsCastWarning) && !frames.Any(IsGroundFireImpact),
            $"queued fire rechecks {rejection} before admitting any source appearance or effect");
        if (rejection == "ownership") f.Registry.Remove(replacement);
    }
}
