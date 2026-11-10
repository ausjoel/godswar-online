using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    public const string BirdPresentationCheckName =
        "Wonderland captured first-island attack effects preserve single damage and native timing";

    public static async Task RunBirdPresentationAsync()
    {
        // Initialized16-byte headers from external16:15:00.4647145, source21639,
        // target240. The remaining capture coordinates are not used as authority.
        Check.True(PacketBuilder.SkillCastImpact(21639, 240, 2179, 0, 0).AsSpan(0, 16).SequenceEqual(
                Convert.FromHexString("18003E2787540000F000000083080000")) &&
            PacketBuilder.SkillCastImpact(21639, 240, 2000, 0, 0).AsSpan(0, 16).SequenceEqual(
                Convert.FromHexString("18003E2787540000F0000000D0070000")),
            "literal external capture headers pin opcode10046, source/target identity and2179/2000skill IDs");
        foreach (var golden in new (uint Actor, uint Skill, string Header)[]
        {
            (22521, 2805, "18003E27F9570000F0000000F50A0000"),
            (20869, 2015, "18003E2785510000F0000000DF070000"),
            (21779, 2000, "18003E2713550000F0000000D0070000"),
            (21485, 2000, "18003E27ED530000F0000000D0070000")
        })
            Check.True(PacketBuilder.SkillCastImpact(golden.Actor, 240, golden.Skill, 0, 0).AsSpan(0, 16)
                .SequenceEqual(Convert.FromHexString(golden.Header)),
                "literal captured Alpha/tower/stooge/assaulter headers pin each native presentation binding");
        foreach (var monsters in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        foreach (var players in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
        foreach (var hit in new[] { true, false })
            await CheckBirdPresentationAsync(monsters, players, hit);
    }

    private static async Task CheckBirdPresentationAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players, bool hit)
    {
        var setup = await CreateBirdPresentationFixtureAsync(monsters, players);
        await using var f = setup.Fixture;
        await using var observer = setup.Observer;
        var observerTransport = setup.ObserverTransport;
        var viewer = setup.Viewer;
        var bird = f.Monster("raider");
        f.MoveTo(bird);
        f.Character.CalculatedStats = new CharacterStats { Hit = 3000, Dodge = hit ? 0 : int.MaxValue,
            PhysicalDefense = 4566, MagicDefense = 2630, DamageAbsorb = 3995 };
        viewer.PositionX = bird.X + 18;
        viewer.PositionZ = bird.Z;
        f.Registry.UpdateCharacter(observer, viewer, advanceWorldRevision: false);
        try
        {
            await using (var visible = await f.Registry.BeginMonsterVisibilityTransitionAsync(observer,
                207, viewer.PositionX, viewer.PositionZ, CancellationToken.None)
                ?? throw new InvalidOperationException("Bird observer visibility was unavailable."))
                visible.Commit();
            Check.True(f.Registry.IsMonsterVisibleTo(observer, bird.ObjectId),
                "the nearby observer has committed the actual bird appearance");
            var before = f.Character.CurrentHp;
            var selfStart = f.Transport.ReadLegacyPackets().Count;
            var worldStart = observerTransport.ReadLegacyPackets().Count;
            await f.IncomingAsync("raider", hit: hit);
            await observer.SendAsync(PacketBuilder.ServerNote("bird observer boundary"), CancellationToken.None);
            var selfDamage = CheckBirdVisualPrefix(f.Transport.ReadLegacyPackets().Skip(selfStart), bird.ObjectId,
                0x1448, f.Character.PositionX, f.Character.PositionZ);
            var worldDamage = CheckBirdVisualPrefix(observerTransport.ReadLegacyPackets().Skip(worldStart), bird.ObjectId,
                WorldObjectIds.ForPlayer(f.Character.Id), f.Character.PositionX, f.Character.PositionZ);
            Check.True(selfDamage == worldDamage &&
                (hit ? selfDamage == before - f.Character.CurrentHp && selfDamage > 0 :
                    f.Character.CurrentHp == before && selfDamage == uint.MaxValue),
                "self and observer see one authoritative damage result; visual impacts add no HP mutation even on a miss");
            Check.True(f.Runtime.Map.TryGetWonderlandSpawnPolicy(bird.ObjectId, out var policy) &&
                policy.Role == WonderlandMonsterRole.DemonicRaider && policy.Stats.MagicAttack == 34211 &&
                policy.Stats.AttackInterval == TimeSpan.FromSeconds(1.92) &&
                WonderlandBossAbilityPolicy.For("raider").Count == 0,
                "the visual mapping adds no spell scheduler, damage multiplier, area attack or cooldown");

            foreach (var other in new (string Key, uint Skill, int Attack, double Interval)[]
                { ("alpha", 2805, 15147, 1.92), ("tower", 2015, 10147, 1.92),
                  ("support", 2000, 8000, 1.92), ("assaulter", 2000, 10000, 1.92) })
            {
                var source = f.Monster(other.Key);
                viewer.PositionX = source.X + 18;
                viewer.PositionZ = source.Z;
                f.Registry.UpdateCharacter(observer, viewer, advanceWorldRevision: false);
                await using (var visible = await f.Registry.BeginMonsterVisibilityTransitionAsync(observer,
                    207, viewer.PositionX, viewer.PositionZ, CancellationToken.None)
                    ?? throw new InvalidOperationException("First-island observer visibility was unavailable."))
                    visible.Commit();
                before = f.Character.CurrentHp;
                selfStart = f.Transport.ReadLegacyPackets().Count;
                worldStart = observerTransport.ReadLegacyPackets().Count;
                await f.IncomingAsync(other.Key, hit: hit);
                await observer.SendAsync(PacketBuilder.ServerNote("first-island observer boundary"), CancellationToken.None);
                selfDamage = CheckBirdVisualPrefix(f.Transport.ReadLegacyPackets().Skip(selfStart), source.ObjectId,
                    0x1448, f.Character.PositionX, f.Character.PositionZ, other.Skill);
                worldDamage = CheckBirdVisualPrefix(observerTransport.ReadLegacyPackets().Skip(worldStart), source.ObjectId,
                    WorldObjectIds.ForPlayer(f.Character.Id), f.Character.PositionX, f.Character.PositionZ, other.Skill);
                Check.True(selfDamage == worldDamage &&
                    (hit ? selfDamage == before - f.Character.CurrentHp && selfDamage > 0 :
                        f.Character.CurrentHp == before && selfDamage == uint.MaxValue) &&
                    f.Runtime.Map.TryGetWonderlandSpawnPolicy(source.ObjectId, out policy) &&
                    policy.Stats.PhysicalAttack == other.Attack &&
                    policy.Stats.AttackInterval == TimeSpan.FromSeconds(other.Interval) &&
                    WonderlandBossAbilityPolicy.For(other.Key).Count == 0,
                    $"{other.Key} keeps its exact captured effect pair, one damage result and unchanged attack policy");
            }
        }
        finally { f.Registry.Remove(observer); }
    }

    private static async Task<(Fixture Fixture, ClientSession Observer,
        FactionCrierCaptureTransport ObserverTransport, GameCharacter Viewer)> CreateBirdPresentationFixtureAsync(
        MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        var transport = new FactionCrierCaptureTransport();
        var session = new ClientSession(transport);
        var observerTransport = new FactionCrierCaptureTransport();
        var observer = new ClientSession(observerTransport);
        var registry = new GameSessionRegistry(store: null, zodiacEnergyOptions: null, monsters, players,
            gameplayCatalogs: GameplayRuntimeCatalogs.Create(WonderlandMapChecks.Content()));
        try
        {
            var created = await registry.CreateLocalWorldInstanceAsync(RealmId.Tempest, new(207), InstanceKind.Dungeon, 5);
            var runtime = created.Runtime ?? throw new InvalidOperationException("Bird presentation instance was unavailable.");
            var character = new GameCharacter { Id = 101, AccountId = 101, Name = "BirdTarget", Level = 130,
                CurrentMap = 207, CurrentHp = 10000000, MaxHp = 10000000, CurrentMp = 1000, MaxMp = 1000,
                Camp = GameDefaults.SpartaCamp, CalculatedStats = Fixture.Stats() };
            var viewer = new GameCharacter { Id = 102, AccountId = 102, Name = "BirdObserver", Level = 130,
                CurrentMap = 207, CurrentHp = 100000, MaxHp = 100000, Camp = GameDefaults.SpartaCamp };
            var targetFence = GameHandlerOwnershipTestFences.Bind(registry, session, character.AccountId, character);
            var viewerFence = GameHandlerOwnershipTestFences.Bind(registry, observer, viewer.AccountId, viewer);
            var reservation = Guid.NewGuid();
            var now = runtime.Descriptor.CreatedAt;
            registry.JoinWorldInstance(session, character.AccountId, character, WorldObjectIds.ForPlayer(character.Id),
                runtime.InstanceId, joinedAt: now);
            registry.JoinWorldInstance(observer, viewer.AccountId, viewer, WorldObjectIds.ForPlayer(viewer.Id),
                runtime.InstanceId, joinedAt: now);
            Check.True(registry.TryStartWonderlandEncounter(runtime.InstanceId, dailyLimit: 3,
                [new(session, character.AccountId, character.Id, character.Name, character.Level,
                    character.Profession, RealmId.Tempest,
                    runtime.InstanceId, 207, targetFence),
                 new(observer, viewer.AccountId, viewer.Id, viewer.Name, viewer.Level,
                    viewer.Profession, RealmId.Tempest,
                    runtime.InstanceId, 207, viewerFence)], now, reservation),
                "both target and observer belong to the frozen Wonderland admission roster");
            registry.RecordWonderlandAdmissions(reservation, [character.Id, viewer.Id]);
            registry.CompleteWonderlandAdmissions(reservation);
            Check.True(runtime.Map.TryGetWonderlandSnapshot(out var admitted) &&
                admitted.Participants.Select(member => member.CharacterId).Order().SequenceEqual(new[] { 101, 102 }),
                "finalized encounter admission retains both real players before publication begins");
            var fixture = new Fixture { Registry = registry, Runtime = runtime, Session = session,
                Transport = transport, Character = character,
                Now = WonderlandMapChecks.EnterIsland(runtime.Map, 1, now) };
            return (fixture, observer, observerTransport, viewer);
        }
        catch
        {
            registry.Remove(session);
            registry.Remove(observer);
            await session.DisposeAsync();
            await observer.DisposeAsync();
            await registry.DisposeAsync();
            throw;
        }
    }

    private static uint CheckBirdVisualPrefix(IEnumerable<byte[]> packets, uint birdId,
        uint targetId, float targetX, float targetZ, uint firstSkill = 2179)
    {
        var prefix = packets.Where(packet => packet.Length >= 8 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == birdId &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) is 10026 or 10040 or 10045 or 10046 or 10062).ToArray();
        Check.True(prefix.Length == 3 &&
            BinaryPrimitives.ReadUInt16LittleEndian(prefix[0].AsSpan(2)) == 10046 &&
            prefix[0].SequenceEqual(PacketBuilder.SkillCastImpact(birdId, targetId, firstSkill, targetX, targetZ)) &&
            prefix[1].SequenceEqual(PacketBuilder.SkillCastImpact(birdId, targetId, 2000, targetX, targetZ)) &&
            prefix[2].Length == 32 && BinaryPrimitives.ReadUInt16LittleEndian(prefix[2].AsSpan(2)) == 10026 &&
            BinaryPrimitives.ReadUInt32LittleEndian(prefix[2].AsSpan(20)) == targetId &&
            prefix[2][28] == 1 && prefix[2][30] == 0 && prefix[2][31] == 0,
            "the captured role effect pair precedes exactly one captured-layout damage packet");
        return BinaryPrimitives.ReadUInt32LittleEndian(prefix[2].AsSpan(24));
    }
}
