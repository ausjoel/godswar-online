using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string AtlantisCompletionUiCheckName =
        "Atlantis completion countdown, settled reward gate, and retryable exact-member exit";

    public static async Task RunAtlantisCompletionUiAsync()
    {
        await CheckAtlantisCompletionCountdownAsync(partySize: 1);
        await CheckAtlantisCompletionCountdownAsync(partySize: 2);
        await CheckAtlantisCompletionManualExitGateAsync();
        await CheckAtlantisTerminalAdmissionAsync(AtlantisRunState.Completed);
        await CheckAtlantisTerminalAdmissionAsync(AtlantisRunState.TimedOut);
    }

    private static async Task CheckAtlantisCompletionCountdownAsync(int partySize)
    {
        await using var fixture = await CreateAtlantisOpalFixtureAsync(null, null, partySize: partySize);
        var leader = fixture.Leader;
        var registry = leader.Registry;
        registry.RegisterAuthoritativeInstanceTransitionSink(leader.Session,
            (command, token) => InvokeAuthoritativeTransitionAsync(leader.Handler, command, token));
        try
        {
            var (runtime, initial) = await PrepareAtlantisDepartureAsync(fixture);
            var completed = await CompleteAtlantisScoreAsync(fixture, runtime, initial);
            var completedAt = completed.TerminalAt!.Value;
            var beforeCompletion = AtlantisPacketCounts(fixture);
            var delayed = CaptureAtlantisDepartureDelivery(registry, runtime, completedAt.AddSeconds(5));
            await PublishAtlantisCompletionUiCheckAsync(registry, delayed, rewardsSettled: false);
            AssertAtlantisCompletionCountdown(fixture, beforeCompletion, remaining: 25);
            Check.True(fixture.Characters.All(character => character.CurrentMap == 205),
                "the countdown result does not transfer anyone before reward settlement");

            var afterCompletion = AtlantisPacketCounts(fixture);
            await PublishAtlantisCompletionUiCheckAsync(registry,
                CaptureAtlantisDepartureDelivery(registry, runtime, completedAt.AddSeconds(10)), false);
            Check.True(fixture.ReadAllPackets().Select((packets, index) =>
                    packets.Skip(afterCompletion[index]).All(packet => !IsAtlantisPanelPacket(packet)))
                .All(value => value),
                "later deliveries neither restart the native countdown nor restore Terminate");

            await PublishAtlantisCompletionUiCheckAsync(registry,
                CaptureAtlantisDepartureDelivery(registry, runtime, completedAt.AddSeconds(30).AddTicks(-1)), true);
            Check.True(runtime.Map.Population == partySize &&
                fixture.Characters.All(character => character.CurrentMap == 205),
                "settled rewards still retain members until the exact thirty-second boundary");
            var due = CaptureAtlantisDepartureDelivery(registry, runtime, completedAt.AddSeconds(30));
            await PublishAtlantisCompletionUiCheckAsync(registry, due, false);
            Check.Equal(partySize, runtime.Map.Population,
                "the elapsed countdown cannot bypass a pending reward settlement");

            var attempts = 0;
            if (fixture.Followers.FirstOrDefault() is { } follower)
            {
                registry.UnregisterAuthoritativeInstanceTransitionSink(follower.Session);
                registry.RegisterAuthoritativeInstanceTransitionSink(follower.Session, (command, token) =>
                    (++attempts) switch
                    {
                        1 => Task.FromResult(false),
                        2 => Task.FromException<bool>(new IOException("completion exit temporarily unavailable")),
                        _ => InvokeAuthoritativeTransitionAsync(follower.Handler, command, token)
                    });
            }
            await PublishAtlantisCompletionUiCheckAsync(registry, due, true);
            if (partySize > 1)
            {
                Check.True(attempts == 1 && runtime.Map.Population == 1 &&
                    registry.TryGetWorldInstance(runtime.InstanceId, out _),
                    "a rejected follower transfer preserves its exact completed instance for retry");
                await PublishAtlantisCompletionUiCheckAsync(registry,
                    CaptureAtlantisDepartureDelivery(registry, runtime, completedAt.AddSeconds(31)), true);
                Check.True(attempts == 2 && runtime.Map.Population == 1,
                    "a transfer exception leaves the same member available for another retry");
                await PublishAtlantisCompletionUiCheckAsync(registry,
                    CaptureAtlantisDepartureDelivery(registry, runtime, completedAt.AddSeconds(32)), true);
                Check.Equal(3, attempts, "only the untransferred member is retried until successful");
            }
            Check.True(runtime.Map.Population == 0 && fixture.Characters.All(character =>
                    character.CurrentMap == (character.Camp == GameDefaults.SpartaCamp
                        ? GameDefaults.SpartaCapitalMap : GameDefaults.AthensCapitalMap)),
                "completion auto-exits every current member to the established faction capital");
            foreach (var (packets, index) in fixture.ReadAllPackets().Select((packets, index) => (packets, index)))
            {
                var emitted = packets.Skip(afterCompletion[index]).ToArray();
                Check.True(emitted.Count(IsAtlantisDepartureClear) == 1 &&
                    emitted.Count(packet => ReadOpcode(packet) == Opcodes.SceneChange) == 1,
                    "successful membership commit clears the countdown and changes scene exactly once");
            }
            var afterExit = AtlantisPacketCounts(fixture);
            await PublishAtlantisCompletionUiCheckAsync(registry, due, true);
            Check.True(fixture.ReadAllPackets().Select((packets, index) =>
                    packets.Skip(afterExit[index]).All(packet => !IsAtlantisPanelPacket(packet) &&
                        ReadOpcode(packet) != Opcodes.SceneChange)).All(value => value),
                "an old completed delivery cannot reopen UI or transfer departed players twice");
            Check.True(runtime.Map.TryGetAtlantisRunSnapshot(out var final) && final == completed,
                "exit timing uses the observed world clock while the authoritative completion stays frozen");
            Check.Equal(0, AtlantisRegistryCacheCount(registry, "_atlantisCompletionEgressInFlight"),
                "all exit attempts release their concurrency guard");
        }
        finally
        {
            registry.UnregisterAuthoritativeInstanceTransitionSink(leader.Session);
        }
    }

    private static async Task CheckAtlantisCompletionManualExitGateAsync()
    {
        await using var fixture = await CreateAtlantisOpalFixtureAsync(null, null, partySize: 1);
        var leader = fixture.Leader;
        var registry = leader.Registry;
        registry.RegisterAuthoritativeInstanceTransitionSink(leader.Session,
            (command, token) => InvokeAuthoritativeTransitionAsync(leader.Handler, command, token));
        try
        {
            var (runtime, initial) = await PrepareAtlantisDepartureAsync(fixture);
            var completed = await CompleteAtlantisScoreAsync(fixture, runtime, initial);
            var now = completed.TerminalAt!.Value.AddSeconds(1);
            Check.True(registry.TryEndAtlantisRunFromLeader(leader.Session, 224, 0, now),
                "the owned leader can request an early completed-instance exit");
            var delivery = CaptureAtlantisDepartureDelivery(registry, runtime, now);
            var legacyExit = typeof(GameSessionRegistry).GetMethod("PublishAtlantisTerminationEgressAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)legacyExit.Invoke(registry, [delivery, CancellationToken.None])!;
            await PublishAtlantisCompletionUiCheckAsync(registry, delivery, false);
            Check.True(runtime.Map.Population == 1 && leader.Character.CurrentMap == 205,
                "neither manual completion nor the cancellation egress bypasses unsettled rewards");
            await PublishAtlantisCompletionUiCheckAsync(registry, delivery, true);
            // The leader's own native Leave is the only early exit: it is the
            // shared clicker-only flow, so he is carried home at once while any
            // teammate keeps his own countdown.
            await InvokeAsync(leader.Handler, CreateRepetitionPanelAction(0, 0));
            Check.True(runtime.Map.Population == 0 && leader.Character.CurrentMap != 205,
                "once rewards settle, the completed run's own Leave carries its clicker " +
                "out before the automatic deadline");
        }
        finally
        {
            registry.UnregisterAuthoritativeInstanceTransitionSink(leader.Session);
        }
    }

    private static async Task PublishAtlantisCompletionUiCheckAsync(
        GameSessionRegistry registry, object delivery, bool rewardsSettled)
    {
        var method = typeof(GameSessionRegistry).GetMethod("PublishAtlantisCompletionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(registry, [delivery, rewardsSettled, CancellationToken.None])!;
    }

    private static void AssertAtlantisCompletionCountdown(
        AtlantisOpalFixture fixture, int[] before, int remaining)
    {
        foreach (var (packets, index) in fixture.ReadAllPackets().Select((packets, index) => (packets, index)))
        {
            var emitted = packets.Skip(before[index]).Where(IsAtlantisPanelPacket).ToArray();
            // The four end-of-run frames are the shared flow's; it reasserts the
            // native run state (10232 state 5) in front of them, exactly as
            // 飘渺幻境 and 港湾遇袭 do, so the countdown can open at all.
            var frames = emitted.Where(packet => ReadOpcode(packet) is
                Opcodes.RepetitionFightInfo or Opcodes.RepetitionPanelAction or
                Opcodes.RepetitionCompletionState or Opcodes.RepetitionReset).ToArray();
            Check.True(frames.Select(ReadOpcode).SequenceEqual(new[]
                {
                    Opcodes.RepetitionFightInfo, Opcodes.RepetitionPanelAction,
                    Opcodes.RepetitionCompletionState, Opcodes.RepetitionReset
                }) &&
                emitted[^1].SequenceEqual(frames[^1]),
                "completion uses Medusa's native result then countdown packet order");
            var countdown = frames[^1];
            Check.True(countdown.SequenceEqual(PacketBuilder.RepetitionCountdown(remaining)) &&
                BinaryPrimitives.ReadInt32LittleEndian(frames[0].AsSpan(4)) == remaining &&
                BinaryPrimitives.ReadInt32LittleEndian(frames[0].AsSpan(16)) == 850,
                "the countdown uses completion age, with the authoritative final score preserved");
        }
    }
}
