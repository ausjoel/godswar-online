using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class IntonedCombatSkillHandlerChecks
{
    public const string LifeAbsorptionAreaCheckName =
        "PvE AOE life absorption shows one actual healing tick per damaged monster";

    public static async Task RunLifeAbsorptionAreaAsync()
    {
        Check.True(GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(
                580, out var combat) &&
            SkillCombatResolver.IsHostileMonsterSelfAreaSkill(combat),
            "the per-monster feedback fixture uses the published mage Fire Blast");
        Check.True(GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(574, out var flameBlast) &&
            SkillCombatResolver.IsHostileMonsterGroundAreaSkill(flameBlast),
            "Flame Blast 574 is the distinct published ground-targeted mage skill");
        foreach (var mode in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
        {
            await CheckLifeAbsorptionAreaAsync(mode, combat, 100, 37, [37, 37, 37]);
            await CheckLifeAbsorptionAreaAsync(mode, combat, 410, 37, [37, 37, 16]);
            await CheckLifeAbsorptionAreaAsync(mode, combat, 500, 37, []);
            await CheckLifeAbsorptionAreaAsync(mode, combat, 100, 0, []);
            await CheckLifeAbsorptionAreaAsync(mode, flameBlast, 100, 37, [37, 37, 37]);
            await CheckLifeAbsorptionAreaAsync(mode, flameBlast, 410, 37, [37, 37, 16]);
            await CheckLifeAbsorptionBurstAsync(mode);
        }
    }

    private static async Task CheckLifeAbsorptionAreaAsync(PlayerRuntimeMode mode,
        SkillCombatDefinition combat, int initialHp, int flat, int[]? expectedHealing,
        int percentageBasisPoints = 0)
    {
        var skillId = checked((uint)combat.SkillId);
        var groundTargeted = SkillCombatResolver.IsHostileMonsterGroundAreaSkill(combat);
        var centerX = groundTargeted ? 3.5f : 0f;
        var monsters = Enumerable.Range(0, 4).Select(CreateLifeAbsorptionAreaMonster).ToArray();
        await using var fixture = await Fixture.CreateAsync($"AreaLife{skillId}{mode}{initialHp}{flat}",
            currentHp: initialHp, playerRuntimeMode: mode, lifeAbsorptionFlat: flat, monsters: monsters);
        if (groundTargeted) fixture.Character.Level = 140; // Flame Blast 5 requires Mage level106.
        fixture.Character.CalculatedStats = new CharacterStats
        {
            MagicAttack = 100, Hit = 10_000, LifeAbsorptionFlat = flat,
            LifeAbsorption = percentageBasisPoints
        };
        fixture.Store.Skills = [new() { SkillId = combat.SkillId, Level = groundTargeted ? 5 : 1 }];
        fixture.Registry.UpdateCharacter(fixture.Socket.Session, fixture.Character, advanceWorldRevision: false);
        var before = PrimeLifeAbsorptionAreaOutcomes(fixture, combat);
        await RefreshLifeAbsorptionAreaVisibilityAsync(fixture);

        var cast = CreateSkillCastPacket(0, 0).Buffer.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(cast.AsSpan(8), skillId);
        BinaryPrimitives.WriteUInt32LittleEndian(cast.AsSpan(16), groundTargeted ? uint.MaxValue : LocalObjectId);
        BinaryPrimitives.WriteSingleLittleEndian(cast.AsSpan(32), centerX);
        await InvokePacketAsync(fixture.Handler, new GamePacket(cast));
        var start = await fixture.Socket.ReadPacketAsync(40);
        Check.True(ReadOpcode(start) == 10040 &&
            BinaryPrimitives.ReadUInt32LittleEndian(start.AsSpan(8)) == skillId &&
            BinaryPrimitives.ReadSingleLittleEndian(start.AsSpan(32)) == centerX &&
            BinaryPrimitives.ReadSingleLittleEndian(start.AsSpan(36)) == 0f,
            $"{mode}: native skill {skillId} begins with its correct area center through the game handler");
        var next = await fixture.Socket.ReadPacketAsync();
        var claims = 0;
        while (ReadOpcode(next) == Opcodes.MonsterClaimState)
        {
            Check.True(++claims <= 3 && next.Length == 12 &&
                before.Take(3).Any(monster => monster.ObjectId ==
                    BinaryPrimitives.ReadUInt32LittleEndian(next.AsSpan(4))),
                "only a positively damaged AOE target can emit a new ownership claim");
            next = await fixture.Socket.ReadPacketAsync();
        }
        Check.True(next.Length == 24 && ReadOpcode(next) == 10046 &&
            BinaryPrimitives.ReadUInt32LittleEndian(next.AsSpan(12)) == skillId &&
            BinaryPrimitives.ReadSingleLittleEndian(next.AsSpan(16)) == centerX &&
            BinaryPrimitives.ReadSingleLittleEndian(next.AsSpan(20)) == 0f,
            $"{mode}: AOE impact retains its actual center before damage and healing");
        var cluster = await fixture.Socket.ReadPacketAsync();
        Check.True(cluster.Length == 53 && ReadOpcode(cluster) == 10047 &&
            BinaryPrimitives.ReadInt32LittleEndian(cluster.AsSpan(8)) == 3 &&
            BinaryPrimitives.ReadUInt32LittleEndian(cluster.AsSpan(12)) == skillId,
            $"{mode}: real skill {skillId} commits three hits and excludes its fourth, missed target");
        var committedHits = new List<PveCommittedMonsterDamage>();
        for (var index = 0; index < before.Length; index++)
        {
            Check.True(fixture.Registry.TryGetMonsterSnapshot(0, before[index].ObjectId, out var after),
                "each target remains present after the real AOE");
            var appliedDamage = before[index].CurrentHealth - after.CurrentHealth;
            Check.True(index < 3 ? appliedDamage > 0 && after.IsAlive : appliedDamage == 0,
                $"{mode}: target {index} has the expected actual hit/miss health outcome");
            if (index < 3)
            {
                Check.Equal(before[index].ObjectId,
                    BinaryPrimitives.ReadUInt32LittleEndian(cluster.AsSpan(17 + index * 12)),
                    "the damage cluster preserves the three actual target identities");
            }
            committedHits.Add(new(
                CombatEventIdentity.ForPlayerMonsterSkill(CharacterId, before[index].ObjectId,
                    before[index].SpawnGeneration, before[index].HealthRevision, 1,
                    skillId, index),
                before[index].ObjectId, before[index].SpawnGeneration, appliedDamage));
        }
        if (expectedHealing is null)
        {
            // Independent percentage expectation from observed target HP loss,
            // never from the reported damage or the production healing result.
            var remaining = fixture.Character.MaxHp - initialHp;
            var portions = new List<int>();
            foreach (var hit in committedHits.Where(hit => hit.AppliedDamage > 0))
            {
                var requested = checked((int)((ulong)hit.AppliedDamage *
                    (uint)percentageBasisPoints / 10_000)) + flat;
                var portion = Math.Min(remaining, requested);
                if (portion > 0) portions.Add(portion);
                remaining -= portion;
            }
            expectedHealing = portions.ToArray();
        }
        var mana = await fixture.Socket.ReadPacketAsync(12);
        Check.True(ReadOpcode(mana) == 10135 &&
            BinaryPrimitives.ReadInt32LittleEndian(mana.AsSpan(8)) == InitialMana - combat.Mp,
            "AOE mana remains charged and published once");

        var nativeHp = initialHp;
        foreach (var healing in expectedHealing)
        {
            var packet = await fixture.Socket.ReadPacketAsync(32);
            AssertLifeAbsorptionHealingPacket(packet, healing, 0, 0, $"{mode} per-monster AOE");
            nativeHp -= BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(16));
        }
        if (expectedHealing.Length > 0)
        {
            var vitals = await fixture.Socket.ReadPacketAsync(16);
            Check.True(ReadOpcode(vitals) == 10097 &&
                BinaryPrimitives.ReadUInt32LittleEndian(vitals.AsSpan(4)) == LocalObjectId &&
                BinaryPrimitives.ReadInt32LittleEndian(vitals.AsSpan(8)) == nativeHp &&
                BinaryPrimitives.ReadInt32LittleEndian(vitals.AsSpan(12)) == InitialMana - combat.Mp,
                "one authoritative vitals packet follows all per-monster ticks without doubling HP");
        }
        Check.Equal(initialHp + expectedHealing.Sum(), fixture.Character.CurrentHp,
            "three per-target heals preserve the single authoritative aggregate HP mutation");
        await fixture.Store.WaitForVitalsWriteAsync();
        Check.Equal(1, fixture.Store.VitalsWrites, "AOE damage, mana and per-target healing persist once");
        await AssertLifeAbsorptionAreaReplayAsync(fixture, committedHits);
        await Task.Delay(50);
        Check.Equal(0, fixture.Socket.Available,
            "no combined extra tick, missed-target tick, full-HP/zero-source tick or replay feedback remains");
    }

    private static CapturedMonsterSpawn CreateLifeAbsorptionAreaMonster(int index)
    {
        var monster = CreateMonster();
        var id = MonsterObjectId + checked((uint)index);
        var x = 2f + index;
        BinaryPrimitives.WriteUInt32LittleEndian(monster.Packet.AsSpan(8), id);
        BinaryPrimitives.WriteSingleLittleEndian(monster.Packet.AsSpan(28), x);
        return monster with { ObjectId = id, X = x };
    }

    private static MonsterRuntimeSnapshot[] PrimeLifeAbsorptionAreaOutcomes(
        Fixture fixture, SkillCombatDefinition combat)
    {
        var result = new MonsterRuntimeSnapshot[4];
        for (var index = 0; index < result.Length; index++)
        {
            var id = MonsterObjectId + checked((uint)index);
            for (var attempt = 0; attempt < 512; attempt++)
            {
                Check.True(fixture.Registry.TryGetMonsterSnapshot(0, id, out var current),
                    "the AOE fixture primes existing authoritative target revisions");
                var target = GameplayContentTestFixtures.Runtime.MonsterCombatProfiles
                    .Resolve(current.Definition).ToTargetStats();
                var eventId = CombatEventIdentity.ForPlayerMonsterSkill(CharacterId, id,
                    current.SpawnGeneration, current.HealthRevision, 1, checked((uint)combat.SkillId), index);
                var resolution = SkillCombatResolver.ResolveDamage(
                    fixture.Character, combat, target, eventId, index);
                if (resolution.Hit == (index < 3))
                {
                    result[index] = current;
                    break;
                }
                Check.True(fixture.Registry.TryApplyMonsterDamage(0, id, 1, CharacterId,
                    current.SpawnGeneration, DateTimeOffset.UtcNow, out var primed) && !primed.Killed,
                    "priming changes a real health revision without admitting a player combat action");
                if (attempt == 511)
                    throw new InvalidOperationException("No bounded deterministic AOE hit/miss revision found.");
            }
        }
        return result;
    }

    private static async Task RefreshLifeAbsorptionAreaVisibilityAsync(Fixture fixture)
    {
        // Rehydrate after test-only priming so the real AOE starts from matching
        // viewer health revisions and exercises direct damage delivery.
        foreach (var coordinate in new[] { 200f, 0f })
        {
            await using var transition = await fixture.Registry.BeginMonsterVisibilityTransitionAsync(
                fixture.Socket.Session, 0, coordinate, coordinate, CancellationToken.None)
                ?? throw new InvalidOperationException("AOE fixture visibility is unavailable.");
            transition.Commit();
        }
    }

    private static async Task AssertLifeAbsorptionAreaReplayAsync(
        Fixture fixture, IReadOnlyList<PveCommittedMonsterDamage> committedHits)
    {
        var commitMethod = typeof(GameClientHandler).GetMethod("CommitPveLifeAbsorption",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Shared life-absorption committer is missing.");
        var beforeHp = fixture.Character.CurrentHp;
        var replay = (PveLifeAbsorptionCommit)(commitMethod.Invoke(fixture.Handler,
            [fixture.Character, committedHits])
            ?? throw new InvalidOperationException("AOE replay returned no receipt."));
        Check.True(!replay.Applied && replay.ClaimedHitCount == 0 &&
            fixture.Character.CurrentHp == beforeHp,
            "all three actual AOE hit keys are already claimed; the zero-damage missed target is ignored");
        var publishMethod = typeof(GameClientHandler).GetMethod("PublishPveLifeAbsorptionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Shared life-absorption publisher is missing.");
        await ((Task?)publishMethod.Invoke(fixture.Handler,
            [fixture.Character, replay, CancellationToken.None, false])
            ?? throw new InvalidOperationException("AOE replay publication returned no task."));
    }
}
