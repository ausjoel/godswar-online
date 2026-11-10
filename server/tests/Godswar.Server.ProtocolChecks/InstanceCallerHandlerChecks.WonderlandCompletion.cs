using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string WonderlandCompletionCheckName =
        "Wonderland final island requires four bosses and chest guard and waits for durable titles before countdown exit";

    public static async Task RunWonderlandCompletionAsync()
    {
        foreach (var partySize in new[] { 1, 2 })
        {
            await using var fixture = await CreateWonderlandHandlerFixtureAsync(partySize);
            var party = fixture.Party;
            var registry = party.Leader.Registry;
            var runtime = await EnterWonderlandHandlerAsync(fixture);
            for (var island = 1; island <= 7; island++)
            {
                await ClearWonderlandHandlerIslandAsync(fixture, runtime);
                Check.True(fixture.Titles.Requests.Count == island &&
                    fixture.Titles.Requests.Select(request => request.IslandNumber).SequenceEqual(Enumerable.Range(1, island)) &&
                    party.Characters.Select((character, index) =>
                        character.OwnedTitleIds.Contains(WonderlandTitlePolicy.Resolve(island).TitleId) &&
                        character.SelectedTitleId == (index == 0 ? 0u : 5009u)).All(value => value),
                    $"island{island} independently grants its title before any later clear, without auto-equipping");
            }
            runtime.Map.TryGetWonderlandSnapshot(out var finalIsland);
            Check.True(finalIsland.CurrentIsland == 8 && finalIsland.CompletedIslands == 7 &&
                finalIsland.RequiredMonstersRemaining == 5 && fixture.Titles.Requests.Count == 7,
                "the eighth island requires four bosses and the chest guard after seven earned island titles");
            foreach (var boss in finalIsland.ActiveSpawns.Where(value => value.IsBoss))
                KillWonderlandHandlerMonster(fixture, runtime, boss.ObjectId);
            runtime.Map.TryGetWonderlandSnapshot(out var bossesDead);
            Check.True(bossesDead.State == WonderlandRunState.Active && bossesDead.RequiredMonstersRemaining == 1 &&
                !registry.HasPendingWonderlandTitles(runtime.InstanceId),
                "defeating the four final bosses cannot complete Wonderland while the chest guard remains");
            var guard = finalIsland.ActiveSpawns.Single(value => value.Role == WonderlandMonsterRole.ChestGuard);
            KillWonderlandHandlerMonster(fixture, runtime, guard.ObjectId);
            runtime.Map.TryGetWonderlandSnapshot(out var completed);
            Check.True(completed.State == WonderlandRunState.Completed && completed.CompletedIslands == 8 &&
                completed.RequiredMonstersRemaining == 0 && registry.HasPendingWonderlandTitles(runtime.InstanceId),
                "the last actual final-island death freezes completion and its eighth title entitlement");
            var completedAt = completed.TerminalAt!.Value;
            var expiresAt = completedAt + WonderlandCompletionPolicy.TreasureWindow;
            var beforeCompletion = AtlantisPacketCounts(party);
            fixture.Titles.FailuresRemaining = 2;
            await registry.AdvanceMonsterWorldOnceAsync(completedAt, CancellationToken.None);
            foreach (var (packets, index) in party.ReadAllPackets().Select((packets, index) => (packets, index)))
            {
                var emitted = packets.Skip(beforeCompletion[index]).ToArray();
                AssertWonderlandCompletionRestoresNativeCountdown(emitted);
                Check.True(emitted.Count(packet => packet.SequenceEqual(PacketBuilder.RepetitionCountdown((int)WonderlandCompletionPolicy.TreasureWindow.TotalSeconds))) == 1 &&
                    emitted.Count(packet => packet.SequenceEqual(PacketBuilder.RepetitionCompletionState(227, true))) == 1 &&
                    emitted.All(packet => ReadOpcode(packet) != Opcodes.SceneChange),
                    "each native client receives one completion countdown without an early transfer");
            }
            await registry.AdvanceMonsterWorldOnceAsync(expiresAt.AddTicks(-1), CancellationToken.None);
            Check.True(runtime.Map.Population == partySize && party.Characters.All(character => character.CurrentMap == 207),
                "members remain in their completed instance immediately before the five-minute treasure window closes");
            fixture.Titles.FailuresRemaining = 1;
            await registry.AdvanceMonsterWorldOnceAsync(expiresAt, CancellationToken.None);
            Check.True(registry.HasPendingWonderlandTitles(runtime.InstanceId) && runtime.Map.Population == partySize &&
                registry.TryGetWorldInstance(runtime.InstanceId, out _),
                "even an elapsed countdown cannot retire or exit before the pending title receipt succeeds");
            var beforeExit = AtlantisPacketCounts(party);
            await registry.AdvanceMonsterWorldOnceAsync(expiresAt.AddSeconds(1), CancellationToken.None);
            Check.True(runtime.Map.Population == 0 && !registry.TryGetWorldInstance(runtime.InstanceId, out _) &&
                !registry.HasPendingWonderlandTitles(runtime.InstanceId) &&
                party.Characters.All(character => character.CurrentMap == (character.Camp == GameDefaults.SpartaCamp
                    ? GameDefaults.SpartaCapitalMap : GameDefaults.AthensCapitalMap)),
                "successful retry exits every eligible online member and removes the empty completed runtime");
            foreach (var (packets, index) in party.ReadAllPackets().Select((packets, index) => (packets, index)))
            {
                var emitted = packets.Skip(beforeExit[index]).ToArray();
                Check.True(emitted.Count(IsAtlantisDepartureClear) == 1 &&
                    emitted.Count(packet => ReadOpcode(packet) == Opcodes.SceneChange) == 1 &&
                    packets.Skip(beforeCompletion[index]).Count(packet =>
                        ReadOpcode(packet) == Opcodes.RepetitionReset) == 2,
                    "completion sends one native countdown and one departure reset without replaying either");
            }
            var expectedTitles = Enumerable.Range(1, 8).Select(island => WonderlandTitlePolicy.Resolve(island).TitleId).ToArray();
            Check.True(expectedTitles.Distinct().Count() == 8 &&
                fixture.Titles.Requests.Select(request => request.IslandNumber).Distinct().Order().SequenceEqual(Enumerable.Range(1, 8)) &&
                fixture.Titles.Requests.Select(request => request.Award.TitleId).Distinct().Order().SequenceEqual(expectedTitles.Order()) &&
                fixture.Titles.Requests.GroupBy(request => request.IslandNumber)
                    .All(group => group.Select(request => request.RequestHash).Distinct().Count() == 1) &&
                party.Characters.Select((character, index) =>
                    character.SelectedTitleId == (index == 0 ? 0u : 5009u) &&
                    character.MedusaHonorPoints == 1234 &&
                    expectedTitles.All(character.OwnedTitleIds.Contains))
                .All(value => value), "all eight unique island titles are owned, final retries keep the same entitlement, and selection and wallet remain unchanged");
            var afterExit = AtlantisPacketCounts(party);
            await registry.AdvanceMonsterWorldOnceAsync(expiresAt.AddSeconds(2), CancellationToken.None);
            Check.True(party.ReadAllPackets().Select((packets, index) => packets.Skip(afterExit[index])
                    .All(packet => !IsAtlantisPanelPacket(packet) && ReadOpcode(packet) != Opcodes.SceneChange))
                .All(value => value), "later world ticks cannot reopen or re-exit a retired Wonderland run");
        }
    }

    private static void AssertWonderlandCompletionRestoresNativeCountdown(byte[][] packets)
    {
        // Origin4BEF6E/5EC3A2 accepts nonzero10231 only in state5/6.
        // Origin4BF438 restores that prerequisite from10232's +10 state.
        // Start reset, without trusting the sync sent when the instance began.
        var state = 0;
        var countdown = 0;
        var syncs = 0;
        foreach (var packet in packets)
        {
            if (ReadOpcode(packet) == Opcodes.RepetitionSync)
            {
                Check.True(packet.Length == 14 &&
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(4)) == 227,
                    "completion synchronization belongs to Wonderland's native scene");
                state = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(10));
                syncs++;
            }
            if (ReadOpcode(packet) == Opcodes.RepetitionReset)
            {
                var seconds = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4));
                if (seconds == 0) state = 0;
                else if (state is 5 or 6) { countdown = seconds; state = 6; }
            }
        }
        Check.True(syncs == 1 && state == 6 && countdown == 300,
            "the completion batch opens the native five-minute countdown even after client state was reset");
    }
}
