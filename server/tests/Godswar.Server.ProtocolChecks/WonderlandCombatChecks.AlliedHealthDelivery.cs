using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    private static async Task CheckMarshalHealthPublicationAsync(Fixture f, uint enemyId, uint allyId)
    {
        // The preceding real lure has already delivered allied damage. The
        // next player's hit must continue that HP revision, not remove/spawn
        // the still-living marshal to recover a silently skipped NPC update.
        for (var cycle = 0; cycle < 4; cycle++)
        {
            if (cycle > 0)
            {
                f.Now = f.Now.AddSeconds(2);
                await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now, CancellationToken.None);
                await f.FlushAsync();
            }
            var enemy = f.Monsters().Single(monster => monster.ObjectId == enemyId);
            Check.True(f.TryDirectHit(enemy, 100, out var damage) && damage.HealthMutation.HasValue,
                "player damage commits after the allied marshal's real HP mutation");
            var before = f.Transport.ReadLegacyPackets().Count;
            var area = cycle % 2 != 0;
            var packet = PacketBuilder.PhysicalDamage(0x1448, f.Character.PositionX, 0,
                f.Character.PositionZ, enemyId, 100, result: 1);
            var delivered = area
                ? await f.Registry.DeliverMonsterAreaDamageToViewerAsync(f.Session, 207, 0x1448, 574,
                    [new(damage.HealthMutation!.Value, 100)], CancellationToken.None)
                : await f.Registry.DeliverMonsterHealthPacketToViewerAsync(f.Session, 207,
                    enemyId, packet, damage.HealthMutation!.Value, CancellationToken.None,
                    label: "MarshalPlayerBasicAfterAid");
            await f.FlushAsync();
            var packets = f.Transport.ReadLegacyPackets().Skip(before).ToArray();
            Check.True(delivered && packets.All(p => p.Length < 4 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2)) is not (0x2728 or 10020)) &&
                packets.Count(p => p.Length >= 4 && BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2)) ==
                    (area ? 10047 : 10026)) == 1,
                "alternating player basic/AOE hits after allied aid never remove or re-spawn the living marshal");
            before = f.Transport.ReadLegacyPackets().Count;
            Check.True(!await f.Registry.DeliverMonsterHealthPacketToViewerAsync(f.Session, 207,
                    enemyId, packet, damage.HealthMutation!.Value, CancellationToken.None),
                "the player's delivered HP revision cannot be published a second time");
            await f.FlushAsync();
            Check.True(f.Transport.ReadLegacyPackets().Skip(before).All(p => p.Length < 4 ||
                BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2)) is not (0x2728 or 10020 or 10026)),
                "duplicate delivery adds no damage or visibility flicker");
        }
        await CheckAlliedVisibilityLeaseWaitAsync(f, enemyId);
        await CheckAlliedSourceAppearanceAsync(f, enemyId, allyId);
    }

    private static async Task CheckAlliedVisibilityLeaseWaitAsync(Fixture f, uint enemyId)
    {
        var registryGate = typeof(GameSessionRegistry).GetField("_gate",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Registry)!;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var held = await f.Registry.BeginMonsterVisibilityTransitionAsync(f.Session, 207,
                f.Character.PositionX, f.Character.PositionZ, CancellationToken.None)
                ?? throw new InvalidOperationException("Held allied visibility transition unavailable.");
            Task tick = Task.CompletedTask;
            var waited = false;
            try
            {
                var hp = f.Monsters().Single(monster => monster.ObjectId == enemyId).CurrentHealth;
                f.Now = f.Now.AddSeconds(2);
                tick = Task.Run(() => f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now, CancellationToken.None));
                await Task.WhenAny(tick, Task.Delay(100));
                if (tick.IsCompleted) continue; // A missed attack produces no health publication.
                waited = f.Monsters().Single(monster => monster.ObjectId == enemyId).CurrentHealth < hp;
                Check.True(await Task.Run(() => { lock (registryGate) return true; })
                    .WaitAsync(TimeSpan.FromSeconds(2)),
                    "allied damage waiting for visibility never holds the registry gate");
            }
            finally
            {
                await held.DisposeAsync();
                await tick.WaitAsync(TimeSpan.FromSeconds(5));
            }
            if (waited) return;
        }
        throw new InvalidOperationException("No allied hit exercised the blocked viewer delivery gate.");
    }

    private static async Task CheckAlliedSourceAppearanceAsync(Fixture f, uint enemyId, uint allyId)
    {
        var enemy = f.Monsters().Single(monster => monster.ObjectId == enemyId);
        var ally = f.Monsters().Single(monster => monster.ObjectId == allyId);
        // Model a viewer that has already retained the target but not the
        // helper. Lured actors can share a visibility cell in this fixture,
        // so remove only the source presentation under the real viewer gate.
        await using (var held = await f.Runtime.Map.AcquireMonsterViewerDeliveryLeaseAsync(f.Session,
            enemyId, CancellationToken.None) ?? throw new InvalidOperationException("Source visibility lease unavailable."))
        {
            var viewers = (ConcurrentDictionary<ClientSession, MonsterViewerState>)typeof(MapInstance)
                .GetField("_monsterViewers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Runtime.Map)!;
            Check.True(viewers[f.Session].VisibleMonsterVersions.TryRemove(allyId, out _),
                "only the allied source presentation is removed from the fixture");
            await f.Session.SendAsync(PacketBuilder.RemoveWorldObjects(allyId), CancellationToken.None);
        }
        Check.True(!f.Runtime.Map.IsMonsterVisibleTo(f.Session, allyId) &&
            f.Runtime.Map.IsMonsterVisibleTo(f.Session, enemyId),
            "the viewer retains the actual enemy and its HP revision without the assisting source");
        var before = f.Transport.ReadLegacyPackets().Count;
        for (var attempt = 0; attempt < 10 && !f.Runtime.Map.IsMonsterVisibleTo(f.Session, allyId); attempt++)
        {
            f.Now = f.Now.AddSeconds(2);
            await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now, CancellationToken.None);
        }
        await f.FlushAsync();
        var packets = f.Transport.ReadLegacyPackets().Skip(before).ToArray();
        var appearanceIndex = Array.FindIndex(packets, p => p.Length >= 12 &&
            BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2)) == 10020 &&
            BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(8)) == allyId);
        var damageIndex = Array.FindIndex(packets, p => IsDamagePacket(p) &&
            BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4)) == allyId &&
            BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(20)) == enemyId);
        Check.True(appearanceIndex >= 0 && damageIndex > appearanceIndex &&
            packets.All(p => p.Length < 4 || BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2)) != 0x2728),
            "missing allied source is hydrated before damage without removing the visible enemy");
        MovePlayer(f, ally.X, ally.Z);
        await using (var transition = await f.Registry.BeginMonsterVisibilityTransitionAsync(f.Session, 207,
            ally.X, ally.Z, CancellationToken.None)
            ?? throw new InvalidOperationException("Restored allied visibility unavailable."))
            transition.Commit();
    }
}
