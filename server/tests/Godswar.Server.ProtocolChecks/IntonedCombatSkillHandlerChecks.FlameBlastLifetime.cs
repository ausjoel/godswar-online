using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class IntonedCombatSkillHandlerChecks
{
    public const string FlameBlastLifetimeCheckName =
        "Flame Blast fields stop on stale authority and reject unpaid placement";

    public static async Task RunFlameBlastLifetimeAsync()
    {
        foreach (var mode in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
        {
            await CheckEmptyFlameAndManaAsync(mode);
            await CheckFlameTargetsEnterAndLeaveAsync(mode);
            foreach (var invalidation in new[] { "death", "new-life", "map", "disconnect", "stop" })
                await CheckFlameInvalidationAsync(mode, invalidation);
            await CheckOverdueFlameAsync(mode);
        }
    }

    private static async Task CheckEmptyFlameAndManaAsync(PlayerRuntimeMode mode)
    {
        var clock = NewFlameClock();
        await using var fixture = await CreateFlameFixtureAsync(mode, clock);
        fixture.Character.CurrentMp = 179;
        await InvokePacketAsync(fixture.Handler, CreateFlameCast(fixture, 3));
        // Reuse the exact three-packet rejection contract; its monster-health
        // assertion belongs to the older fixture, so verify these frames here.
        var error = await fixture.Socket.ReadPacketAsync(12);
        var mana = await fixture.Socket.ReadPacketAsync(12);
        var interrupt = await fixture.Socket.ReadPacketAsync(8);
        Check.True(error.SequenceEqual(PacketBuilder.LocalizedError(NativeErrorCodes.InsufficientMana)) &&
            ReadOpcode(mana) == 10135 &&
            BinaryPrimitives.ReadUInt32LittleEndian(mana.AsSpan(4)) == LocalObjectId &&
            BinaryPrimitives.ReadInt32LittleEndian(mana.AsSpan(8)) == 179 &&
            interrupt.SequenceEqual(PacketBuilder.SkillCastInterrupt(LocalObjectId)) &&
            FlameFieldCount(fixture.Handler) == 0 &&
            clock.ScheduledTimerCount == 0,
            "insufficient MP emits the normal rejection and cannot create a lasting field");
        fixture.Character.CurrentMp = InitialMana;
        fixture.Character.PositionX = -3;
        fixture.Registry.UpdateCharacter(fixture.Socket.Session, fixture.Character,
            advanceWorldRevision: false);
        var hits = await CastAndReadInitialFlameAsync(fixture, centerX: -3);
        Check.Equal(0, hits, "a valid empty ground placement initially hits no monsters");
        await WaitForFlameTimersAsync(fixture, clock, 1);
        CheckFlameEventDomains(fixture);
        var pulsesBefore = fixture.Handler.FlameBlastPulsesApplied;
        for (var ordinal = 1; ordinal <= 10; ordinal++)
        {
            var pulse = await AdvanceAndReadFlameAsync(fixture, clock,
                TimeSpan.FromSeconds(1));
            Check.True(pulse.Targets == 0 && pulse.Damage == 0,
                "an empty field survives its earlier empty pulses without fictitious healing");
        }
        await WaitForFlameRetirementAsync(fixture, clock);
        Check.Equal(9, fixture.Handler.FlameBlastPulsesApplied - pulsesBefore,
            "an empty field still delivers every scheduled pass before it retires");
        Check.Equal(InitialMana - 180, fixture.Character.CurrentMp,
            "empty placement is paid once, independent of target acquisition");
    }

    private static async Task CheckFlameInvalidationAsync(PlayerRuntimeMode mode, string invalidation)
    {
        var clock = NewFlameClock();
        await using var fixture = await CreateFlameFixtureAsync(mode, clock);
        await CastAndReadInitialFlameAsync(fixture, 3);
        await WaitForFlameTimersAsync(fixture, clock, 1);
        var before = FlameMonsterHealth(fixture);
        switch (invalidation)
        {
            case "death":
                lock (fixture.Character.VitalsSync)
                {
                    fixture.Character.CurrentHp = 0;
                    fixture.Character.MarkVitalsChanged();
                }
                break;
            case "new-life":
                fixture.Registry.AdvancePlayerLifeRevision(fixture.Socket.Session);
                lock (fixture.Character.VitalsSync)
                {
                    fixture.Character.CurrentHp = 100;
                    fixture.Character.MarkVitalsChanged();
                }
                break;
            case "map":
                fixture.Character.CurrentMap = 1;
                fixture.Registry.UpdateCharacter(fixture.Socket.Session, fixture.Character);
                break;
            case "disconnect":
                fixture.Socket.Session.Disconnect();
                break;
            case "stop":
                await StopFlameFieldsAsync(fixture.Handler);
                await StopFlameFieldsAsync(fixture.Handler);
                break;
        }
        var beforeHp = fixture.Character.CurrentHp;
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForFlameTimersAsync(fixture, clock, 0);
        Check.True(before.SequenceEqual(FlameMonsterHealth(fixture)) &&
            fixture.Character.CurrentHp == beforeHp && fixture.Socket.Available == 0,
            $"{mode}: {invalidation} prevents old-field damage, healing and stale packets");
    }

    private static async Task CheckOverdueFlameAsync(PlayerRuntimeMode mode)
    {
        var clock = NewFlameClock();
        await using var fixture = await CreateFlameFixtureAsync(mode, clock);
        await CastAndReadInitialFlameAsync(fixture, 3);
        await WaitForFlameTimersAsync(fixture, clock, 1);
        var before = FlameMonsterHealth(fixture);
        clock.Advance(TimeSpan.FromSeconds(11));
        await WaitForFlameTimersAsync(fixture, clock, 0);
        Check.True(before.SequenceEqual(FlameMonsterHealth(fixture)) && fixture.Socket.Available == 0,
            "a stalled handler cannot replay all expired ground damage in one catch-up burst");
    }
}
