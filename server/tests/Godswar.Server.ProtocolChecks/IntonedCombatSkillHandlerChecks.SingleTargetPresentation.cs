using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class IntonedCombatSkillHandlerChecks
{
    public const string SingleTargetPresentationCheckName =
        "Single-target skill normal and critical numbers preserve caster, observer, and lethal outcomes";

    public static async Task RunSingleTargetPresentationAsync()
    {
        Check.True(GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(274, out var skill) &&
            skill.CastTime == TimeSpan.Zero && SkillCombatResolver.IsHostileMonsterSingleTargetSkill(skill),
            "presentation check uses the real immediate Stab V single-target handler");
        foreach (var mode in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
        foreach (var outcome in new[] { CombatHitOutcome.Normal, CombatHitOutcome.Critical })
        foreach (var lethal in new[] { false, true })
            await CheckSingleTargetPresentationAsync(mode, skill, outcome, lethal);
    }

    private static async Task CheckSingleTargetPresentationAsync(PlayerRuntimeMode mode,
        SkillCombatDefinition skill, CombatHitOutcome outcome, bool lethal)
    {
        var initialHealth = lethal ? 2_000u : 100_000u;
        var (monster, expected) = FindSingleTargetPresentationMonster(skill, outcome, initialHealth);
        Check.Equal(lethal, expected.Damage >= initialHealth,
            "presentation fixture independently distinguishes surviving and lethal damage");
        await using var fixture = await Fixture.CreateAsync($"Number{mode}{outcome}{lethal}",
            playerRuntimeMode: mode, monster: monster);
        fixture.Character.Profession = 1;
        fixture.Character.Level = 135;
        fixture.Character.CalculatedStats = SingleTargetPresentationStats();
        fixture.Store.Skills = [new() { SkillId = skill.SkillId, Level = 5 }];
        fixture.Registry.UpdateCharacter(fixture.Socket.Session, fixture.Character,
            advanceWorldRevision: false);

        await using var observer = await RuntimePolicySessionSocket.CreateAsync();
        var observerCharacter = new GameCharacter
        {
            Id = CharacterId + 1, AccountId = AccountId + 1, Name = "SkillNumberViewer",
            CreatedUtc = DateTime.UtcNow, Camp = GameDefaults.SpartaCamp, CurrentMap = 0,
            PositionX = 0, PositionZ = 0, Profession = 3, Level = 135,
            CurrentHp = 500, MaxHp = 500, CurrentMp = 500, MaxMp = 500
        };
        fixture.Registry.JoinMap(observer.Session, observerCharacter.AccountId,
            observerCharacter, WorldObjectIds.ForPlayer(observerCharacter.Id), worldReady: true);
        try
        {
            await using (var visible = await fixture.Registry.BeginMonsterVisibilityTransitionAsync(
                observer.Session, 0, 0, 0, CancellationToken.None)
                ?? throw new InvalidOperationException("Skill-number observer visibility is unavailable."))
                visible.Commit();

            var cast = CreateSkillCastPacket(0, 0).Buffer.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(cast.AsSpan(8), checked((uint)skill.SkillId));
            BinaryPrimitives.WriteUInt32LittleEndian(cast.AsSpan(16), monster.ObjectId);
            await InvokePacketAsync(fixture.Handler, new GamePacket(cast))
                .WaitAsync(TimeSpan.FromSeconds(5));

            var selfDamage = await ReadSingleTargetPresentationDamageAsync(fixture.Socket);
            var worldDamage = await ReadSingleTargetPresentationDamageAsync(observer);
            AssertSingleTargetPresentationPacket(selfDamage, LocalObjectId, monster.ObjectId,
                skill.SkillId, expected, $"{mode} caster {outcome} lethal={lethal}");
            AssertSingleTargetPresentationPacket(worldDamage, WorldObjectIds.ForPlayer(CharacterId),
                monster.ObjectId, skill.SkillId, expected, $"{mode} observer {outcome} lethal={lethal}");
            Check.True(fixture.Registry.TryGetMonsterSnapshot(0, monster.ObjectId, out var after),
                "the committed target remains available for authoritative lifecycle validation");
            Check.Equal(initialHealth - Math.Min(initialHealth, expected.Damage), after.CurrentHealth,
                "number styling never changes applied HP damage or overkill handling");
            Check.Equal(!lethal, after.IsAlive,
                "normal and critical lethal skills preserve the dead-monster lifecycle without flag5");
            Check.Equal(InitialMana - skill.Mp, fixture.Character.CurrentMp,
                "single-target number presentation preserves exactly one mana charge");
        }
        finally
        {
            fixture.Registry.Remove(observer.Session);
        }
    }

    private static CharacterStats SingleTargetPresentationStats() => new()
    {
        PhysicalAttack = 1_000, MagicAttack = 1_000,
        Hit = 1_000_000, Critical = 1_000_000
    };

    private static (CapturedMonsterSpawn Monster, CombatResolution Resolution)
        FindSingleTargetPresentationMonster(SkillCombatDefinition skill,
            CombatHitOutcome wanted, uint health)
    {
        var character = new GameCharacter
        {
            Id = CharacterId, Profession = 1, Level = 135,
            CalculatedStats = SingleTargetPresentationStats()
        };
        // Choose a valid object identity before the real cast. Neither the RNG
        // nor the live target's HP/revision is replaced or modified for a roll.
        for (var index = 0u; index < 128; index++)
        {
            var monster = CreateMonster() with { ObjectId = MonsterObjectId + index };
            BinaryPrimitives.WriteUInt32LittleEndian(monster.Packet.AsSpan(8), monster.ObjectId);
            BinaryPrimitives.WriteUInt32LittleEndian(monster.Packet.AsSpan(20), health);
            BinaryPrimitives.WriteUInt32LittleEndian(monster.Packet.AsSpan(24), health);
            var target = GameplayContentTestFixtures.Runtime.MonsterCombatProfiles
                .Resolve(monster).ToTargetStats();
            var eventId = CombatEventIdentity.ForPlayerMonsterSkill(CharacterId, monster.ObjectId,
                spawnGeneration: 1, healthRevision: 0, admittedCombatRevision: 1,
                skillId: checked((uint)skill.SkillId), targetOrder: 0);
            var resolution = SkillCombatResolver.ResolveDamage(character, skill, target, eventId);
            if (resolution.Outcome == wanted) return (monster, resolution);
        }
        throw new InvalidOperationException("No deterministic skill-number fixture outcome found.");
    }

    private static async Task<byte[]> ReadSingleTargetPresentationDamageAsync(RuntimePolicySessionSocket socket)
    {
        for (var index = 0; index < 24; index++)
        {
            var packet = await socket.ReadPacketAsync().WaitAsync(TimeSpan.FromSeconds(3));
            if (ReadOpcode(packet) == 10045) return packet;
        }
        throw new InvalidOperationException("A single-target damage number was not published.");
    }

    private static void AssertSingleTargetPresentationPacket(byte[] packet, uint casterId,
        uint targetId, int skillId, CombatResolution expected, string description)
    {
        Check.True(packet.Length == 32 && ReadOpcode(packet) == 10045 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == casterId &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)) == targetId &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(20)) == checked((uint)skillId),
            $"{description}: native skill damage retains its recipient-specific identities");
        Check.Equal((uint)expected.Outcome, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12)),
            $"{description}: low byte is actual normal1/critical0 and upper HP-resource bytes stay zero");
        Check.Equal(expected.CapturedDamageValue, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(16)),
            $"{description}: displayed damage is the unchanged authoritative resolution");
    }
}
