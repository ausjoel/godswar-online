using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static readonly MethodInfo AreaOutcomeProjection = typeof(GameClientHandler).GetMethod(
        "CreateEcsMonsterAreaBroadcastHits", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("The committed ECS area presentation projection was not found.");

    private static async Task CheckMixedMonsterAreaOutcomesAsync()
    {
        foreach (var mode in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
            await CheckMixedMonsterAreaOutcomesAsync(mode);
    }

    private static async Task CheckMixedMonsterAreaOutcomesAsync(MonsterRuntimeMode mode)
    {
        await using var caster = await RuntimePolicySessionSocket.CreateAsync();
        await using var observer = await RuntimePolicySessionSocket.CreateAsync();
        var actor = CreateCharacter();
        actor.CurrentMap = 0;
        actor.PositionX = 132;
        actor.PositionZ = 100;
        var viewer = CreateCharacter();
        viewer.Id += 1;
        viewer.AccountId += 1;
        viewer.Name = "MixedAreaViewer";
        viewer.CurrentMap = 0;
        viewer.PositionX = 70;
        viewer.PositionZ = 100;
        var criticalMonster = CreateCapturedMonster(10_051, 100, 100, "A_normal_stub_001");
        var normalMonster = CreateCapturedMonster(10_052, 164, 100, "A_normal_stub_002");
        var registry = new GameSessionRegistry(monsterRuntimeMode: mode);
        registry.InitializeMapMonsters(0, [criticalMonster, normalMonster], DateTimeOffset.UtcNow);
        registry.JoinMap(caster.Session, actor.AccountId, actor, WorldObjectIds.ForPlayer(actor.Id));
        registry.JoinMap(observer.Session, viewer.AccountId, viewer, WorldObjectIds.ForPlayer(viewer.Id));
        try
        {
            foreach (var (socket, character, expectedCount) in new[]
                     { (caster, actor, 2), (observer, viewer, 1) })
            {
                await using var visibility = await registry.BeginMonsterVisibilityTransitionAsync(
                    socket.Session, 0, character.PositionX, character.PositionZ, CancellationToken.None)
                    ?? throw new InvalidOperationException("Mixed area viewer visibility was unavailable.");
                Check.Equal(expectedCount, visibility.Delta.Entering.Count,
                    "the caster sees both outcomes while the other viewer sees only the critical target");
                await socket.Session.SendAsync(PacketBuilder.CapturedMonsterSpawns(
                    visibility.Delta.Entering.Select(monster => monster.Appearance).ToArray()),
                    CancellationToken.None, "MixedAreaAppearances", framed: false);
                visibility.Commit();
                for (var i = 0; i < expectedCount; i++)
                    _ = await socket.ReadPacketAsync(108);
            }

            Check.True(registry.TryApplyMonsterDamage(0, criticalMonster.ObjectId, 5_000, out var criticalHit) &&
                criticalHit.Killed && criticalHit.BeforeHealth < 5_000 && criticalHit.AfterHealth == 0,
                "critical fixture commits lethal overkill independently of its presentation byte");
            Check.True(registry.TryApplyMonsterDamage(0, normalMonster.ObjectId, 11, out var normalHit) &&
                !normalHit.Killed && normalHit.BeforeHealth - normalHit.AfterHealth == 11,
                "normal fixture commits its unchanged damage independently");
            var decision = MixedAreaDecision(criticalHit, normalHit);
            var hits = ProjectAreaOutcomes(decision);
            Check.True(hits.Length == 2 && hits[0].HealthMutation.ObjectId == normalMonster.ObjectId &&
                hits[0].Outcome == CombatHitOutcome.Normal && hits[1].Outcome == CombatHitOutcome.Critical,
                "committed outcomes follow object/generation/revision identity after misses, rejection and reordered hits");
            CheckAreaOutcomeIdentityGuards(decision);

            Check.True(await registry.DeliverMonsterAreaDamageToViewerAsync(caster.Session, 0,
                0x1448, 574, hits, CancellationToken.None), "mixed outcomes reach the caster");
            var expected = new Dictionary<uint, (uint Damage, byte Style)>
            {
                [criticalMonster.ObjectId] = (5_000, 0), [normalMonster.ObjectId] = (11, 1)
            };
            AssertMixedAreaPacket(await caster.ReadPacketAsync(41), 0x1448, expected);

            var marker = PacketBuilder.MonsterLifecycleMarker(0xABCA01);
            Check.Equal(1, await registry.BroadcastMonsterAreaDamageToViewersAsync(0, marker, marker,
                WorldObjectIds.ForPlayer(actor.Id), 574, hits, CancellationToken.None, caster.Session),
                "world delivery filters the invisible normal hit without rematching the remaining outcome");
            _ = await observer.ReadPacketAsync(marker.Length);
            _ = await observer.ReadPacketAsync(marker.Length);
            AssertMixedAreaPacket(await observer.ReadPacketAsync(29), WorldObjectIds.ForPlayer(actor.Id),
                new Dictionary<uint, (uint Damage, byte Style)> { [criticalMonster.ObjectId] = (5_000, 0) });
            Check.True(!await registry.DeliverMonsterAreaDamageToViewerAsync(caster.Session, 0,
                0x1448, 574, hits, CancellationToken.None),
                "mixed critical/normal damage cannot replay already delivered health revisions");
            Check.Equal(0, await registry.BroadcastMonsterAreaDamageToViewersAsync(0, marker, marker,
                WorldObjectIds.ForPlayer(actor.Id), 574, hits, CancellationToken.None, caster.Session),
                "world viewer health stamps also suppress duplicate critical overkill presentation");
        }
        finally
        {
            registry.Remove(caster.Session);
            registry.Remove(observer.Session);
        }
    }

    private static PlayerCombatEcsDecision MixedAreaDecision(MonsterDamageResult criticalHit,
        MonsterDamageResult normalHit)
    {
        static PlayerCombatEcsResolvedTarget Resolved(MonsterDamageResult result, uint damage,
            CombatHitOutcome outcome, int order) => new(result.ObjectId,
            result.Monster.SpawnGeneration, result.HealthMutation!.Value.BeforeHealthRevision,
            new(4, 812, order, CombatDamageChannel.Magic, outcome, damage, default, default));
        return new(812, PlayerCombatIntentKind.AreaSkill, PlayerCombatRejectionReason.None,
            PlayerCombatMutationRejectionReason.None, 4, 2, 1, 0, false, 500, 0,
            DateTimeOffset.MinValue,
            [new(normalHit, 11, null), new(criticalHit, 5_000, null)],
            [Resolved(criticalHit, 5_000, CombatHitOutcome.Critical, 3),
                new(90_001, 1, 0, new(4, 812, 0, CombatDamageChannel.Magic,
                    CombatHitOutcome.Miss, 0, default, default)),
                new(90_002, 1, 0, new(4, 812, 1, CombatDamageChannel.Magic,
                    CombatHitOutcome.Critical, 999, default, default)),
                Resolved(normalHit, 11, CombatHitOutcome.Normal, 2)]);
    }

    private static MonsterAreaDamageBroadcastHit[] ProjectAreaOutcomes(PlayerCombatEcsDecision decision) =>
        (MonsterAreaDamageBroadcastHit[])AreaOutcomeProjection.Invoke(null, [decision])!;

    private static void CheckAreaOutcomeIdentityGuards(PlayerCombatEcsDecision decision)
    {
        var first = decision.Resolutions[0];
        foreach (var replacement in new[]
                 {
                     first with { TargetObjectId = first.TargetObjectId + 100 },
                     first with { SpawnGeneration = first.SpawnGeneration + 1 },
                     first with { HealthRevision = first.HealthRevision + 1 },
                     first with { Resolution = first.Resolution with { Outcome = CombatHitOutcome.Miss } },
                     first with { Resolution = first.Resolution with { Damage = 4_999 } }
                 })
            AssertRejected(decision with { Resolutions = decision.Resolutions.SetItem(0, replacement) });
        AssertRejected(decision with { Resolutions = decision.Resolutions.Add(first) });

        static void AssertRejected(PlayerCombatEcsDecision malformed)
        {
            try
            {
                _ = ProjectAreaOutcomes(malformed);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException)
            {
                return;
            }
            throw new InvalidOperationException("A stale, mismatched or ambiguous committed area outcome was accepted.");
        }
    }

    private static void AssertMixedAreaPacket(byte[] packet, uint caster,
        IReadOnlyDictionary<uint, (uint Damage, byte Style)> expected)
    {
        Check.True(BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 10047 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == caster &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12)) == 574 &&
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(8)) == expected.Count,
            "mixed damage retains the native caster, skill and filtered target count");
        var seen = new HashSet<uint>();
        for (var index = 0; index < expected.Count; index++)
        {
            var offset = 17 + index * 12;
            var id = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(offset));
            Check.True(seen.Add(id) && expected.TryGetValue(id, out var hit),
                "every entry belongs to exactly one expected visible target");
            hit = expected[id];
            Check.True(BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(offset + 8)) == hit.Damage &&
                packet[offset + 4] == hit.Style && packet[offset + 5] == 0,
                "normal/critical styles 1/0 preserve the full damage and HP channel, including lethal overkill");
        }
    }
}
