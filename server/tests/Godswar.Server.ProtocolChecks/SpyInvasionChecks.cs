using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Monsters;

namespace Godswar.Server.ProtocolChecks;

// SPIES: capital-city spies appear only in invasion waves (Mon/Wed/Sat 19:00
// Manila = 11:00 UTC, six 5-minute waves) when event.spy_invasion is 1.
internal static class SpyInvasionChecks
{
    public const string CheckName = "Spy invasion waves";

    private const string SpyKey = "A_normals_greecewarrior_003";
    private const uint SpyId = 10501;

    // 2026-11-02 is a Monday.
    private static DateTimeOffset Utc(int day, int hour, int minute, int second = 0) =>
        new(2026, 11, day, hour, minute, second, TimeSpan.Zero);

    public static Task RunAsync()
    {
        try
        {
            // FINALISE2 (the admin F-04): the policy reads the realm clock. These checks keep their Manila expectations by
            // pinning a Manila realm, then CheckAest proves the live realm (Australia/Brisbane) moves the waves to AEST.
            Godswar.Server.Application.Gates.RealmClock.Calendar =
                Godswar.Server.Application.Realms.RealmCalendar.CreateForTesting(Godswar.Server.Domain.World.Instances.RealmId.Tempest, "Asia/Manila");
            CheckPolicy();
            CheckRuntime();
            CheckDisabledKeepsSpiesPermanent();
            CheckAest();
        }
        finally
        {
            Godswar.Server.Application.Gates.RealmClock.Calendar = null;
            ServerRates.ResetToDefaults();
        }

        return Task.CompletedTask;
    }

    private static void Install(decimal value) =>
        ServerRates.Install(ServerRatesSnapshot.Create(
            [KeyValuePair.Create(ServerRateKeys.SpyInvasion, value)],
            1, DateTimeOffset.UtcNow, "check", "test"));

    private static void CheckPolicy()
    {
        Check.True(!SpyInvasionPolicy.IsActive(Utc(2, 10, 59, 59)), "closed just before 19:00 Monday");
        Check.True(SpyInvasionPolicy.IsActive(Utc(2, 11, 0)), "open at 19:00 Monday");
        Check.True(SpyInvasionPolicy.IsActive(Utc(2, 11, 29, 59)), "open until 19:30");
        Check.True(!SpyInvasionPolicy.IsActive(Utc(2, 11, 30)), "closed at 19:30");
        Check.True(!SpyInvasionPolicy.IsActive(Utc(3, 11, 0)), "closed on Tuesday");
        Check.True(SpyInvasionPolicy.IsActive(Utc(4, 11, 10)), "open on Wednesday");
        Check.True(SpyInvasionPolicy.IsActive(Utc(7, 11, 10)), "open on Saturday");
        Check.Equal(Utc(2, 11, 5), SpyInvasionPolicy.NextWaveStart(Utc(2, 11, 1)), "next wave after 19:01");
        Check.Equal(Utc(2, 11, 25), SpyInvasionPolicy.NextWaveStart(Utc(2, 11, 20)), "last wave is 19:25");
        Check.Equal(Utc(4, 11, 0), SpyInvasionPolicy.NextWaveStart(Utc(2, 11, 25)), "after the last wave comes Wednesday");
        Check.Equal(Utc(2, 11, 0), SpyInvasionPolicy.NextWaveStart(Utc(1, 12, 0)), "Sunday waits for Monday");
        Check.Equal(Utc(9, 11, 0), SpyInvasionPolicy.NextWaveStart(Utc(7, 11, 26)), "after Saturday comes Monday");
        Check.True(SpyInvasionPolicy.IsSpy(1, SpyKey) && SpyInvasionPolicy.IsSpy(0, "A_normalsB_greecewarrior_002"), "spy keys");
        Check.True(!SpyInvasionPolicy.IsSpy(8, SpyKey) && !SpyInvasionPolicy.IsSpy(1, "A_normal_stub_001"), "other maps and monsters are not spies");
    }

    private static void CheckAest()
    {
        Godswar.Server.Application.Gates.RealmClock.Calendar =
            Godswar.Server.Application.Realms.RealmCalendar.CreateForTesting(Godswar.Server.Domain.World.Instances.RealmId.Tempest, "Australia/Brisbane");
        // 19:00 AEST = 09:00 UTC.
        Check.True(!SpyInvasionPolicy.IsActive(Utc(2, 8, 59, 59)), "AEST: closed just before 19:00 Monday AEST");
        Check.True(SpyInvasionPolicy.IsActive(Utc(2, 9, 0)), "AEST: open at 19:00 Monday AEST (09:00 UTC)");
        Check.True(!SpyInvasionPolicy.IsActive(Utc(2, 11, 0)), "AEST: the old Manila 19:00 (21:00 AEST) is closed");
        Check.Equal(Utc(2, 9, 5), SpyInvasionPolicy.NextWaveStart(Utc(2, 9, 1)), "AEST: next wave 19:05 AEST");
        Check.Equal(Utc(4, 9, 0), SpyInvasionPolicy.NextWaveStart(Utc(2, 9, 26)), "AEST: then Wednesday 19:00 AEST");
    }

    private static void CheckRuntime()
    {
        Install(1m);
        var runtime = new EcsMonsterMapRuntime(1, [CreateSpy()], Utc(1, 12, 0));
        MonsterRuntimeSnapshot Spy() => runtime.Snapshot().Single();

        runtime.Advance(Utc(1, 12, 0, 1));
        Check.True(!Spy().IsSpawned && !Spy().IsAlive, "spy is withdrawn outside a window");

        runtime.Advance(Utc(2, 10, 59, 59));
        Check.True(!Spy().IsSpawned, "still withdrawn at 18:59:59");

        var tick = runtime.Advance(Utc(2, 11, 0, 1));
        Check.True(Spy().IsSpawned && Spy().IsAlive, "first wave at 19:00");
        Check.True(tick.Updates.Any(u => u.Kind == MonsterRuntimeUpdateKind.Respawned), "wave announces a respawn");

        Check.True(runtime.TryApplyDamage(SpyId, uint.MaxValue, 7, Utc(2, 11, 1), out var kill) && kill.Killed, "spy can be killed");
        Check.Equal(Utc(2, 11, 5), Spy().RespawnAt!.Value, "killed spy waits for the 19:05 wave");
        runtime.Advance(Utc(2, 11, 3));
        runtime.Advance(Utc(2, 11, 3, 1));
        Check.True(!Spy().IsSpawned && !Spy().IsAlive, "still gone at 19:03");
        runtime.Advance(Utc(2, 11, 5, 1));
        Check.True(Spy().IsSpawned && Spy().IsAlive, "back with the 19:05 wave");

        Check.True(runtime.TryApplyDamage(SpyId, uint.MaxValue, 7, Utc(2, 11, 26), out _), "late kill");
        Check.Equal(Utc(4, 11, 0), Spy().RespawnAt!.Value, "kill after the last wave waits for the next event");
        runtime.Advance(Utc(2, 11, 40));
        runtime.Advance(Utc(2, 11, 40, 1));
        Check.True(!Spy().IsSpawned, "no return after the window");
        runtime.Advance(Utc(4, 11, 0, 1));
        Check.True(Spy().IsSpawned, "back at the next event");

        var tick2 = runtime.Advance(Utc(4, 11, 30, 1));
        Check.True(!Spy().IsSpawned && !Spy().IsAlive, "survivor leaves at 19:30");
        Check.True(
            tick2.Updates.Any(u => u.Kind == MonsterRuntimeUpdateKind.Despawned) &&
            tick2.Updates.All(u => u.Kind != MonsterRuntimeUpdateKind.Died),
            "leaving is a despawn, not a kill");
        Check.True(
            runtime.TryApplyDamage(SpyId, 5, 7, Utc(4, 11, 31), out var late) && !late.Killed,
            "a withdrawn spy cannot be killed for loot");
    }

    private static void CheckDisabledKeepsSpiesPermanent()
    {
        ServerRates.ResetToDefaults();
        var runtime = new EcsMonsterMapRuntime(1, [CreateSpy()], Utc(1, 12, 0));
        runtime.Advance(Utc(1, 12, 5));
        Check.True(runtime.Snapshot().Single().IsSpawned, "stock default leaves spies in the city all day");
        Check.True(runtime.TryApplyDamage(SpyId, uint.MaxValue, 7, Utc(1, 12, 6), out _), "kill at stock");
        Check.True(
            runtime.Snapshot().Single().RespawnAt!.Value - Utc(1, 12, 6) == MonsterMapRuntime.DefaultRespawnDelay,
            "stock respawn delay untouched");
    }

    private static CapturedMonsterSpawn CreateSpy()
    {
        var packet = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 10020);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), 0x00000212);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), SpyId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), 141);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20, 4), 237);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(24, 4), 237);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(28, 4), 100f);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(32, 4), 2f);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(36, 4), 50f);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(40, 4), 1f);
        Encoding.ASCII.GetBytes(SpyKey).CopyTo(packet.AsSpan(44));
        return new CapturedMonsterSpawn(1, "Athens", SpyKey, "Spartan Spy", SpyId, 100f, 50f, packet);
    }
}
