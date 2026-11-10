using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class IntonedCombatSkillHandlerChecks
{
    public const string SingleTargetMissCheckName =
        "Single-target skill misses finish native casting without health mutation";

    public static async Task RunSingleTargetMissAsync()
    {
        foreach (var mode in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
            await CheckSingleTargetMissAsync(mode);
    }

    private static async Task CheckSingleTargetMissAsync(PlayerRuntimeMode mode)
    {
        // Use real committed target revisions to locate a deterministic pair
        // of misses under the current PvE formula, without replacing its RNG.
        var monster = CreateMonster();
        BinaryPrimitives.WriteUInt32LittleEndian(monster.Packet.AsSpan(12), 10_000);
        await using var fixture = await Fixture.CreateAsync($"StabMiss{mode}",
            playerRuntimeMode: mode, monster: monster);
        fixture.Character.Profession = 1;
        fixture.Character.Level = 135;
        fixture.Character.CalculatedStats = new CharacterStats { PhysicalAttack = 100, MagicAttack = 100, Hit = 0 };
        fixture.Store.Skills = [new() { SkillId = 274, Level = 5 }, new() { SkillId = 530, Level = 1 }];
        fixture.Registry.UpdateCharacter(fixture.Socket.Session, fixture.Character, advanceWorldRevision: false);
        Check.True(GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(274, out var stab) &&
            stab.CastTime == TimeSpan.Zero && SkillCombatResolver.IsHostileMonsterSingleTargetSkill(stab),
            "Stab274 follows the live incident's immediate single-target skill path");
        Check.True(GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(530, out var thunder) &&
            thunder.CastTime > TimeSpan.Zero, "the second skill exercises intoned miss completion too");
        var before = PrimeDeterministicMissTarget(fixture, stab, thunder);

        var nativeCast = new MissNativeCastState();
        await InvokePacketAsync(fixture.Handler, CreateMissSkillPacket(274, fixture.Character)).WaitAsync(TimeSpan.FromSeconds(3));
        await AssertMissSequenceAsync(fixture, nativeCast, 274, InitialMana - stab.Mp, startAlreadyRead: false);
        CheckUnchangedMissTarget(fixture, before, InitialMana - stab.Mp, "immediate Stab");
        Check.Equal(1, fixture.Store.VitalsWrites, "miss consumes and persists its actual mana once");

        Check.True(nativeCast.InputAllowed, "native terminal phase re-enables input before a new client action");
        var walk = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(walk, 20);
        BinaryPrimitives.WriteUInt16LittleEndian(walk.AsSpan(2), Opcodes.Walk);
        BinaryPrimitives.WriteUInt32LittleEndian(walk.AsSpan(4), 0x1448);
        BinaryPrimitives.WriteSingleLittleEndian(walk.AsSpan(8), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(walk.AsSpan(12), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(walk.AsSpan(16), 1f);
        await InvokePacketAsync(fixture.Handler, new GamePacket(walk)).WaitAsync(TimeSpan.FromSeconds(3));
        Check.True(fixture.Character.PositionX == 1f &&
            fixture.Character.PositionZ == 0f && fixture.Store.PositionWrites == 1,
            $"{mode}: a real subsequent move is accepted and persisted without requiring a self echo");

        await InvokePacketAsync(fixture.Handler, CreateMissSkillPacket(530, fixture.Character)).WaitAsync(TimeSpan.FromSeconds(3));
        var start = await fixture.Socket.ReadPacketAsync();
        nativeCast.Observe(start);
        Check.True(ReadOpcode(start) == Opcodes.SkillCast && !nativeCast.InputAllowed,
            "the next learned intoned skill starts normally after the missed Stab and movement");
        await AssertMissSequenceAsync(fixture, nativeCast, 530,
            InitialMana - stab.Mp - thunder.Mp, startAlreadyRead: true);
        CheckUnchangedMissTarget(fixture, before, InitialMana - stab.Mp - thunder.Mp, "intoned Thunder");
        await fixture.Store.WaitForSecondVitalsWriteAsync();
        Check.Equal(2, fixture.Store.VitalsWrites, "both accepted misses persist one mana cost each");
        Check.True(nativeCast.InputAllowed && nativeCast.Starts == 2 && nativeCast.Finishes == 2,
            "intoned completion sends no second cast start and releases the native action latch");
        Check.Equal(0, fixture.Socket.Available, "miss completion sends no damage, healing or duplicate terminal frame");
    }

    private static GamePacket CreateMissSkillPacket(uint skillId, GameCharacter character)
    {
        var packet = CreateSkillCastPacket(character.PositionX, character.PositionZ).Buffer.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), skillId);
        return new(packet);
    }

    private static MonsterRuntimeSnapshot PrimeDeterministicMissTarget(Fixture fixture,
        SkillCombatDefinition stab, SkillCombatDefinition thunder)
    {
        for (var attempt = 0; attempt < 512; attempt++)
        {
            Check.True(fixture.Registry.TryGetMonsterSnapshot(0, MonsterObjectId, out var current),
                "forced miss target remains an exact current-generation monster");
            var target = GameplayContentTestFixtures.Runtime.MonsterCombatProfiles.Resolve(current.Definition).ToTargetStats();
            var misses = new[] { (stab, 1UL), (thunder, 2UL) }.All(pair =>
                !SkillCombatResolver.ResolveDamage(fixture.Character, pair.Item1, target,
                    CombatEventIdentity.ForPlayerMonsterSkill(CharacterId, MonsterObjectId,
                        current.SpawnGeneration, current.HealthRevision, pair.Item2, (uint)pair.Item1.SkillId, 0)).Hit);
            if (misses) return current;
            Check.True(fixture.Registry.TryApplyMonsterDamage(0, MonsterObjectId, 1, CharacterId,
                current.SpawnGeneration, DateTimeOffset.UtcNow, out var primed) && !primed.Killed,
                "fixture advances a real health revision while preserving target life and player combat revisions");
        }
        throw new InvalidOperationException("No deterministic miss pair found within the bounded fixture revisions.");
    }

    private static async Task AssertMissSequenceAsync(Fixture fixture, MissNativeCastState nativeCast,
        uint skillId, int expectedMana, bool startAlreadyRead)
    {
        if (!startAlreadyRead)
        {
            var start = await fixture.Socket.ReadPacketAsync();
            Check.True(start.Length == 40 && ReadOpcode(start) == 10040 &&
                BinaryPrimitives.ReadUInt32LittleEndian(start.AsSpan(8)) == skillId,
                "miss begins with its original native cast visual");
            nativeCast.Observe(start);
        }
        var finish = await fixture.Socket.ReadPacketAsync();
        Check.True(finish.Length == 24 && ReadOpcode(finish) == 10046 &&
            BinaryPrimitives.ReadUInt32LittleEndian(finish.AsSpan(4)) == LocalObjectId &&
            BinaryPrimitives.ReadUInt32LittleEndian(finish.AsSpan(8)) == MonsterObjectId &&
            BinaryPrimitives.ReadUInt32LittleEndian(finish.AsSpan(12)) == skillId,
            "a missed skill must publish native terminal10046, never10045 whose FFFFFFFF means healing");
        nativeCast.Observe(finish);
        var mana = await fixture.Socket.ReadPacketAsync();
        Check.True(mana.Length == 12 && ReadOpcode(mana) == 10135 &&
            BinaryPrimitives.ReadInt32LittleEndian(mana.AsSpan(8)) == expectedMana,
            "the terminal is followed by the exact authoritative mana update");
    }

    private static void CheckUnchangedMissTarget(Fixture fixture, MonsterRuntimeSnapshot before,
        int mana, string label)
    {
        Check.True(fixture.Registry.TryGetMonsterSnapshot(0, MonsterObjectId, out var after) &&
            after.SpawnGeneration == before.SpawnGeneration && after.HealthRevision == before.HealthRevision &&
            after.CurrentHealth == before.CurrentHealth && after.IsAlive,
            $"{label}: miss changes neither target HP nor its authoritative revision");
        Check.Equal(mana, fixture.Character.CurrentMp, $"{label}: mana is spent once despite the miss");
    }

    // Small contract model of the independently audited native action latch:
    // local10040 starts casting; matching10046 advances phase1 and closes it.
    // This is not an executable client integration test or evidence that a
    // fabricated10045 miss sentinel is safe (native interprets it as healing).
    private sealed class MissNativeCastState
    {
        private uint? _skill;
        public bool InputAllowed => _skill is null;
        public int Starts { get; private set; }
        public int Finishes { get; private set; }
        public void Observe(byte[] packet)
        {
            if (ReadOpcode(packet) == 10040)
            {
                Check.True(_skill is null, "native action latch must be free before a new cast starts");
                _skill = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8));
                Starts++;
            }
            else if (ReadOpcode(packet) == 10046)
            {
                Check.True(_skill == BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12)),
                    "native terminal completes the same active skill");
                _skill = null;
                Finishes++;
            }
        }
    }
}
