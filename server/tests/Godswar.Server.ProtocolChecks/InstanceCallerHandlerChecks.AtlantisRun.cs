using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string AtlantisRunCheckName =
        "Atlantis handler entry, team score, and world-clock completion";

    public static async Task RunAtlantisRunAsync()
    {
        await CheckAtlantisStationaryWaveVisibilityAsync();
        foreach (var partySize in new[] { 1, 5 })
        {
            await CheckAtlantisRunAsync(partySize, complete: true);
            await CheckAtlantisRunAsync(partySize, complete: false);
        }
    }

    private static async Task CheckAtlantisRunAsync(int partySize, bool complete)
    {
        var daily = new ScriptedLegacyInstanceDailyEntryStore();
        var payments = new ScriptedLegacyInstanceOpalPaymentStore();
        await using var fixture = await CreateAtlantisOpalFixtureAsync(
            daily, payments, partySize: partySize);
        var registry = fixture.Leader.Registry;
        registry.RegisterAuthoritativeInstanceTransitionSink(fixture.Leader.Session,
            (command, token) => InvokeAuthoritativeTransitionAsync(fixture.Leader.Handler, command, token));
        try
        {
            await CheckAtlantisRunResultAsync(fixture, daily, payments, partySize, complete);
        }
        finally
        {
            registry.UnregisterAuthoritativeInstanceTransitionSink(fixture.Leader.Session);
        }
    }

    private static async Task CheckAtlantisRunResultAsync(AtlantisOpalFixture fixture,
        ScriptedLegacyInstanceDailyEntryStore daily, ScriptedLegacyInstanceOpalPaymentStore payments,
        int partySize, bool complete)
    {
        var leader = fixture.Leader;
        var sourceId = GetSourceInstanceId(leader);
        var rewardsBefore = fixture.Characters.Select(AtlantisRewardState).ToArray();

        await EnterAtlantisAsync(fixture, InstanceCallerProtocol.AtlantisEnterSubId);
        var instanceId = GetSourceInstanceId(leader);
        Check.True(instanceId != sourceId &&
            fixture.Characters.All(static character => character.CurrentMap == 205) &&
            AllSessionsShareCurrentInstance(fixture) &&
            daily.Claims.Single().CharacterIds.Count == partySize &&
            payments.Charges.Count == 0,
            $"Atlantis action210 admits {partySize} member(s) into one free exact instance");
        Check.True(leader.Registry.TryGetAtlantisEncounterSnapshot(instanceId, out var initial) &&
            initial.WorldInstanceId == instanceId && initial.ContentMapId.Value == 205 &&
            initial.State == AtlantisRunState.Active && initial.TeamPoints == 0 &&
            initial.Deadline - initial.StartedAt == TimeSpan.FromMinutes(40),
            "actual Atlantis handler entry starts exactly one 40-minute score clock");

        await CompleteAtlantisSceneReadinessAsync(leader.Handler);
        foreach (var follower in fixture.Followers)
        {
            await CompleteAtlantisSceneReadinessAsync(follower.Handler);
        }

        var initialPacketCounts = AtlantisPacketCounts(fixture);
        await leader.Registry.AdvanceMonsterWorldOnceAsync(initial.StartedAt, CancellationToken.None);
        AssertAtlantisInitialPanel(fixture, initialPacketCounts);
        var runtime = (WorldInstanceRuntime)(typeof(GameSessionRegistry)
            .GetMethod("GetRequiredWorldInstance", BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, types: [typeof(WorldInstanceId)], modifiers: null)!
            .Invoke(leader.Registry, [instanceId]) ??
                throw new InvalidOperationException("Atlantis runtime was not available."));
        AtlantisRunSnapshot terminal;
        int[] terminalPacketCounts;
        if (complete)
        {
            terminal = await CompleteAtlantisScoreAsync(fixture, runtime, initial);
            terminalPacketCounts = AtlantisPacketCounts(fixture);
            await leader.Registry.AdvanceMonsterWorldOnceAsync(
                terminal.TerminalAt!.Value.AddSeconds(1), CancellationToken.None);
        }
        else
        {
            var beforeLastSecond = AtlantisPacketCounts(fixture);
            await leader.Registry.AdvanceMonsterWorldOnceAsync(
                initial.Deadline.AddSeconds(-1), CancellationToken.None);
            AssertAtlantisFightPanel(fixture, beforeLastSecond, remainingSeconds: 1, points: 0);
            Check.True(leader.Registry.TryGetAtlantisEncounterSnapshot(instanceId, out var almost) &&
                almost.State == AtlantisRunState.Active,
                "Atlantis remains active during its last second");
            terminalPacketCounts = AtlantisPacketCounts(fixture);
            await leader.Registry.AdvanceMonsterWorldOnceAsync(initial.Deadline, CancellationToken.None);
            Check.True(leader.Registry.TryGetAtlantisEncounterSnapshot(instanceId, out terminal!) &&
                terminal.State == AtlantisRunState.TimedOut &&
                terminal.TerminalAt == initial.Deadline && terminal.TeamPoints == 0,
                "world tick expires Atlantis at the exact 40-minute deadline");
        }

        AssertAtlantisTerminalPanel(fixture, terminalPacketCounts, complete);
        var afterTerminalCounts = AtlantisPacketCounts(fixture);
        var lateKill = runtime.Map.RecordCommittedAtlantisMonsterKill(
            instanceId, 999_999, 1, "boss", initial.Deadline.AddMinutes(1));
        await leader.Registry.AdvanceMonsterWorldOnceAsync(
            initial.Deadline.AddMinutes(1), CancellationToken.None);
        Check.True(lateKill.PointsAwarded == 0 &&
            runtime.Map.TryGetAtlantisRunSnapshot(out var repeated) &&
            repeated == terminal &&
            fixture.ReadAllPackets().Select((packets, index) => packets.Skip(afterTerminalCounts[index])
                .All(packet => ReadOpcode(packet) is not (Opcodes.RepetitionCompletionState or
                    Opcodes.RepetitionPanelAction or Opcodes.RepetitionFightInfo))).All(static value => value),
            "terminal Atlantis score and timer stay frozen while corpse lifecycle may continue");
        if (complete)
        {
            Check.True(fixture.Characters.All(character => character.CurrentMap ==
                    (character.Camp == GameDefaults.SpartaCamp
                        ? GameDefaults.SpartaCapitalMap : GameDefaults.AthensCapitalMap)) &&
                runtime.Map.Population == 0 && !leader.Registry.TryGetWorldInstance(instanceId, out _),
                "completed Atlantis auto-exits the party after its countdown and retires the empty run");
        }
        else
        {
            Check.True(fixture.Characters.All(character => character.CurrentMap ==
                    (character.Camp == GameDefaults.SpartaCamp
                        ? GameDefaults.SpartaCapitalMap : GameDefaults.AthensCapitalMap)) &&
                runtime.Map.Population == 0,
                "a timed-out run enters the same terminal flow: its countdown expires and " +
                "every member is carried home, exactly as a completed run");
        }
        Check.True(fixture.Characters.Select(AtlantisRewardState).SequenceEqual(rewardsBefore) &&
            fixture.ReadAllPackets().All(packets => packets.All(packet =>
                ReadOpcode(packet) != Opcodes.RepetitionReward)),
            "a fixture without the optional durable reward store does not fabricate reward mutations");
    }

    private static async Task<AtlantisRunSnapshot> CompleteAtlantisScoreAsync(
        AtlantisOpalFixture fixture, WorldInstanceRuntime runtime, AtlantisRunSnapshot initial)
    {
        var totalKills = 0;
        var points = 0;
        AtlantisRunSnapshot? terminal = null;
        for (var waveIndex = 0; waveIndex < 25; waveIndex++)
        {
            var alive = runtime.Map.SnapshotMonsters().Where(monster =>
                monster.IsAlive && IsAtlantisScoringObject(monster.ObjectId)).ToArray();
            var bossWave = waveIndex % 5 == 4;
            Check.Equal(bossWave ? 1 : 12, alive.Length,
                "actual entry exposes exactly one current Atlantis group or boss");
            if (waveIndex == 24)
            {
                Check.Equal(800, points, "the final boss unlocks at exactly 800 team points");
                Check.Equal("Dinna the Sea Guard", alive.Single().Definition.DisplayName,
                    "the approved final boss supplies the last 50 points");
            }
            var at = initial.StartedAt.AddSeconds(waveIndex + 1);
            var beforeProgress = AtlantisPacketCounts(fixture);
            foreach (var monster in alive)
            {
                Check.True(runtime.Map.TryApplyMonsterDamage(monster.ObjectId,
                    monster.CurrentHealth, at, out var death) && death.Killed,
                    "a current wave monster can be killed through the real map runtime");
                var scored = AtlantisMonsterKillScoring.RecordCommitted(runtime.Map,
                    GameplayContentTestFixtures.Published, runtime.InstanceId, death, at);
                Check.True(scored.PointsAwarded > 0, "committed authoritative wave death scores");
                points += scored.PointsAwarded;
                totalKills++;
                terminal = scored.Snapshot;
            }
            if (waveIndex < 24)
            {
                await fixture.Leader.Registry.AdvanceMonsterWorldOnceAsync(at, CancellationToken.None);
                AssertAtlantisFightPanel(fixture, beforeProgress,
                    remainingSeconds: 2_400 - waveIndex - 1, points: points);
                Check.True(terminal?.State == AtlantisRunState.Active,
                    "earlier groups and bosses leave the encounter active");
            }
        }
        Check.Equal(245, totalKills, "handler-created Atlantis contains exactly 245 scored monsters");
        Check.True(terminal is { State: AtlantisRunState.Completed, TeamPoints: 850 },
            "the final boss completes the actual staged encounter at 850 points");
        return terminal!;
    }
    private static bool IsAtlantisScoringObject(uint objectId) => objectId is >= 42_000 and < 42_245;

    private static async Task CompleteAtlantisSceneReadinessAsync(GameClientHandler handler)
    {
        await InvokeAsync(handler, CreateControlPacket(Opcodes.ClientReady));
        await InvokeAsync(handler, CreatePlayerDetailRequest());
    }

    private static int[] AtlantisPacketCounts(AtlantisOpalFixture fixture) =>
        fixture.ReadAllPackets().Select(static packets => packets.Count).ToArray();

    private static void AssertAtlantisInitialPanel(AtlantisOpalFixture fixture, int[] before)
    {
        var allPackets = fixture.ReadAllPackets();
        // The published roster is the run's own admitted party, ordered by
        // character id, with the run's leader marked: the party leader who
        // committed the entry is the instance's leader. Every member is inside, so
        // every row is online, and the packet's single state byte is 1 for all of
        // them - 「在线」 and 「在线(队长)」 share the one byte the client draws.
        var expectedRoster = PacketBuilder.RepetitionInstanceMembers(
            fixture.Characters.OrderBy(static character => character.Id)
                .Select(character => new RepetitionInstanceMember(
                    character.Id, character.Name, character.Level,
                    character.Id == fixture.Leader.Character.Id
                        ? RepetitionMemberState.OnlineLeader
                        : RepetitionMemberState.Online,
                    character.Profession))
                .ToArray());
        for (var index = 0; index < allPackets.Count; index++)
        {
            var emitted = allPackets[index].Skip(before[index]).ToArray();
            var sync = emitted.Single(packet => ReadOpcode(packet) == Opcodes.RepetitionSync);
            Check.True(sync.Length == 14 &&
                BinaryPrimitives.ReadUInt16LittleEndian(sync.AsSpan(4)) == 224 &&
                BinaryPrimitives.ReadUInt16LittleEndian(sync.AsSpan(10)) == 5 &&
                BinaryPrimitives.ReadUInt16LittleEndian(sync.AsSpan(12)) == 4 &&
                emitted.Single(packet => ReadOpcode(packet) == Opcodes.RepetitionInstanceMembers)
                    .SequenceEqual(expectedRoster),
                $"Atlantis member {index} receives scene224, unchanged daily limit, and exact party roster");
        }
        AssertAtlantisFightPanel(fixture, before, remainingSeconds: 2_400, points: 0);
    }

    private static void AssertAtlantisFightPanel(
        AtlantisOpalFixture fixture, int[] before, int remainingSeconds, int points)
    {
        var allPackets = fixture.ReadAllPackets();
        for (var index = 0; index < allPackets.Count; index++)
        {
            var fight = allPackets[index].Skip(before[index])
                .Single(packet => ReadOpcode(packet) == Opcodes.RepetitionFightInfo);
            Check.True(fight.Length == 20 &&
                BinaryPrimitives.ReadInt32LittleEndian(fight.AsSpan(4)) == remainingSeconds &&
                BinaryPrimitives.ReadInt32LittleEndian(fight.AsSpan(16)) == points,
                $"Atlantis member {index} sees {remainingSeconds} seconds and {points} team points");
        }
    }

    private static void AssertAtlantisTerminalPanel(
        AtlantisOpalFixture fixture, int[] before, bool complete)
    {
        var allPackets = fixture.ReadAllPackets();
        // Native 10231 is the leave countdown when its seconds field is non-zero
        // and the plain teardown when it is zero.
        var seconds = BinaryPrimitives.ReadInt32LittleEndian(
            allPackets[0].Skip(before[0])
                .Single(packet => ReadOpcode(packet) == Opcodes.RepetitionReset)
                .AsSpan(4));
        for (var index = 0; index < allPackets.Count; index++)
        {
            var emitted = allPackets[index].Skip(before[index]).ToArray();
            var result = emitted.Single(packet => ReadOpcode(packet) == Opcodes.RepetitionCompletionState);
            // A completed run and a run that ran out of time show the same native
            // leave countdown: the panel is terminal either way, and the members
            // are carried home when the countdown expires.
            Check.True(result.Length == 12 &&
                BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(4)) == 224 &&
                BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(8)) == 1 &&
                seconds is 29 or 30 &&
                emitted.All(packet => ReadOpcode(packet) != Opcodes.SceneChange) &&
                emitted.Any(packet => packet.SequenceEqual(PacketBuilder.RepetitionPanelCompletion())) &&
                emitted.Any(packet => packet.SequenceEqual(PacketBuilder.RepetitionCountdown(seconds))),
                $"Atlantis member {index} receives its native leave countdown without relocation");
        }
        AssertAtlantisFightPanel(fixture, before,
            remainingSeconds: seconds, points: complete ? 850 : 0);
    }

    private static string AtlantisRewardState(GameCharacter character) =>
        JsonSerializer.Serialize(new
        {
            character.Experience, character.TalentExperience, character.Silver,
            character.Gold, character.BindingGold, character.MedusaHonorPoints,
            character.MedusaRewardRevision, character.KitBag, character.SelectedTitleId,
            character.OwnedTitleIds
        });
}
