using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static async Task CheckAtlantisTerminalAdmissionAsync(AtlantisRunState terminalState)
    {
        await using var fixture = await CreateAtlantisOpalFixtureAsync(null, null,
            failedFollowerIndexes: new HashSet<int> { 1 }, partySize: 2);
        var registry = fixture.Leader.Registry;
        var (runtime, initial) = await PrepareAtlantisDepartureAsync(fixture, publishInitialUi: false);
        var follower = fixture.Followers.Single();
        var source = registry.GetMapSessions(follower.Character.CurrentMap)
            .Single(context => ReferenceEquals(context.Session, follower.Session));
        Check.True(source.WorldReady && source.Ownership.IsValid && source.MapId != 205 &&
            source.WorldInstanceId != runtime.InstanceId && runtime.Map.Population == 1,
            "the late entrant retains a valid ready source session after its initial transfer failed");

        if (terminalState == AtlantisRunState.Completed)
        {
            _ = CompleteAtlantisRewardRun(runtime, initial);
        }
        else
        {
            Check.True(runtime.Map.TryAdvanceAtlantisEncounter(initial.Deadline, out var timeout) &&
                timeout.State == AtlantisRunState.TimedOut, "the exact destination reaches its terminal deadline");
        }
        Check.True(runtime.Descriptor.LifecycleState == WorldInstanceLifecycleState.Active &&
            runtime.Map.TryGetAtlantisRunSnapshot(out var terminal) && terminal.State == terminalState,
            "directory activity alone cannot make a completed or timed-out run admit another member");
        var sourceX = follower.Character.PositionX;
        var sourceZ = follower.Character.PositionZ;
        var before = follower.Transport.ReadLegacyPackets().Count;
        var command = new AuthoritativeInstanceTransitionCommand(follower.Character.Id,
            source.WorldInstanceId, source.MapId, source.Ownership,
            runtime.InstanceId, 205, 171f, 24f);
        Check.True(!await InvokeAuthoritativeTransitionAsync(follower.Handler, command, CancellationToken.None),
            $"the real authoritative transition handler rejects entry into {terminalState} Atlantis");
        Check.True(registry.TryGetSessionWorldInstanceId(follower.Session, out var after) &&
            after == source.WorldInstanceId && follower.Character.CurrentMap == source.MapId &&
            follower.Character.PositionX == sourceX && follower.Character.PositionZ == sourceZ &&
            runtime.Map.Population == 1 && follower.Transport.ReadLegacyPackets().Skip(before)
                .All(packet => ReadOpcode(packet) != Opcodes.SceneChange),
            "rejected terminal admission preserves source ownership, position, and scene without exposing the destination");
    }
}
