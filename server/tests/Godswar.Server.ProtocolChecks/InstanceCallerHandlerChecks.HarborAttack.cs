using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string HarborAttackCheckName =
        "港湾遇袭 party entry windows, confirmed-only admission, abort vote, and in-run revival";

    public static async Task RunHarborAttackAsync()
    {
        // INSTANCES: the entry switch defaults to closed; this check exercises
        // the open entry flow.
        using var open = InstanceEntrySwitchScope.Open();
        await CheckHarborAttackPartyEntryAndAbortVoteAsync();
        await CheckHarborAttackRevivalAsync();
    }

    private static async Task CheckHarborAttackPartyEntryAndAbortVoteAsync()
    {
        var daily = new ScriptedLegacyInstanceDailyEntryStore();
        await using var fixture = await CreateAtlantisOpalFixtureAsync(
            daily,
            null,
            partySize: 4);
        foreach (var character in fixture.Characters)
        {
            character.Level = 55;
        }
        var leader = fixture.Leader;
        var confirming = fixture.Followers[0];
        var late = fixture.Followers[1];
        var absent = fixture.Followers[2];
        // The shared fixture registers the followers' sinks; the leader's own
        // authoritative egress sink is registered per check, as the Atlantis
        // termination check does.
        leader.Registry.RegisterAuthoritativeInstanceTransitionSink(
            leader.Session,
            (command, token) =>
                InvokeAuthoritativeTransitionAsync(leader.Handler, command, token));
        var source = GetSourceInstanceId(leader);
        var memberPackets = fixture.Followers
            .Select(follower => follower.Transport.ReadLegacyPackets().Count)
            .ToArray();

        await InvokeAsync(leader.Handler, CreateActionPacket(
            InstanceCallerProtocol.HarborAttackRootSubId));
        await InvokeAsync(
            leader.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.HarborAttackRootSubId,
                InstanceCallerProtocol.HarborAttackPageSubId),
            confirmEntry: false);

        Check.True(
            leader.Character.CurrentMap == leader.SourceMapId &&
            PendingEntrySceneId(leader.Handler) ==
                InstanceCallerProtocol.HarborAttackFirstClientSceneId,
            "the harbour page opens the same sixty-second window the reviewed " +
            "instances open");
        Check.True(
            fixture.Followers
                .Select((follower, index) => follower.Transport
                    .ReadLegacyPackets()
                    .Skip(memberPackets[index])
                    .Count(packet => ReadOpcode(packet) is
                        Opcodes.RepetitionQueueState or Opcodes.RepetitionNotice))
                .All(count => count == 2),
            "every admitted member receives the same native Enter window the " +
            "leader received");

        var absentPackets = absent.Transport.ReadLegacyPackets().Count;
        await InvokeAsync(
            confirming.Handler,
            CreateRepetitionResponse(
                InstanceCallerProtocol.HarborAttackFirstClientSceneId,
                0,
                accepted: true),
            confirmEntry: false);
        Check.True(
            confirming.Character.CurrentMap == leader.SourceMapId &&
            absent.Transport.ReadLegacyPackets().Count == absentPackets,
            "a member's confirmation is recorded while the leader's own window " +
            "is still open");

        await InvokeAsync(
            leader.Handler,
            CreateRepetitionResponse(
                InstanceCallerProtocol.HarborAttackFirstClientSceneId,
                0,
                accepted: true),
            confirmEntry: false);

        Check.True(
            leader.Character.CurrentMap ==
                InstanceCallerProtocol.HarborAttackFirstMapId &&
            GetSourceInstanceId(leader) != source,
            "the confirming leader is admitted into an exact map 208 instance");
        Check.True(
            confirming.Character.CurrentMap ==
                InstanceCallerProtocol.HarborAttackFirstMapId &&
            leader.Registry.TryGetSessionWorldInstanceId(
                confirming.Session,
                out var confirmingInstanceId) &&
            confirmingInstanceId == GetSourceInstanceId(leader),
            "a member who confirmed the same window enters it with the leader");

        // A member whose own window is still open confirms after the run exists.
        // The admission is driven by the server on its tick, exactly as a
        // registered party member is pulled in, so the tick is advanced here.
        var latePackets = late.Transport.ReadLegacyPackets().Count;
        await InvokeAsync(
            late.Handler,
            CreateRepetitionResponse(
                InstanceCallerProtocol.HarborAttackFirstClientSceneId,
                0,
                accepted: true),
            confirmEntry: false);
        for (var tick = 0;
             tick < 4 &&
             late.Character.CurrentMap != InstanceCallerProtocol.HarborAttackFirstMapId;
             tick++)
        {
            await leader.Registry.AdvanceMonsterWorldOnceAsync(
                DateTimeOffset.UtcNow,
                CancellationToken.None);
        }

        var runInstanceId = GetSourceInstanceId(leader);
        Check.True(
            late.Character.CurrentMap ==
                InstanceCallerProtocol.HarborAttackFirstMapId &&
            leader.Registry.TryGetSessionWorldInstanceId(
                late.Session,
                out var lateInstanceId) &&
            lateInstanceId == runInstanceId &&
            daily.Claims.Any(claim =>
                claim.CharacterIds.SequenceEqual(new[] { late.Character.Id })),
            "a member who confirms after the leader committed joins the running " +
            "instance and claims only their own daily attempt");

        Check.True(
            absent.Character.CurrentMap == leader.SourceMapId &&
            daily.MemberReleases.Any(release =>
                release.CharacterIds.Contains(absent.Character.Id)) &&
            daily.MemberReleases.All(release =>
                !release.CharacterIds.Contains(confirming.Character.Id)),
            "a member who let their window close never enters and keeps the " +
            "daily attempt they never spent");

        await CompleteAtlantisSceneReadinessAsync(leader.Handler);
        await CompleteAtlantisSceneReadinessAsync(confirming.Handler);
        await CompleteAtlantisSceneReadinessAsync(late.Handler);

        // The registered leader leaves the running instance. The earliest
        // present entrant inherits the run, so its end control stays reachable
        // instead of the run becoming impossible to end.
        Check.True(
            leader.Registry.TryTransferMap(
                leader.Session,
                InstanceCallerProtocol.HarborAttackFirstMapId,
                0,
                165f,
                -97f),
            "the registered leader can leave the running instance");
        for (var tick = 0; tick < 3; tick++)
        {
            await leader.Registry.AdvanceMonsterWorldOnceAsync(
                DateTimeOffset.UtcNow,
                CancellationToken.None);
        }

        // A member's own end control is not authoritative, exactly as in the
        // reviewed instances: only the run's leader ends it.
        var memberEndPackets = late.Transport.ReadLegacyPackets().Count;
        await InvokeAsync(late.Handler, CreateHarborAbortPacket());
        Check.True(
            leader.Registry.TryGetSessionWorldInstanceId(
                confirming.Session,
                out var stillInside) &&
            stillInside == runInstanceId &&
            late.Transport.ReadLegacyPackets().Count == memberEndPackets,
            "a member's end control cannot end the instance");

        // The leadership moved to the entrant who has been inside longest, so
        // that entrant - not the departed original leader - ends the run. An
        // ended run behaves like a completed one: the panel becomes the native
        // leave countdown and members stay inside until it expires.
        var successorPackets = confirming.Transport.ReadLegacyPackets().Count;
        await InvokeAsync(confirming.Handler, CreateHarborAbortPacket());
        await leader.Registry.AdvanceMonsterWorldOnceAsync(
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        var endPackets = confirming.Transport.ReadLegacyPackets()
            .Skip(successorPackets)
            .ToArray();
        Check.True(
            endPackets.Any(packet =>
                ReadOpcode(packet) == Opcodes.RepetitionFightInfo) &&
            endPackets.Any(packet =>
                packet.SequenceEqual(PacketBuilder.RepetitionPanelCompletion())) &&
            endPackets.Any(packet =>
                ReadOpcode(packet) == Opcodes.RepetitionCompletionState &&
                System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
                    packet.AsSpan(8)) == 1) &&
            // Native 10231 carries the leave countdown when its seconds field is
            // non-zero; zero is the plain teardown.
            endPackets.Any(packet =>
                ReadOpcode(packet) == Opcodes.RepetitionReset &&
                System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
                    packet.AsSpan(4)) == 30),
            "the inherited leader's end control publishes the native leave " +
            "countdown to the members still inside");
        Check.True(
            leader.Registry.TryGetSessionWorldInstanceId(
                confirming.Session,
                out var afterEndCountdown) &&
            afterEndCountdown == runInstanceId &&
            leader.Registry.TryGetSessionWorldInstanceId(
                late.Session,
                out var lateAfterEndCountdown) &&
            lateAfterEndCountdown == runInstanceId,
            "the end countdown keeps every member inside until it expires");

        // During the countdown the same native control is the member's own leave:
        // that member is carried home at once while the others keep counting down.
        await InvokeAsync(late.Handler, CreateHarborAbortPacket());
        Check.True(
            leader.Registry.TryGetSessionWorldInstanceId(
                late.Session,
                out var lateAfterLeave) &&
            lateAfterLeave != runInstanceId &&
            late.Character.CurrentMap is
                Godswar.Server.State.GameDefaults.SpartaCapitalMap or
                Godswar.Server.State.GameDefaults.AthensCapitalMap &&
            leader.Registry.TryGetSessionWorldInstanceId(
                confirming.Session,
                out var confirmingAfterLeave) &&
            confirmingAfterLeave == runInstanceId,
            "a member's leave during the end countdown returns them to their " +
            "capital at once while the others keep counting down");

        // Terminal egress happens once the countdown expires: a member whose
        // exact write deferred is carried out by a later tick.
        for (var tick = 0; tick < 4; tick++)
        {
            await leader.Registry.AdvanceMonsterWorldOnceAsync(
                DateTimeOffset.UtcNow.AddSeconds(31),
                CancellationToken.None);
            if (leader.Registry.TryGetSessionWorldInstanceId(
                    confirming.Session,
                    out var current) && current != runInstanceId)
            {
                break;
            }
        }
        Check.True(
            leader.Registry.TryGetSessionWorldInstanceId(
                confirming.Session,
                out var afterEnd) &&
            afterEnd != runInstanceId &&
            confirming.Character.CurrentMap is
                Godswar.Server.State.GameDefaults.SpartaCapitalMap or
                Godswar.Server.State.GameDefaults.AthensCapitalMap &&
            late.Character.CurrentMap is
                Godswar.Server.State.GameDefaults.SpartaCapitalMap or
                Godswar.Server.State.GameDefaults.AthensCapitalMap,
            "the inherited leader's end control returns the remaining members " +
            "to their capital");
    }

    private static async Task CheckHarborAttackRevivalAsync()
    {
        var daily = new ScriptedLegacyInstanceDailyEntryStore();
        await using var fixture = await CreateAtlantisOpalFixtureAsync(
            daily,
            null,
            partySize: 3);
        foreach (var character in fixture.Characters)
        {
            character.Level = 55;
        }
        var leader = fixture.Leader;
        await InvokeAsync(leader.Handler, CreateActionPacket(
            InstanceCallerProtocol.HarborAttackRootSubId));
        await InvokeAsync(leader.Handler, CreateActionPacket(
            InstanceCallerProtocol.HarborAttackRootSubId,
            InstanceCallerProtocol.HarborAttackPageSubId));
        var instanceId = GetSourceInstanceId(leader);
        Check.True(
            leader.Character.CurrentMap ==
                InstanceCallerProtocol.HarborAttackFirstMapId,
            "the revival check starts inside the harbour instance");
        // The destination handshake must finish before anything but scene
        // traffic is accepted on the new map.
        await CompleteAtlantisSceneReadinessAsync(leader.Handler);

        leader.Character.CurrentHp = 0;
        leader.Character.CurrentMp = 0;
        leader.Character.PositionX = -200;
        leader.Character.PositionZ = 130;
        leader.Registry.UpdateCharacter(
            leader.Session,
            leader.Character,
            advanceWorldRevision: false);
        var beforeLife = leader.Registry.GetPlayerLifeRevision(leader.Session);
        var packets = leader.ReadPackets().Count;
        // The revive frame carries the local player object id, not the world
        // object id the registry assigns to the session.
        const uint localObjectId = 0x1448;

        await InvokeAsync(leader.Handler, CreateWonderlandRevivePacket(0x9999));
        Check.True(
            leader.Character.CurrentHp == 0 &&
            leader.Registry.GetPlayerLifeRevision(leader.Session) == beforeLife,
            "only the local character's own free revival is accepted");

        await InvokeAsync(leader.Handler, CreateWonderlandRevivePacket(localObjectId));
        Check.True(
            leader.Character.CurrentMap ==
                InstanceCallerProtocol.HarborAttackFirstMapId &&
            leader.Registry.TryGetSessionWorldInstanceId(
                leader.Session,
                out var revivedInstanceId) &&
            revivedInstanceId == instanceId &&
            leader.Character.PositionX ==
                InstanceCallerProtocol.HarborAttackArrivalX &&
            leader.Character.PositionZ ==
                InstanceCallerProtocol.HarborAttackArrivalZ &&
            leader.Character.CurrentHp ==
                Math.Max(1, leader.Character.MaxHp / 10) &&
            leader.Registry.GetPlayerLifeRevision(leader.Session) ==
                beforeLife + 1 &&
            leader.ReadPackets().Skip(packets).Count(packet =>
                ReadOpcode(packet) == Opcodes.SceneChange) == 1,
            "free revival restores a tenth of the vitals at the harbour " +
            "arrival without leaving the run");
    }

    private static GamePacket CreateHarborAbortPacket()
    {
        var bytes = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 6);
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2),
            Opcodes.RepetitionPanelAction);
        bytes[4] = 0;
        return new GamePacket(bytes);
    }
}
