using System.Reflection;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MapLiveTransferChecks
{
    public const string MutationLifetimeCheckName =
        "World membership accepted mutation lifetime";

    public static async Task RunMutationLifetimeAsync()
    {
        await CheckDeliveryLeaseDoesNotBlockWorldOwnerAsync();
        await CheckAcceptedProjectionOutlivesOwnerDeadlineAsync();
    }

    private static async Task CheckDeliveryLeaseDoesNotBlockWorldOwnerAsync()
    {
        await using var socket = await RuntimePolicySessionSocket.CreateAsync();
        await using var registry = CreateRegistry(worldInstanceOptions:
            new WorldInstanceRuntimeOptions { RealmId = 1, OwnerInvocationTimeoutMilliseconds = 50 });
        var character = CreateCharacter();
        registry.JoinMap(socket.Session, AccountId, character, PlayerObjectId,
            worldReady: true, joinedAt: TestTime);
        registry.InitializeMapMonsters(SourceMapId, [CreateMonster()], TestTime);
        var source = registry.GetMapSessions(SourceMapId).Single();
        var sourceRuntime = GetMutationRuntime(registry, source.WorldInstanceId);
        await using (var visibility = await registry.BeginMonsterVisibilityTransitionAsync(
                         socket.Session, SourceMapId, SourceX, SourceZ, CancellationToken.None))
        {
            Check.True(visibility is not null, "mutation fixture initializes visibility");
            visibility!.Commit();
        }

        await using var lease = await sourceRuntime.Map.AcquireMonsterViewerDeliveryLeaseAsync(
            socket.Session, MonsterObjectId, CancellationToken.None);
        Check.True(lease is not null, "mutation fixture holds a real source delivery lease");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transfer = Task.Run(() =>
        {
            started.SetResult();
            return registry.TryTransferMap(socket.Session,
                SourceMapId, TargetMapId, TargetX, TargetZ);
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(200);
            Check.True(!transfer.IsCompleted,
                "transfer waits for delivery ordering beyond the 50ms owner deadline");
            Check.True(await Task.Run(() => registry.IsCurrentWorldSessionSnapshot(
                    socket.Session, source)).WaitAsync(TimeSpan.FromSeconds(2)),
                "waiting for delivery does not retain the registry gate");
            Check.Equal(1, sourceRuntime.Owner.Invoke(static map => map.Population,
                    TimeSpan.FromSeconds(2)),
                "waiting for delivery does not occupy the source world owner");
        }
        finally
        {
            await lease!.DisposeAsync();
        }

        Check.True(await transfer.WaitAsync(TimeSpan.FromSeconds(2)),
            "transfer commits after source delivery completes");
        Check.True(character.CurrentMap == TargetMapId &&
            registry.GetMapPopulation(SourceMapId) == 0 &&
            registry.GetMapPopulation(TargetMapId) == 1 &&
            registry.GetMapSessions(TargetMapId).Count == 0,
            "accepted transfer leaves exactly one hidden destination membership");
        Check.Equal(0, sourceRuntime.Owner.GetSnapshot().Depth,
            "source has no late removal command after transfer completion");
        registry.Remove(socket.Session);
    }

    private static async Task CheckAcceptedProjectionOutlivesOwnerDeadlineAsync()
    {
        await using var socket = await RuntimePolicySessionSocket.CreateAsync();
        await using var registry = CreateRegistry(worldInstanceOptions:
            new WorldInstanceRuntimeOptions { RealmId = 1, OwnerInvocationTimeoutMilliseconds = 50 });
        var character = CreateCharacter();
        registry.JoinMap(socket.Session, AccountId, character, PlayerObjectId,
            worldReady: true, joinedAt: TestTime);
        var source = registry.GetMapSessions(SourceMapId).Single();
        var runtime = GetMutationRuntime(registry, source.WorldInstanceId);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocker = runtime.Owner.TrySubmit(map =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Mutation fixture was not released.");
            }
            return map.Population;
        });
        Check.True(entered.Wait(TimeSpan.FromSeconds(2)), "world owner blocker entered");
        var projection = Task.Run(() => registry.UpdateCharacter(
            socket.Session, character, advanceWorldRevision: false));
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (runtime.Owner.GetSnapshot().Depth < 2)
            {
                await Task.Delay(5, deadline.Token);
            }
            await Task.Delay(200);
            Check.True(!projection.IsCompleted,
                "accepted membership projection retains completion beyond owner wait deadline");
        }
        finally
        {
            release.Set();
        }
        await blocker.RequireCompletion().WaitAsync(TimeSpan.FromSeconds(2));
        await projection.WaitAsync(TimeSpan.FromSeconds(2));
        Check.True(registry.GetMapPopulation(SourceMapId) == 1 &&
            registry.IsCurrentWorldSessionSnapshot(socket.Session, source),
            "accepted projection settles without a late rollback or routing replacement");
        registry.Remove(socket.Session);
    }

    private static WorldInstanceRuntime GetMutationRuntime(
        GameSessionRegistry registry, WorldInstanceId instanceId) =>
        (WorldInstanceRuntime)(typeof(GameSessionRegistry).GetMethod(
            "GetRequiredWorldInstance", BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, types: [typeof(WorldInstanceId)], modifiers: null)!
            .Invoke(registry, [instanceId])!);
}
