using System.Collections;
using System.Reflection;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    public const string TargetRaceCheckName =
        "Wonderland scheduled casts and reflections tolerate removed targets without stopping the monster world";

    public static async Task RunTargetRacesAsync()
    {
        foreach (var monsters in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        {
            await CheckScheduledTargetRemovalAsync(monsters, reflection: false);
            await CheckScheduledTargetRemovalAsync(monsters, reflection: true);
            await CheckScheduledUnexpectedFailureAsync(monsters);
        }
    }

    private static async Task CheckScheduledTargetRemovalAsync(MonsterRuntimeMode monsters, bool reflection)
    {
        await using var f = await Fixture.CreateAsync(reflection ? 4 : 3, monsters, PlayerRuntimeMode.Ecs);
        // The reported player's HP is ordinary valid input. The deterministic
        // trigger below is session removal during status lookup, not overflow.
        f.Character.MaxHp = f.Character.CurrentHp = 731_455;
        var dueAt = await PrepareTargetRaceAsync(f, reflection);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = 0;
        f.Registry.RuntimeStatusSessionLookupHook = () =>
        {
            Interlocked.Increment(ref hooks);
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };
        var beforeHp = f.Character.CurrentHp;
        var beforeRevision = f.Character.VitalsRevision;
        var attackTask = Task.Run(() => f.Registry.AdvanceMonsterWorldOnceAsync(dueAt, CancellationToken.None));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() => f.Registry.Remove(f.Session, preservePlayerStatus: true))
                .WaitAsync(TimeSpan.FromSeconds(5));
            release.TrySetResult();
            await attackTask.WaitAsync(TimeSpan.FromSeconds(5));
            Check.Equal(1, hooks, "the due Wonderland attack reaches the exact captured-status lookup barrier once");
            Check.Equal(beforeHp, f.Character.CurrentHp, "a removed target receives no scheduled or reflected damage");
            Check.Equal(beforeRevision, f.Character.VitalsRevision, "a removed target receives no new vitals revision");
            Check.True(f.Registry.GetPlayerVitalsDamageEcsDiagnostics(f.Session) is null,
                "removal clears incoming-damage state without a late attack recreating it");
            Check.Equal(0, f.Runtime.Map.Population, "the removed target has no live map membership");
            await f.FlushAsync();
            var packets = f.Transport.ReadLegacyPackets().Count;
            await f.Registry.AdvanceMonsterWorldOnceAsync(dueAt.AddSeconds(1), CancellationToken.None);
            await f.FlushAsync();
            Check.True(!f.Transport.ReadLegacyPackets().Skip(packets).Any(IsDamagePacket),
                "the following monster-world tick survives and emits no stale target damage");
        }
        finally
        {
            release.TrySetResult();
            try { await attackTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* Preserve the original regression assertion or exception. */ }
            f.Registry.RuntimeStatusSessionLookupHook = null;
            f.Registry.RemovePlayerStatusState(f.Session);
        }
    }

    private static async Task CheckScheduledUnexpectedFailureAsync(MonsterRuntimeMode monsters)
    {
        await using var f = await Fixture.CreateAsync(3, monsters, PlayerRuntimeMode.Ecs);
        var dueAt = await PrepareTargetRaceAsync(f, reflection: false);
        var injected = new InvalidOperationException("Wonderland unexpected fault regression");
        f.Registry.RuntimeStatusSessionLookupHook = () => throw injected;
        try
        {
            Exception? actual = null;
            try { await f.Registry.AdvanceMonsterWorldOnceAsync(dueAt, CancellationToken.None); }
            catch (Exception error) { actual = error; }
            Check.True(ReferenceEquals(injected, actual),
                "only expected target unavailability is tolerated; unexpected simulation faults still reach supervision");
        }
        finally { f.Registry.RuntimeStatusSessionLookupHook = null; }
    }

    private static async Task<DateTimeOffset> PrepareTargetRaceAsync(Fixture f, bool reflection)
    {
        Check.True(SkillStatusEffectCatalog.TryGet(90, out var ward), "the race fixture resolves native Holy Ward");
        Check.True(await f.Registry.ApplyRuntimeStatusAndPublishAsync(f.Session, ward, f.Now,
            "wonderland-target-race", CancellationToken.None), "the race fixture establishes a runtime status gate");
        f.MoveTo(f.Monster(reflection ? "rock" : "rooster"));
        await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now, CancellationToken.None);
        if (reflection) EnqueueTargetRaceReflection(f);
        await f.FlushAsync();
        return reflection ? f.Now : f.Now + WonderlandBossAbilityPolicy.For("rooster")[0].Windup;
    }

    private static void EnqueueTargetRaceReflection(Fixture f)
    {
        // Direct Rock reflection currently commits synchronously. Seed the
        // retained queued-reflection dispatch explicitly to cover its separate
        // asynchronous path without adding a production-only test entry point.
        var registryType = typeof(GameSessionRegistry);
        var states = (IDictionary)registryType.GetField("_wonderlandCombat",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Registry)!;
        var state = states[f.Runtime.InstanceId]!;
        var queue = state.GetType().GetProperty("Reflections")!.GetValue(state)!;
        var reflectedType = registryType.GetNestedType("WonderlandReflectedHit", BindingFlags.NonPublic)!;
        var target = f.Registry.GetWorldInstanceSessions(f.Runtime.InstanceId).Single(context => context.Session == f.Session);
        var reflected = Activator.CreateInstance(reflectedType,
            target, f.Registry.GetPlayerLifeRevision(f.Session), f.Monster("rock"), 100u, f.Now)!;
        queue.GetType().GetMethod("Enqueue")!.Invoke(queue, [reflected]);
    }
}
