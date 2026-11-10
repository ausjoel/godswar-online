using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class IntonedCombatSkillHandlerChecks
{
    public const string LifeAbsorptionFeedbackCheckName =
        "PvE life absorption shows only actual committed healing";

    public static async Task RunLifeAbsorptionFeedbackAsync()
    {
        Check.True(GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(
            checked((int)ThunderSkillId), out var combat), "Thunder is published");
        await CheckLifeAbsorptionCompletionAsync(combat);
        foreach (var mode in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
        {
            await CheckLifeAbsorptionSkillFeedbackAsync(mode, 400, 0, 37);
            await CheckLifeAbsorptionSkillFeedbackAsync(mode, 100, 1_000, 7);
            await CheckLifeAbsorptionSkillFeedbackAsync(mode, 500, 0, 37);
            await CheckLifeAbsorptionSkillFeedbackAsync(mode, 400, 0, 0);
            await CheckLifeAbsorptionBasicFeedbackAsync(mode);
            await CheckLifeAbsorptionRejectedCommitFeedbackAsync(mode);
        }
    }

    private static async Task CheckLifeAbsorptionSkillFeedbackAsync(
        PlayerRuntimeMode mode, int initialHp, int percent, int flat)
    {
        await using var fixture = await Fixture.CreateAsync($"LifeText{mode}{initialHp}{percent}{flat}",
            currentHp: initialHp, lifeAbsorption: percent, playerRuntimeMode: mode,
            lifeAbsorptionFlat: flat);
        var beforeMonsterHp = fixture.CurrentMonsterHealth();
        await fixture.BeginCastAsync();
        await AssertMonsterClaimAsync(fixture);
        var damage = await fixture.Socket.ReadPacketAsync(32);
        var impact = await fixture.Socket.ReadPacketAsync(24);
        var mana = await fixture.Socket.ReadPacketAsync(12);
        Check.True(ReadOpcode(damage) == 10045 && ReadOpcode(impact) == 10046 &&
            ReadOpcode(mana) == 10135, $"{mode}: primary skill sequence remains intact");
        var actualDamage = beforeMonsterHp - fixture.CurrentMonsterHealth();
        Check.True(actualDamage > 0, $"{mode}: feedback fixture lands real authoritative damage");
        var requested = checked((int)(actualDamage * (uint)percent / 10_000)) + flat;
        var applied = Math.Min(requested, 500 - initialHp);
        Check.Equal(initialHp + applied, fixture.Character.CurrentHp,
            $"{mode}: flat and percentage life absorption use actual damage and missing HP");
        if (applied > 0)
            await AssertLifeAbsorptionFeedbackAsync(fixture, initialHp, applied, "skill");
        await fixture.Store.WaitForVitalsWriteAsync();
        await Task.Delay(50);
        Check.Equal(1, fixture.Store.VitalsWrites,
            $"{mode}: skill damage, mana and healing share one persisted vitals update");
        Check.Equal(0, fixture.Socket.Available,
            $"{mode}: no duplicate, zero or full-health healing number/vitals packet");
    }

    private static async Task CheckLifeAbsorptionBasicFeedbackAsync(PlayerRuntimeMode mode)
    {
        await using var fixture = await Fixture.CreateAsync($"LifeBasic{mode}",
            currentHp: 400, playerRuntimeMode: mode, lifeAbsorptionFlat: 37);
        fixture.Character.CalculatedStats = new CharacterStats
        {
            PhysicalAttack = 100, MagicAttack = 100, Hit = 10_000,
            BasicAttackRange = 10f, LifeAbsorptionFlat = 37
        };
        fixture.Registry.UpdateCharacter(fixture.Socket.Session, fixture.Character,
            advanceWorldRevision: false);
        var packet = new byte[32];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 32);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), Opcodes.BasicAttack);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), LocalObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20), MonsterObjectId);
        var beforeMonsterHp = fixture.CurrentMonsterHealth();
        await InvokePacketAsync(fixture.Handler, new GamePacket(packet));
        await AssertMonsterClaimAsync(fixture);
        var damage = await fixture.Socket.ReadPacketAsync();
        Check.True(ReadOpcode(damage) == 10026 && fixture.CurrentMonsterHealth() < beforeMonsterHp,
            $"{mode}: a native basic attack commits real damage before healing feedback");
        await AssertLifeAbsorptionFeedbackAsync(fixture, 400, 37, "basic attack");
        await fixture.Store.WaitForVitalsWriteAsync();
        var afterMonsterHp = fixture.CurrentMonsterHealth();
        await InvokePacketAsync(fixture.Handler, new GamePacket(packet));
        await Task.Delay(50);
        Check.True(fixture.CurrentMonsterHealth() == afterMonsterHp &&
            fixture.Character.CurrentHp == 437 && fixture.Store.VitalsWrites == 1 &&
            fixture.Socket.Available == 0,
            $"{mode}: duplicate native input rejected by cooldown cannot add healing or a second number");
    }

    private static async Task CheckLifeAbsorptionRejectedCommitFeedbackAsync(PlayerRuntimeMode mode)
    {
        await using var fixture = await Fixture.CreateAsync($"LifeReplay{mode}",
            currentHp: 400, playerRuntimeMode: mode, lifeAbsorptionFlat: 37);
        var commitMethod = typeof(GameClientHandler).GetMethod("CommitPveLifeAbsorption",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The shared life-absorption committer is missing.");
        var publishMethod = typeof(GameClientHandler).GetMethod("PublishPveLifeAbsorptionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The shared life-absorption publisher is missing.");
        PveLifeAbsorptionCommit Commit(PveCommittedMonsterDamage hit) =>
            (PveLifeAbsorptionCommit)(commitMethod.Invoke(fixture.Handler,
                [fixture.Character, new[] { hit }])
                ?? throw new InvalidOperationException("Life-absorption commit returned null."));
        async Task Publish(PveLifeAbsorptionCommit commit)
        {
            var task = publishMethod.Invoke(fixture.Handler,
                [fixture.Character, commit, CancellationToken.None, false]) as Task
                ?? throw new InvalidOperationException("Life-absorption publication returned no task.");
            await task;
        }
        var key = new PveCommittedMonsterDamage(123_456, MonsterObjectId, 1, 11);
        var first = Commit(key);
        Check.True(first.AppliedHealing == 37 && first.ClaimedHitCount == 1,
            $"{mode}: actual shared committer claims a successful direct hit once");
        await Publish(first);
        await AssertLifeAbsorptionFeedbackAsync(fixture, 400, 37, "committed-hit publication");
        var replay = Commit(key);
        var zero = Commit(key with { CombatEventId = 123_457, AppliedDamage = 0 });
        Check.True(!replay.Applied && replay.ClaimedHitCount == 0 && !zero.Applied,
            $"{mode}: replay and zero damage have no applied healing to present");
        await Publish(replay);
        await Publish(zero);
        var previousLife = Commit(key with { CombatEventId = 123_458 });
        Check.True(previousLife.Applied && previousLife.SourceContext is not null,
            $"{mode}: delayed healing captures its authoritative source membership");
        var newLife = fixture.Registry.AdvancePlayerLifeRevision(fixture.Socket.Session);
        Check.True(newLife > previousLife.SourceLifeRevision &&
            !fixture.Registry.IsCurrentPveLifeAbsorption(previousLife),
            $"{mode}: a new life invalidates an already committed healing presentation");
        await Publish(previousLife);
        var previousWorld = Commit(key with { CombatEventId = 123_459 });
        Check.True(previousWorld.Applied, $"{mode}: the current life can commit another distinct heal");
        fixture.Character.CurrentMap = 1;
        fixture.Registry.UpdateCharacter(fixture.Socket.Session, fixture.Character);
        Check.True(!fixture.Registry.IsCurrentPveLifeAbsorption(previousWorld),
            $"{mode}: transfer invalidates the old world's delayed healing presentation");
        await Publish(previousWorld);
        lock (fixture.Character.VitalsSync)
        {
            fixture.Character.CurrentHp = 0;
            fixture.Character.MarkVitalsChanged();
        }
        var dead = Commit(key with { CombatEventId = 123_460 });
        Check.True(!dead.Applied && fixture.Character.CurrentHp == 0,
            $"{mode}: life absorption cannot resurrect a dead attacker");
        await Publish(dead);
        await Task.Delay(50);
        Check.Equal(0, fixture.Socket.Available,
            $"{mode}: replay, zero damage, stale life/world and dead sources emit no healing number");
    }

    private static async Task AssertLifeAbsorptionFeedbackAsync(
        Fixture fixture, int initialHp, int applied, string action)
    {
        var healing = await fixture.Socket.ReadPacketAsync(32);
        var vitals = await fixture.Socket.ReadPacketAsync(16);
        AssertLifeAbsorptionHealingPacket(healing, applied,
            fixture.Character.PositionX, fixture.Character.PositionZ, action);
        Check.True(ReadOpcode(vitals) == 10097 &&
            BinaryPrimitives.ReadUInt32LittleEndian(vitals.AsSpan(4)) == LocalObjectId &&
            BinaryPrimitives.ReadInt32LittleEndian(vitals.AsSpan(8)) == fixture.Character.CurrentHp &&
            BinaryPrimitives.ReadInt32LittleEndian(vitals.AsSpan(12)) == fixture.Character.CurrentMp,
            $"{action}: final authoritative vitals follow the native healing number");
        Check.Equal(initialHp + applied, ApplyNativeLifeAbsorptionFeedback(initialHp, healing, vitals),
            $"{action}: native healing plus final projection leaves exactly the committed HP");
    }

    private static void AssertLifeAbsorptionHealingPacket(
        byte[] packet, int applied, float x, float z, string action)
    {
        Check.True(packet.Length == 32 && ReadOpcode(packet) == 10045 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == LocalObjectId &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)) == LocalObjectId &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12)) == 0x101 &&
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(16)) == -applied &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(20)) == 0 &&
            BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(24)) == x &&
            BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(28)) == z,
            $"{action}: exact self-heal wire fields show the actual positive healing amount");
    }

    private static int ApplyNativeLifeAbsorptionFeedback(int initialHp, byte[] healing, byte[] vitals)
    {
        // 10045 subtracts its signed damage (a negative healing amount). 10097
        // then replaces native HP, so the projection must follow the number.
        var signedDamage = BinaryPrimitives.ReadInt32LittleEndian(healing.AsSpan(16));
        Check.True(signedDamage < 0, "healing combat text uses native negative damage");
        var afterNumber = checked(initialHp - signedDamage);
        var afterProjection = BinaryPrimitives.ReadInt32LittleEndian(vitals.AsSpan(8));
        Check.Equal(afterNumber, afterProjection,
            "native number and authoritative projection describe the same committed heal");
        return afterProjection;
    }
}
