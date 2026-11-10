using System.Collections;
using System.Reflection;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string AtlantisRetirementCheckName =
        "Atlantis finished empty instances release runtimes and retry lifecycle cleanup";

    public static async Task RunAtlantisRetirementAsync()
    {
        await CheckAtlantisActiveEmptyAdmissionRetainedAsync();
        await CheckAtlantisTerminalRetirementAsync(complete: true,
            WorldInstanceLifecycleState.Active);
        foreach (var resumeState in new[]
            { WorldInstanceLifecycleState.Active, WorldInstanceLifecycleState.Draining,
                WorldInstanceLifecycleState.Closed })
        {
            await CheckAtlantisTerminalRetirementAsync(complete: false, resumeState);
        }
        await CheckAtlantisTerminalRetirementAsync(complete: false,
            WorldInstanceLifecycleState.Closed, removeBeforeRetry: true);
    }

    private static async Task CheckAtlantisActiveEmptyAdmissionRetainedAsync()
    {
        await using var fixture = await CreateFixtureAsync(level: 90, transitionReady: true);
        var created = await fixture.Registry.CreateLocalWorldInstanceAsync(
            RealmId.Tempest, new MapId(205), InstanceKind.Dungeon, 1,
            CancellationToken.None);
        var runtime = created.Runtime ?? throw new InvalidOperationException(
            "Atlantis admission runtime was not created.");
        var startedAt = runtime.Descriptor.LastTransitionAt;
        Check.True(fixture.Registry.TryStartAtlantisEncounter(runtime.InstanceId, 4,
                [(fixture.Character.Id, 90)], startedAt),
            "an admission can prepare Atlantis before transferring its first player");
        Check.Equal(0, runtime.Map.Population, "the admission is initially empty");
        await fixture.Registry.AdvanceMonsterWorldOnceAsync(startedAt, CancellationToken.None);
        Check.True(fixture.Registry.TryGetWorldInstance(runtime.InstanceId, out var retained) &&
            retained.LifecycleState == WorldInstanceLifecycleState.Active &&
            fixture.Registry.TryGetAtlantisEncounterSnapshot(runtime.InstanceId, out var run) &&
            run.State == AtlantisRunState.Active,
            "an active empty admission is never mistaken for a finished abandoned run");
        Check.Equal(0, AtlantisRegistryCacheCount(fixture.Registry, "_pendingAtlantisRetirements"),
            "active admissions do not enqueue retirement work");
        Check.True(await fixture.Registry.TryRetireEmptyLocalWorldInstanceAsync(runtime.Descriptor),
            "the test explicitly compensates its unused admission");
    }

    private static async Task CheckAtlantisTerminalRetirementAsync(
        bool complete,
        WorldInstanceLifecycleState resumeState,
        bool removeBeforeRetry = false)
    {
        await using var fixture = await CreateAtlantisOpalFixtureAsync(null, null, partySize: 2);
        var registry = fixture.Leader.Registry;
        await EnterAtlantisAsync(fixture, InstanceCallerProtocol.AtlantisEnterSubId);
        await CompleteAtlantisSceneReadinessAsync(fixture.Leader.Handler);
        foreach (var follower in fixture.Followers)
        {
            await CompleteAtlantisSceneReadinessAsync(follower.Handler);
        }
        var instanceId = GetSourceInstanceId(fixture.Leader);
        var directory = (LocalWorldInstanceRuntimeDirectory)typeof(GameSessionRegistry)
            .GetProperty("WorldInstances", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(registry)!;
        Check.True(directory.TryFind(instanceId, out var runtime) &&
            registry.TryGetAtlantisEncounterSnapshot(instanceId, out _),
            "the admitted party owns an Atlantis runtime");
        Check.True(registry.TryGetAtlantisEncounterSnapshot(instanceId, out var initial),
            "the admitted Atlantis clock exists");
        await registry.AdvanceMonsterWorldOnceAsync(initial.StartedAt, CancellationToken.None);
        Check.Equal(2, AtlantisRegistryCacheCount(registry, "_atlantisUi"),
            "both exact party members have cached native Atlantis panels");

        var terminalAt = initial.Deadline;
        if (complete)
        {
            var finished = await CompleteAtlantisScoreAsync(fixture, runtime, initial);
            terminalAt = finished.TerminalAt!.Value;
        }
        await registry.AdvanceMonsterWorldOnceAsync(terminalAt, CancellationToken.None);
        Check.True(registry.TryGetWorldInstance(instanceId, out var occupied) &&
            occupied.LifecycleState == WorldInstanceLifecycleState.Active &&
            runtime.Map.Population == 2 &&
            fixture.Characters.All(static character => character.CurrentMap == 205),
            "terminal Atlantis retains its occupied runtime without moving anyone");
        Check.Equal(1, AtlantisRegistryCacheCount(registry, "_pendingAtlantisRetirements"),
            "only the terminal run records pending retirement");

        registry.Remove(fixture.Leader.Session);
        await registry.AdvanceMonsterWorldOnceAsync(terminalAt.AddSeconds(1), CancellationToken.None);
        Check.True(registry.TryGetWorldInstance(instanceId, out _) && runtime.Map.Population == 1,
            "the leader leaving cannot retire a run containing a follower");
        registry.Remove(fixture.Followers.Single().Session);
        Check.Equal(0, runtime.Map.Population, "all party sessions left the actual map");

        if (resumeState is WorldInstanceLifecycleState.Draining or WorldInstanceLifecycleState.Closed)
        {
            var draining = await directory.BeginDrainAsync(instanceId, runtime.Descriptor.Revision,
                terminalAt.AddSeconds(2), CancellationToken.None);
            Check.True(draining.Status == WorldInstanceRuntimeDirectoryStatus.Draining,
                "fixture reaches an interrupted retirement after BeginDrain");
        }
        if (resumeState == WorldInstanceLifecycleState.Closed)
        {
            var closed = await directory.CloseAsync(instanceId, runtime.Descriptor.Revision,
                terminalAt.AddSeconds(3), CancellationToken.None);
            Check.True(closed.Status == WorldInstanceRuntimeDirectoryStatus.Closed,
                "fixture reaches an interrupted retirement after owner shutdown");
        }
        if (removeBeforeRetry)
        {
            var removal = await directory.RemoveClosedAsync(instanceId, CancellationToken.None);
            Check.True(removal.Status == WorldInstanceRuntimeDirectoryStatus.Removed,
                "another cleanup can remove the closed runtime before its retry");
        }

        await registry.AdvanceMonsterWorldOnceAsync(terminalAt.AddSeconds(4), CancellationToken.None);
        Check.True(!registry.TryGetWorldInstance(instanceId, out _) &&
            !registry.TryGetAtlantisEncounterSnapshot(instanceId, out _),
            $"empty terminal Atlantis resumes from {resumeState} and releases its directory entry");
        foreach (var field in new[]
            { "_atlantisDailyLimits", "_atlantisUi", "_pendingAtlantisRetirements" })
        {
            Check.Equal(0, AtlantisRegistryCacheCount(registry, field),
                $"retired Atlantis releases {field}");
        }
        await registry.AdvanceMonsterWorldOnceAsync(terminalAt.AddSeconds(5), CancellationToken.None);
        Check.True(!registry.TryGetWorldInstance(instanceId, out _),
            "repeated world ticks after retirement are harmless");
    }

    private static int AtlantisRegistryCacheCount(GameSessionRegistry registry, string field) =>
        ((IDictionary)typeof(GameSessionRegistry)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(registry)!).Count;
}
