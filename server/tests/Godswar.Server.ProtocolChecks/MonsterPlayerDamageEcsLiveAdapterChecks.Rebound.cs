using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MonsterPlayerDamageEcsLiveAdapterChecks
{
    private static async Task CheckMonsterReboundParityAsync()
    {
        foreach (var monsterMode in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        foreach (var playerMode in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
        foreach (var attackKind in new[] { MonsterAttackDamageKind.Physical, MonsterAttackDamageKind.Magical })
            await CheckMonsterDoesNotReceiveReboundAsync(monsterMode, playerMode, attackKind);
    }

    private static async Task CheckMonsterDoesNotReceiveReboundAsync(
        MonsterRuntimeMode monsterMode, PlayerRuntimeMode playerMode, MonsterAttackDamageKind attackKind)
    {
        var label = $"{monsterMode}/{playerMode}/{attackKind}";
        var content = AtlantisLiveWaveChecks.Content();
        content = content with
        {
            MonsterTemplates = content.MonsterTemplates.Select(template =>
                template with { AttackType = (short)attackKind }).ToArray()
        };
        await using var socket = await RuntimePolicySessionSocket.CreateAsync();
        await using var registry = new GameSessionRegistry(
            store: null, zodiacEnergyOptions: null, monsterMode, playerMode,
            gameplayCatalogs: GameplayRuntimeCatalogs.Create(content));
        var character = CreateCharacter();
        character.Name = "NoMonsterRebound";
        character.Level = 160;
        character.CurrentMap = 205;
        character.CurrentHp = character.MaxHp = 1_000_000;
        character.CalculatedStats = NoMonsterReboundFixtureStats(miss: false);
        var created = await registry.CreateLocalWorldInstanceAsync(RealmId.Tempest,
            new(205), InstanceKind.Dungeon, playerCapacity: 5);
        var runtime = created.Runtime ?? throw new InvalidOperationException("Atlantis rebound fixture was not created.");
        var activeAt = runtime.Descriptor.CreatedAt;
        Check.True(registry.TryStartAtlantisEncounter(runtime.InstanceId, 1,
                [(character.Id, character.Level)], activeAt),
            $"{label}: the fixture starts a real scored Atlantis wave");
        var before = runtime.Map.SnapshotMonsters().ToDictionary(monster => monster.ObjectId);
        var attacker = before.Values.Single(monster => monster.ObjectId == 42_000);
        character.PositionX = attacker.X;
        character.PositionZ = attacker.Z;
        var playerObjectId = WorldObjectIds.ForPlayer(character.Id);
        registry.JoinWorldInstance(socket.Session, character.AccountId, character,
            playerObjectId, runtime.InstanceId, joinedAt: activeAt);
        await using (var visibility = await registry.BeginMonsterVisibilityTransitionAsync(
            socket.Session, character.CurrentMap, character.PositionX, character.PositionZ,
            CancellationToken.None) ?? throw new InvalidOperationException("Rebound visibility was unavailable."))
        {
            visibility.Commit();
        }

        var preparedRewards = 0;
        var publishedRewards = 0;
        registry.RegisterPveMonsterKillRewardPreparer(socket.Session, damage =>
        {
            preparedRewards++;
            _ = AtlantisMonsterKillScoring.RecordCommitted(runtime.Map, content,
                runtime.InstanceId, damage, DateTimeOffset.UtcNow);
            return Task.FromResult<PreparedPveMonsterKillReward?>(new PreparedPveMonsterKillReward(_ =>
            {
                publishedRewards++;
                return Task.CompletedTask;
            }));
        });

        var profile = registry.GameplayCatalogs.MonsterCombatProfiles.Resolve(attacker.Definition);
        Check.True(profile.AttackKind == attackKind && attacker.CurrentHealth < 60_879,
            $"{label}: the former flat rebound would kill this actual Atlantis monster");
        ulong lastEvent = 0;
        for (var hit = 0; hit < 3; hit++)
        {
            var eventId = FindMonsterOutcomeEventId(profile, character, lastEvent + 1, hit: true);
            var expected = MonsterIncomingCombatPolicy.ResolveAttack(profile, character, default, eventId);
            var hpBefore = character.CurrentHp;
            var attack = NoReboundAttack(registry, socket, character, attacker, eventId);
            await registry.ProcessMonsterAttackForSessionAsync(socket.Session, attack, CancellationToken.None);
            Check.Equal(expected.Damage, checked((uint)(hpBefore - character.CurrentHp)),
                $"{label}: repeated incoming hits retain their authored player damage");
            Check.True(expected.Damage > 0, $"{label}: the incoming hit deals real damage");
            await AssertNoMonsterReboundPacketsAsync(socket, playerMode, attacker.ObjectId,
                expected.CapturedDamageValue);
            AssertAtlantisHasNoPassiveKills(registry, runtime, before, preparedRewards, publishedRewards, label);

            // Replaying the exact incoming event cannot create a monster hit,
            // a kill reward, or score even when player vitals have advanced.
            await registry.ProcessMonsterAttackForSessionAsync(socket.Session, attack, CancellationToken.None);
            AssertNoPacketTargetsMonster(await ReadNoReboundBoundaryAsync(socket), attacker.ObjectId, label);
            AssertAtlantisHasNoPassiveKills(registry, runtime, before, preparedRewards, publishedRewards, label);
            lastEvent = eventId;
        }

        character.CalculatedStats = NoMonsterReboundFixtureStats(miss: true);
        var missEvent = FindMonsterOutcomeEventId(profile, character, lastEvent + 1, hit: false);
        var hpBeforeMiss = character.CurrentHp;
        await registry.ProcessMonsterAttackForSessionAsync(socket.Session,
            NoReboundAttack(registry, socket, character, attacker, missEvent), CancellationToken.None);
        Check.Equal(hpBeforeMiss, character.CurrentHp, $"{label}: a zero-damage miss preserves player HP");
        await AssertNoMonsterReboundPacketsAsync(socket, playerMode, attacker.ObjectId, uint.MaxValue);
        AssertAtlantisHasNoPassiveKills(registry, runtime, before, preparedRewards, publishedRewards, label);

        character.CalculatedStats = NoMonsterReboundFixtureStats(miss: false);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await registry.ProcessMonsterAttackForSessionAsync(socket.Session,
                NoReboundAttack(registry, socket, character, attacker,
                    FindMonsterOutcomeEventId(profile, character, missEvent + 1, hit: true)), canceled.Token);
        }
        catch (OperationCanceledException) { }
        AssertNoPacketTargetsMonster(await ReadNoReboundBoundaryAsync(socket), attacker.ObjectId, label);
        AssertAtlantisHasNoPassiveKills(registry, runtime, before, preparedRewards, publishedRewards, label);
        registry.Remove(socket.Session);
    }

    private static CharacterStats NoMonsterReboundFixtureStats(bool miss) => new()
    {
        DamageRebound = 10_000,
        DamageReboundFlat = 60_879,
        Dodge = miss ? int.MaxValue : 0
    };

    private static MonsterRuntimeUpdate NoReboundAttack(GameSessionRegistry registry,
        RuntimePolicySessionSocket socket, GameCharacter character, MonsterRuntimeSnapshot monster, ulong eventId) =>
        new(MonsterRuntimeUpdateKind.Attacked, monster,
            TargetCharacterId: character.Id, TargetX: character.PositionX, TargetZ: character.PositionZ,
            TargetObjectId: WorldObjectIds.ForPlayer(character.Id),
            TargetLifeRevision: registry.GetPlayerLifeRevision(socket.Session),
            TargetVitalsRevision: character.VitalsRevision, AttackEventId: eventId);

    private static async Task AssertNoMonsterReboundPacketsAsync(RuntimePolicySessionSocket socket,
        PlayerRuntimeMode playerMode, uint monsterObjectId, uint incomingDamage)
    {
        var boundary = PacketBuilder.ServerNote("No monster rebound boundary");
        await socket.Session.SendAsync(boundary, CancellationToken.None, "NoMonsterReboundBoundary");
        await ReadMonsterImpactPrefixAsync(socket, playerMode, monsterObjectId);
        var damage = await socket.ReadPacketAsync(30);
        Check.True(BinaryPrimitives.ReadUInt16LittleEndian(damage.AsSpan(2)) == 0x272A &&
            BinaryPrimitives.ReadUInt32LittleEndian(damage.AsSpan(4)) == monsterObjectId &&
            BinaryPrimitives.ReadUInt32LittleEndian(damage.AsSpan(20)) == 0x1448 &&
            BinaryPrimitives.ReadUInt32LittleEndian(damage.AsSpan(24)) == incomingDamage,
            "incoming monster impact and damage retain their native source, victim, and damage value");
        Check.True((await socket.ReadPacketAsync()).SequenceEqual(boundary),
            "the incoming sequence ends without a rebound, death, or reward packet");
    }

    private static async Task<IReadOnlyList<byte[]>> ReadNoReboundBoundaryAsync(RuntimePolicySessionSocket socket)
    {
        var boundary = PacketBuilder.ServerNote("No monster rebound boundary");
        await socket.Session.SendAsync(boundary, CancellationToken.None, "NoMonsterReboundBoundary");
        var packets = new List<byte[]>();
        for (var index = 0; index < 16; index++)
        {
            var packet = await socket.ReadPacketAsync();
            if (packet.SequenceEqual(boundary)) return packets;
            packets.Add(packet);
        }
        throw new InvalidOperationException("Unexpected unbounded monster attack publication.");
    }

    private static void AssertNoPacketTargetsMonster(IEnumerable<byte[]> packets, uint monsterId, string label) =>
        Check.True(packets.All(packet => packet.Length != 30 ||
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) != 0x272A ||
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(20)) != monsterId),
            $"{label}: duplicate or canceled delivery never publishes damage against the attacking monster");

    private static void AssertAtlantisHasNoPassiveKills(GameSessionRegistry registry,
        WorldInstanceRuntime runtime, IReadOnlyDictionary<uint, MonsterRuntimeSnapshot> before,
        int preparedRewards, int publishedRewards, string label)
    {
        var after = runtime.Map.SnapshotMonsters();
        Check.True(after.Count == before.Count && after.All(monster =>
                monster.IsAlive && monster.IsSpawned &&
                monster.CurrentHealth == before[monster.ObjectId].CurrentHealth &&
                monster.HealthRevision == before[monster.ObjectId].HealthRevision &&
                monster.SpawnGeneration == before[monster.ObjectId].SpawnGeneration),
            $"{label}: attacking and ambient Atlantis monsters retain exact health and lifecycle state");
        Check.True(preparedRewards == 0 && publishedRewards == 0 &&
            registry.TryGetAtlantisEncounterSnapshot(runtime.InstanceId, out var run) &&
            run.State == AtlantisRunState.Active && run.TeamPoints == 0,
            $"{label}: standing under monster attacks cannot prepare kill rewards or earn Atlantis score");
    }

    private static ulong FindMonsterHitEventId(in MonsterCombatProfile profile, GameCharacter character) =>
        FindMonsterOutcomeEventId(profile, character, 1, hit: true);

    private static ulong FindMonsterOutcomeEventId(in MonsterCombatProfile profile,
        GameCharacter character, ulong firstEvent, bool hit)
    {
        for (var eventId = firstEvent; eventId < firstEvent + 10_000; eventId++)
            if (MonsterIncomingCombatPolicy.ResolveAttack(profile, character, default, eventId).Hit == hit)
                return eventId;
        throw new InvalidOperationException("No deterministic monster outcome was available.");
    }
}
