using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    public const string AreaFeedbackCheckName =
        "Wonderland lethal six-bird AOE preserves native floating damage and exact health once";

    public static async Task RunAreaFeedbackAsync()
    {
        foreach (var monsters in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        foreach (var players in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
            await CheckLethalBirdAreaFeedbackAsync(monsters, players);
    }

    private static async Task CheckLethalBirdAreaFeedbackAsync(MonsterRuntimeMode monsters,
        PlayerRuntimeMode players)
    {
        await using var f = await Fixture.CreateAsync(3, monsters, players);
        const uint reportedDamage = 8_078_181; // Lethal full-width value against the captured five-million bird HP.
        MovePlayer(f, -126, -119);
        var birds = f.Monsters().Where(monster => monster.IsAlive &&
            f.Runtime.Map.TryGetWonderlandSpawnPolicy(monster.ObjectId, out var policy) &&
            policy.Stage == 3 && policy.MechanicKey == "petbird").Take(6).ToArray();
        Check.Equal(6, birds.Length, "six real targets are selected from the captured twenty-four pet birds");
        await using (var visible = await f.Runtime.Map.BeginMonsterVisibilityTransitionAsync(f.Session,
                         f.Character.PositionX, f.Character.PositionZ, CancellationToken.None)
                     ?? throw new InvalidOperationException("Bird AOE visibility was unavailable."))
        {
            Check.True(birds.All(bird => visible.IsDesiredVisible(bird.ObjectId)),
                "every target actor is hydrated before the native cluster can reference it");
            await f.Session.SendAsync(PacketBuilder.CapturedMonsterSpawns(
                    visible.Delta.Entering.Select(monster => monster.Appearance).ToArray()),
                CancellationToken.None, "BirdAreaAppearances", framed: false);
            visible.Commit();
        }
        var hits = new List<MonsterAreaDamageBroadcastHit>();
        foreach (var bird in birds)
        {
            Check.True(f.TryDirectHit(bird, reportedDamage, out var result) && result.Killed &&
                result.AfterHealth == 0 && result.BeforeHealth == bird.CurrentHealth,
                "the primary authoritative mutation kills each real bird exactly once");
            hits.Add(new(result.HealthMutation!.Value, reportedDamage));
        }
        var before = f.Transport.ReadLegacyPackets().Count;
        Check.True(await f.Registry.DeliverMonsterAreaDamageToViewerAsync(f.Session, 207, 0x1448,
            314, hits, CancellationToken.None), "committed lethal AOE reaches its exact hydrated viewer");
        await f.FlushAsync();
        var clusters = f.Transport.ReadLegacyPackets().Skip(before).Where(packet => packet.Length >= 17 &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 10047).ToArray();
        Check.Equal(1, clusters.Length, "all six lethal hits use one native cluster with no duplicate damage packet");
        var cluster = clusters[0];
        Check.True(cluster.Length == 17 + 6 * 12 && BinaryPrimitives.ReadInt32LittleEndian(cluster.AsSpan(8)) == 6 &&
            BinaryPrimitives.ReadUInt32LittleEndian(cluster.AsSpan(4)) == 0x1448 &&
            BinaryPrimitives.ReadUInt32LittleEndian(cluster.AsSpan(12)) == 314,
            "the original local caster, skill and full target count remain intact");
        var nativeHealth = birds.ToDictionary(bird => bird.ObjectId, bird => (long)bird.CurrentHealth);
        var floats = new List<uint>();
        // Native4E8F27 iterates LAST to FIRST and aborts on any missing actor.
        // It shows positive digits only for AttackType<=2 and updates HP for
        // every type.4DE740 clamps that HP to zero independently of the flag.
        for (var index = 5; index >= 0; index--)
        {
            var offset = 17 + index * 12;
            var id = BinaryPrimitives.ReadUInt32LittleEndian(cluster.AsSpan(offset));
            var damage = BinaryPrimitives.ReadUInt32LittleEndian(cluster.AsSpan(offset + 8));
            Check.True(nativeHealth.ContainsKey(id) && damage == reportedDamage && cluster[offset + 4] == 1,
                "every lethal entry carries native normal damage style and the unclipped 32-bit resolved amount");
            if (cluster[offset + 4] <= 2) floats.Add(damage);
            nativeHealth[id] = Math.Max(0, nativeHealth[id] - damage);
        }
        Check.True(floats.Count == 6 && nativeHealth.Values.All(hp => hp == 0) &&
            f.Monsters().Where(monster => nativeHealth.ContainsKey(monster.ObjectId)).All(monster => !monster.IsAlive),
            "all six positive floating numbers coexist with the exact authoritative zero-HP deaths");
        before = f.Transport.ReadLegacyPackets().Count;
        Check.True(!await f.Registry.DeliverMonsterAreaDamageToViewerAsync(f.Session, 207, 0x1448,
            314, hits, CancellationToken.None), "replaying the same health revisions cannot apply or display the AOE twice");
        await f.FlushAsync();
        Check.True(f.Transport.ReadLegacyPackets().Skip(before).All(packet =>
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) != 10047),
            "the duplicate operation emits no native damage cluster");
    }
}
