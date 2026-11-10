using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class IntonedCombatSkillHandlerChecks
{
    public const string FlameBlastPulsesCheckName =
        "Flame Blast fields retain per-monster healing and independent pulses";
    private const uint FlameSkillId = 574;
    private const int FlameHealing = 7;

    public static async Task RunFlameBlastPulsesAsync()
    {
        foreach (var mode in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
        {
            await CheckFlameBlastLethalOpeningHitAsync(mode);
            await CheckFlameBlastLethalPulseKeepsDamageNumberAsync(mode);
            await CheckFlameBlastTenSecondFieldAsync(mode);
            await CheckFlameBlastIndependentFieldsAsync(mode);
            await CheckSharedFlameFormulaAsync(mode);
        }
    }

    // A recurring pass that kills its target must still publish its number
    // before the reconciliation removal, exactly like the opening hit.
    private static async Task CheckFlameBlastLethalPulseKeepsDamageNumberAsync(
        PlayerRuntimeMode mode)
    {
        var clock = NewFlameClock();
        await using var fixture = await CreateFlameFixtureAsync(mode, clock,
            monsterHealth: 500);
        fixture.Character.CalculatedStats = new CharacterStats
        {
            MagicAttack = 500, Hit = 1_000_000, LifeAbsorptionFlat = FlameHealing
        };
        fixture.Registry.UpdateCharacter(fixture.Socket.Session, fixture.Character,
            advanceWorldRevision: false);
        await BeginFlameCastAsync(fixture, 3);
        await WaitForFlameTimersAsync(fixture, clock, 1);
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForFlameQuiescenceAsync(fixture, clock);
        var damageIndex = -1;
        var removalIndex = -1;
        for (var index = 0; index < 8; index++)
        {
            var packet = await fixture.Socket.ReadPacketAsync();
            var opcode = ReadOpcode(packet);
            if (opcode == 10045 && damageIndex < 0) damageIndex = index;
            if (packet.Length >= 12 && opcode == 0x2728 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)) == MonsterObjectId &&
                removalIndex < 0) removalIndex = index;
        }

        Check.True(damageIndex >= 0,
            $"{mode}: a lethal recurring pass still publishes its damage number");
        Check.True(removalIndex < 0 || damageIndex < removalIndex,
            $"{mode}: a lethal recurring pass publishes its number before the removal");
    }

    private static uint ReadMonsterGeneration(Fixture fixture) =>
        fixture.Registry.TryGetMonsterSnapshot(0, MonsterObjectId, out var monster)
            ? monster.SpawnGeneration
            : throw new InvalidOperationException(
                "lethal pulse priming reads the real monster generation");

    // A lethal opening hit retires the monster's viewer object. The damage
    // number has to be admitted while the client still has the monster, or the
    // removal that follows swallows the hit that killed it.
    private static async Task CheckFlameBlastLethalOpeningHitAsync(PlayerRuntimeMode mode)
    {
        var clock = NewFlameClock();
        await using var fixture = await CreateFlameFixtureAsync(mode, clock,
            monsterHealth: 500);
        fixture.Character.CalculatedStats = new CharacterStats
        {
            MagicAttack = 5_000, Hit = 1_000_000, LifeAbsorptionFlat = FlameHealing
        };
        fixture.Registry.UpdateCharacter(fixture.Socket.Session, fixture.Character,
            advanceWorldRevision: false);
        var before = FlameMonsterHealth(fixture);
        await BeginFlameCastAsync(fixture, 3);
        var damageIndex = -1;
        var removalIndex = -1;
        for (var index = 0; index < 8 && (damageIndex < 0 || removalIndex < 0); index++)
        {
            var packet = await fixture.Socket.ReadPacketAsync();
            var opcode = ReadOpcode(packet);
            if (opcode == 10047 && damageIndex < 0) damageIndex = index;
            if (packet.Length >= 8 && opcode == 0x2728 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)) == MonsterObjectId &&
                removalIndex < 0) removalIndex = index;
        }

        Check.True(FlameMonsterHealth(fixture)[0] < before[0],
            $"{mode}: the lethal opening hit really committed damage");
        Check.True(damageIndex >= 0,
            $"{mode}: the lethal opening hit still publishes its damage number");
        Check.True(removalIndex < 0 || damageIndex < removalIndex,
            $"{mode}: the removal of the killed monster never precedes its damage number");
    }

    private static async Task CheckFlameBlastTenSecondFieldAsync(PlayerRuntimeMode mode)
    {
        var clock = NewFlameClock();
        await using var fixture = await CreateFlameFixtureAsync(mode, clock);
        var totals = await CastAndReadInitialFlameAsync(fixture, centerX: 3);
        await WaitForFlameTimersAsync(fixture, clock, 1);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Check.Equal(0, fixture.Socket.Available, "Flame Blast cannot damage before its first one-second deadline");

        // Placement belongs to the ground point, not the caster's later location.
        fixture.Character.PositionX = 20;
        fixture.Registry.UpdateCharacter(fixture.Socket.Session, fixture.Character,
            advanceWorldRevision: true);
        var pulsesBefore = fixture.Handler.FlameBlastPulsesApplied;
        totals += (await AdvanceAndReadFlameAsync(fixture, clock,
            TimeSpan.FromMilliseconds(1))).Targets;
        for (var step = 0; step < 8; step++)
            totals += (await AdvanceAndReadFlameAsync(fixture, clock,
                TimeSpan.FromSeconds(1))).Targets;

        var log = fixture.Handler.FlameBlastPulseLog;
        Check.Equal(9, fixture.Handler.FlameBlastPulsesApplied - pulsesBefore,
            "the ten-second field delivers one recurring pass per second before it retires");
        Check.True(log.Select(entry => entry.Ordinal).SequenceEqual(Enumerable.Range(1, 9)) &&
            log.Zip(log.Skip(1)).All(pair =>
                pair.Second.ElapsedTicks - pair.First.ElapsedTicks ==
                FlameBlastPulsePolicy.Interval.Ticks),
            "recurring passes land one interval apart from the accepted cast");
        await WaitForFlameRetirementAsync(fixture, clock);

        Check.True(totals >= 5, $"{mode}: several real target hits survive all ten damage passes");
        Check.Equal(50 + totals * FlameHealing, fixture.Character.CurrentHp,
            $"{mode}: each damaged monster contributes one actual heal across the field lifetime");
        Check.Equal(InitialMana - 180, fixture.Character.CurrentMp,
            "later pulses neither reserve nor charge the initial mana cost again");
        var finalHealth = FlameMonsterHealth(fixture);
        clock.Advance(TimeSpan.FromSeconds(11));
        await Task.Yield();
        Check.True(finalHealth.SequenceEqual(FlameMonsterHealth(fixture)) &&
            fixture.Socket.Available == 0 && FlameFieldCount(fixture.Handler) == 0,
            "ten total pulses expire without an eleventh damage pass or residual field");
    }

    private static async Task CheckFlameBlastIndependentFieldsAsync(PlayerRuntimeMode mode)
    {
        var clock = NewFlameClock();
        await using var fixture = await CreateFlameFixtureAsync(mode, clock);
        var totals = await CastAndReadInitialFlameAsync(fixture, 3);
        await WaitForFlameTimersAsync(fixture, clock, 1);
        var firstFieldMana = fixture.Character.CurrentMp;

        // Drive the first field one pass at a time so the second cast lands
        // while it is demonstrably still ticking on its own schedule.
        var firstLog = fixture.Handler.FlameBlastPulseLog;
        await PumpFlameClockAsync(fixture, clock, TimeSpan.FromSeconds(2));
        var firstFieldPasses = fixture.Handler.FlameBlastPulseLog.Count - firstLog.Count;
        Check.Equal(2, firstFieldPasses,
            $"{mode}: the first field keeps its own one-pass-per-second schedule");

        // The second accepted cast must own a second field with its own clock
        // rather than refreshing, restarting or merging into the first one.
        fixture.Registry.PruneHostileSkillCooldowns(DateTimeOffset.UtcNow.AddSeconds(3));
        await BeginSecondFlameCastAsync(fixture, 3);
        await PumpFlameClockAsync(fixture, clock, TimeSpan.FromSeconds(9));
        Check.Equal(InitialMana - 360, fixture.Character.CurrentMp,
            "each accepted cast pays its own mana once and no field pass charges again");

        var log = fixture.Handler.FlameBlastPulseLog;
        var fieldIds = log.Select(entry => entry.FieldId).Distinct().Order().ToArray();
        Check.Equal(2, fieldIds.Length,
            "two accepted casts own two independent fields");
        foreach (var fieldId in fieldIds)
        {
            var entries = log.Where(entry => entry.FieldId == fieldId)
                .OrderBy(entry => entry.Ordinal).ToArray();
            Check.True(entries.Select(entry => entry.Ordinal).SequenceEqual(
                    Enumerable.Range(1, entries.Length)),
                $"{mode}: field{fieldId} never restarts or skips its own ordinals");
            Check.True(entries.Length == 9,
                $"{mode}: field{fieldId} delivers its own nine recurring passes");
            Check.True(entries.Zip(entries.Skip(1)).All(pair =>
                    pair.Second.ElapsedTicks - pair.First.ElapsedTicks ==
                    FlameBlastPulsePolicy.Interval.Ticks),
                $"{mode}: field{fieldId} keeps its own one-second cadence");
        }

        // The log is chronological, so the second field's first pass must come
        // after the first field's first pass: the later cast appended a new
        // schedule instead of taking over the ticking one.
        var firstAppearance = fieldIds.ToDictionary(fieldId => fieldId,
            fieldId => log.ToList().FindIndex(entry => entry.FieldId == fieldId));
        Check.True(firstAppearance[fieldIds[1]] > firstAppearance[fieldIds[0]],
            "the second field starts its own schedule instead of refreshing the first");
        Check.True(FlameFieldCount(fixture.Handler) == 0 && clock.ScheduledTimerCount == 0,
            "both fields retire independently without residual timers");
        Check.True(totals >= 2 && firstFieldMana == InitialMana - 180,
            $"{mode}: both initial casts commit damage before their recurring passes");
    }

    private static async Task<int> CastAndReadInitialFlameAsync(Fixture fixture, float centerX,
        int expectedManaCost = 180, IReadOnlyCollection<uint>? expectedDamageValues = null,
        Func<uint, byte>? expectedStyle = null)
    {
        var before = FlameMonsterHealth(fixture);
        var beforeHp = fixture.Character.CurrentHp;
        var beforeMana = fixture.Character.CurrentMp;
        await BeginFlameCastAsync(fixture, centerX);
        var impact = await ReadAfterFlameClaimsAsync(fixture);
        Check.True(impact.Length == 24 && ReadOpcode(impact) == 10046 &&
            BinaryPrimitives.ReadUInt32LittleEndian(impact.AsSpan(12)) == FlameSkillId &&
            BinaryPrimitives.ReadSingleLittleEndian(impact.AsSpan(16)) == centerX &&
            BinaryPrimitives.ReadSingleLittleEndian(impact.AsSpan(20)) == 0,
            "the accepted ground cast retains its original574 impact and fixed ground center");
        var cluster = await fixture.Socket.ReadPacketAsync();
        var after = FlameMonsterHealth(fixture);
        var changed = ChangedFlameTargets(before, after);
        Check.True(ReadOpcode(cluster) == 10047 && cluster.Length == 17 + 12 * changed.Length &&
            BinaryPrimitives.ReadInt32LittleEndian(cluster.AsSpan(8)) == changed.Length &&
            BinaryPrimitives.ReadUInt32LittleEndian(cluster.AsSpan(12)) == FlameSkillId,
            "initial cast keeps its ordinary574 cluster damage, including an empty selected field");
        for (var index = 0; index < changed.Length; index++)
        {
            Check.Equal(MonsterObjectId + (uint)changed[index],
                BinaryPrimitives.ReadUInt32LittleEndian(cluster.AsSpan(17 + index * 12)),
                "initial cluster contains exactly the monsters with committed damage");
            if (expectedDamageValues is not null)
            {
                var appliedDamage = before[changed[index]] - after[changed[index]];
                Check.True(expectedDamageValues.Contains(appliedDamage),
                    "accepted initial Flame damage uses the projected normal or critical skill outcome");
                Check.Equal(appliedDamage,
                    BinaryPrimitives.ReadUInt32LittleEndian(cluster.AsSpan(25 + index * 12)),
                    "initial native cluster reports the same skill damage committed to HP");
                if (expectedStyle is not null)
                    Check.Equal(expectedStyle(appliedDamage), cluster[21 + index * 12],
                        "initial Flame number style matches that target's actual normal or critical damage");
            }
        }
        var mana = await fixture.Socket.ReadPacketAsync(12);
        Check.True(ReadOpcode(mana) == 10135 &&
            BinaryPrimitives.ReadInt32LittleEndian(mana.AsSpan(8)) == beforeMana - expectedManaCost,
            "the initial574 cast consumes its projected MP exactly once");
        await ReadFlameHealingAsync(fixture, changed.Length, beforeHp);
        return changed.Length;
    }

    // Advances the field clock by one scheduled step. Returns the number of
    // targets whose health was actually mutated (each contributes one heal
    // packet) and the total damage committed. A single advance can deliver
    // more than one pass under the manually driven clock.
    private static async Task<(int Targets, int Damage)> AdvanceAndReadFlameAsync(
        Fixture fixture, ManualTimeProvider clock, TimeSpan advance,
        Func<uint, byte>? expectedStyle = null)
    {
        var before = FlameMonsterHealth(fixture);
        var beforeHp = fixture.Character.CurrentHp;
        var beforeMana = fixture.Character.CurrentMp;
        clock.Advance(advance);
        await WaitForFlameQuiescenceAsync(fixture, clock);
        var after = FlameMonsterHealth(fixture);
        var changed = ChangedFlameTargets(before, after);
        var damage = changed.Sum(index => checked((int)(before[index] - after[index])));
        if (changed.Length > 0)
        {
            var packet = await ReadAfterFlameClaimsAsync(fixture);
            for (var index = 0; index < changed.Length; index++)
            {
                if (index > 0) packet = await fixture.Socket.ReadPacketAsync(32);
                var target = changed[index];
                Check.True(packet.Length == 32 && ReadOpcode(packet) == 10045 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == LocalObjectId &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)) == MonsterObjectId + (uint)target &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12)) is 0 or 1 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(20)) == 250 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(16)) == before[target] - after[target],
                    "each pulse uses the captured250 damage envelope for exactly one actual target mutation");
                if (expectedStyle is not null)
                    Check.Equal((uint)expectedStyle(before[target] - after[target]),
                        BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12)),
                        "recurring Flame number style matches that target's actual normal or critical damage");
            }
            await ReadFlameHealingAsync(fixture, changed.Length, beforeHp);
        }
        Check.True(after[2] == before[2] && fixture.Character.CurrentMp == beforeMana &&
            fixture.Socket.Available == 0,
            "a pulse excludes the out-of-area mob and sends no recast, impact, mana or extra heal");
        return (changed.Length, damage);
    }

    private static int[] ChangedFlameTargets(uint[] before, uint[] after) =>
        Enumerable.Range(0, before.Length).Where(index => after[index] < before[index]).ToArray();

    private static async Task ReadFlameHealingAsync(Fixture fixture, int hits, int beforeHp)
    {
        for (var index = 0; index < hits; index++)
            AssertLifeAbsorptionHealingPacket(await fixture.Socket.ReadPacketAsync(32),
                FlameHealing, fixture.Character.PositionX, fixture.Character.PositionZ,
                "Flame Blast per-target healing");
        if (hits > 0)
        {
            var vitals = await fixture.Socket.ReadPacketAsync(16);
            Check.True(ReadOpcode(vitals) == 10097 &&
                BinaryPrimitives.ReadInt32LittleEndian(vitals.AsSpan(8)) == beforeHp + hits * FlameHealing &&
                BinaryPrimitives.ReadInt32LittleEndian(vitals.AsSpan(12)) == fixture.Character.CurrentMp,
                "one final HP/MP snapshot follows all actual per-target healing numbers");
        }
        Check.Equal(beforeHp + hits * FlameHealing, fixture.Character.CurrentHp,
            "pulse healing equals the sum of damaged-mob contributions");
    }
}
