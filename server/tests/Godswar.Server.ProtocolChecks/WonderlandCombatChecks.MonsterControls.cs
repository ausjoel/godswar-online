using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    public const string MonsterControlsCheckName = "Wonderland boss control skills and queued attack interruption";
    public static async Task RunMonsterControlsAsync()
    {
        foreach (var monsters in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        foreach (var players in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
        {
            foreach (var skill in new[] { 74, 354, 604, 794 })
                await CheckBossControlHandlerAsync(monsters, players, skill);
            await CheckQueuedBossControlAsync(monsters, players);
            await CheckMonsterControlProjectionAsync(monsters, players);
            await CheckMonsterDebuffTimersAsync(monsters, players);
        }
    }

    private static async Task CheckBossControlHandlerAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players, int skill)
    {
        await using var f = await Fixture.CreateAsync(1, monsters, players);
        Check.True(MonsterControlSkillPolicy.TryGet(skill, out var definition), "native boss control is supported");
        f.Character.Profession = definition.RequiredProfession; f.Character.Level = 140;
        var boss = f.Monster("alpha"); f.MoveTo(boss);
        await using (var view = await f.Registry.BeginMonsterVisibilityTransitionAsync(f.Session, 207,
            f.Character.PositionX, f.Character.PositionZ, CancellationToken.None)) view!.Commit();
        var store = new MonsterControlStore(skill);
        var handler = CreateMonsterControlHandler(f, store);
        var before = f.Transport.ReadLegacyPackets().Count;
        var request = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(request, 40);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), Opcodes.SkillCast);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), 0x1448);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), (uint)skill);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16), boss.ObjectId);
        BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(24), f.Character.PositionX);
        BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(28), f.Character.PositionZ);
        BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(32), boss.X);
        BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(36), boss.Z);
        try
        {
            await (Task)typeof(GameClientHandler).GetMethod("HandlePacketAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(handler, [new GamePacket(request), CancellationToken.None])!;
            await store.Written.Task.WaitAsync(TimeSpan.FromSeconds(4));
            await f.FlushAsync();
            var current = f.Monster("alpha");
            var now = DateTimeOffset.UtcNow;
            Check.True(current.Controls.Active(now).Any(entry => entry.Definition.StatusId == definition.StatusId),
                "real learned class control lands on the actual admitted Wonderland boss");
            Check.True(current.CurrentHealth == boss.CurrentHealth && current.HealthRevision == boss.HealthRevision,
                "control skill never falls through to damage or creates health revisions");
            Check.True(current.CastInterruptionRevision > boss.CastInterruptionRevision,
                "a real accepted cast invalidates previously queued boss intonation");
            Check.Equal(1000 - definition.ManaCost, f.Character.CurrentMp, "real control consumes its native mana once");
            Check.Equal(1, store.Writes, "control persists exactly one authoritative mana mutation");
            var packets = f.Transport.ReadLegacyPackets().Skip(before).ToArray();
            ushort Op(byte[] p) => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2));
            Check.True(packets.Count(p => Op(p) == Opcodes.SkillCast) == 1 &&
                packets.Count(p => Op(p) == 10046) == 1 && packets.All(p => Op(p) != 10045),
                "native control emits one start and terminal, with no damage/heal packet");
            var status = packets.Single(p => Op(p) == 10167);
            Check.True(BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(4)) == boss.ObjectId &&
                BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(12)) == definition.StatusId,
                "native status projection targets the correct boss and native control ID");
            Check.True(!f.Registry.TryCommitPlayerMonsterControl(f.Session, current with { SpawnGeneration = current.SpawnGeneration + 1 },
                CaptureAuthority(), definition, now, out _), "new control cannot target an old/replaced boss generation");
            var reduced = f.Registry.AdjustPveMonsterTargetStats(f.Session, current, now, default);
            Check.True(reduced.PhysicalDamageReductionBasisPoints == (skill == 794 ? 7500 : 0) &&
                reduced.MagicDamageReductionBasisPoints == (skill == 794 ? 7500 : 0),
                "native Caged preserves its seventy-five percent reduction without inventing mitigation for other controls");
            if (skill is 604 or 794)
            {
                var lateStart = f.Transport.ReadLegacyPackets().Count;
                var manaBefore = f.Character.CurrentMp;
                await (Task)typeof(GameClientHandler).GetMethod("HandleSkillCastAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(handler,
                    [new GamePacket(request), CancellationToken.None, true, current.SpawnGeneration + 1, null])!;
                await f.FlushAsync();
                var late = f.Transport.ReadLegacyPackets().Skip(lateStart).ToArray();
                Check.True(late.Count(p => Op(p) == 10046) == 1 &&
                    late.All(p => Op(p) is not (10040 or 10045)) && f.Character.CurrentMp == manaBefore && store.Writes == 1,
                    "a claimed intonation with a vanished generation still terminates locally without damage, charge or new cast");
            }
        }
        finally
        {
            await (Task)typeof(GameClientHandler).GetMethod("StopPendingSkillCastsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(handler, null)!;
        }
        PlayerMonsterCombatAuthority CaptureAuthority()
        {
            Check.True(f.Registry.TryCapturePlayerMonsterTarget(f.Session, 207, boss.ObjectId, out _, out var authority),
                "boss test captures current player membership and life");
            return authority;
        }
    }

    private static async Task CheckQueuedBossControlAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        await using var f = await Fixture.CreateAsync(1, monsters, players);
        f.Character.Profession = 0; f.MoveTo(f.Monster("alpha"));
        var emitted = new List<MonsterRuntimeUpdate>();
        for (var i = 0; i < 4; i++) emitted.AddRange(MonsterOwnerCheckSteps.Advance(f.Runtime, f.Registry,
            f.Now.AddMilliseconds(i * 100)).Updates);
        var attack = emitted.FirstOrDefault(update => update.Kind == MonsterRuntimeUpdateKind.Attacked &&
            update.Monster.ObjectId == f.Monster("alpha").ObjectId);
        Check.True(attack is not null, "real boss simulation emits an attack before it is paused for dispatch");
        MonsterControlSkillPolicy.TryGet(74, out var stun);
        var at = f.Now.AddMilliseconds(500);
        Check.True(f.Registry.TryCapturePlayerMonsterTarget(f.Session, 207, attack!.Monster.ObjectId, out var target, out var authority) &&
            f.Registry.TryCommitPlayerMonsterControl(f.Session, target, authority, stun, at, out var result),
            "a later valid control interrupts the emitted attack before target HP commit");
        var before = f.Character.CurrentHp;
        var process = typeof(GameSessionRegistry).GetMethod("ProcessMonsterAttackAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)process.Invoke(f.Registry, [f.Runtime, attack, CancellationToken.None, at.AddSeconds(4)])!;
        Check.Equal(before, f.Character.CurrentHp, "expired stun still cancels the old queued attack by its interruption revision");
        var expiry = at.AddSeconds(3);
        var resumed = new List<MonsterRuntimeUpdate>();
        for (var i = 0; i < 5; i++) resumed.AddRange(MonsterOwnerCheckSteps.Advance(f.Runtime, f.Registry,
            expiry.AddMilliseconds(i * 100)).Updates);
        var fresh = resumed.FirstOrDefault(update => update.Kind == MonsterRuntimeUpdateKind.Attacked &&
            update.Monster.ObjectId == target.ObjectId);
        Check.True(fresh is not null && fresh.Monster.AttackInterruptionRevision > attack.Monster.AttackInterruptionRevision,
            "new attacks resume with a fresh control fence, rather than restoring the old queue");
        var profile = f.Registry.AdjustPveMonsterAttackerProfile(f.Session, fresh!.Monster, expiry.AddSeconds(1),
            f.Registry.GameplayCatalogs.MonsterCombatProfiles.Resolve(fresh.Monster.Definition));
        ulong eventId = 1;
        while (!MonsterIncomingCombatPolicy.ResolveAttack(profile, f.Character, default, eventId).Hit) eventId++;
        fresh = fresh with { AttackEventId = eventId };
        await (Task)process.Invoke(f.Registry, [f.Runtime, fresh!, CancellationToken.None, expiry.AddSeconds(1)])!;
        Check.True(f.Character.CurrentHp < before, "fresh post-expiry boss attack commits real damage");
    }

    private static GameClientHandler CreateMonsterControlHandler(Fixture f, MonsterControlStore store)
    {
        var handler = new GameClientHandler(f.Session, store, f.Registry,
            CharacterSnapshotReaderTestFixtures.Unused, WorldContentReaderTestFixtures.Empty);
        Set("_account", new AccountIdentity(f.Character.AccountId, "boss-controls"));
        Set("_character", f.Character); Set("_registered", true); Set("_worldPresenceAnnounced", true);
        return handler;
        void Set(string field, object value) => typeof(GameClientHandler).GetField(field,
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(handler, value);
    }

    private sealed class MonsterControlStore(int skill) : GameStoreTestStub
    {
        public TaskCompletionSource<bool> Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Writes { get; private set; }
        public override Task<IReadOnlyList<SkillState>> GetSkillStatesAsync(int accountId, int characterId,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SkillState>>([new() { SkillId = skill, Level = 5 }]);
        public override Task SaveCharacterVitalsAsync(int accountId, int characterId, int currentHp, int currentMp,
            long vitalsRevision, CancellationToken cancellationToken = default)
        { Writes++; Written.TrySetResult(true); return Task.CompletedTask; }
    }
}
