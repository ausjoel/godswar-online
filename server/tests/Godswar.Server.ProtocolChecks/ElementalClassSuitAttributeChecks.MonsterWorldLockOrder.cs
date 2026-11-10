using System.Reflection;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.World;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ElementalClassSuitAttributeChecks
{
    internal const string MonsterWorldLockOrderCheckName =
        "Monster world target projection and elemental Shock do not invert owner and vitals locks";

    internal static async Task RunMonsterWorldLockOrderAsync()
    {
        foreach (var mode in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
            await CheckMonsterWorldElementalLockOrderAsync(mode);
    }

    private static async Task CheckMonsterWorldElementalLockOrderAsync(MonsterRuntimeMode mode)
    {
        await using var socket = await RuntimePolicySessionSocket.CreateAsync();
        var catalogs = GameplayRuntimeCatalogs.Create(new GameplayContentCatalog(
            [], [], [], [ElementalMonsterTemplate("ElementalOwnerLockOrder", isBoss: false)], [], [], []));
        await using var registry = new GameSessionRegistry(
            store: null, zodiacEnergyOptions: null, mode, PlayerRuntimeMode.Ecs,
            gameplayCatalogs: catalogs);
        var ownership = new PlayerOwnershipFence(Guid.NewGuid(), 1);
        var character = ElementalLiveCharacter(1_424, 64, ownership);
        character.MaxHp = character.CurrentHp = 731_455;
        SetElementalProfile(character, LiveProfile((ElementKind.Lightning, 1,
            new ElementalEffectTotals(1_000, 0, 2_000))));
        var at = DateTimeOffset.UtcNow;
        const uint objectId = 9_424;
        registry.InitializeMapMonsters(character.CurrentMap,
            [ElementalReachMonster(objectId, "ElementalOwnerLockOrder")], at);
        BindElementalLiveSession(registry, socket.Session, character, ownership, at);
        var directory = (LocalWorldInstanceRuntimeDirectory)typeof(GameSessionRegistry)
            .GetProperty("WorldInstances", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;
        var runtime = directory.Snapshot().Single(value => value.MapId == character.CurrentMap);
        var advanceTargets = typeof(GameSessionRegistry).GetMethod("AdvanceMonsterWorldRuntime",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var authority = registry.CapturePveElementalCommitAuthority(socket.Session, character)
            ?? throw new InvalidOperationException("Lock-order fixture captured no authority.");
        var primary = ApplyTransactionDamage(registry, socket.Session, character, objectId, 1, at);
        var eventId = FindReachApplicationEventId(ElementKind.Lightning, character, objectId, at, 424_001);
        var vitalsLocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var projectionStarting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<PveElementalCommitResult>? elemental = null;
        Task? world = null;
        try
        {
            registry.PveElementalVitalsLockedHook = () =>
            {
                Check.True(Monitor.IsEntered(character.VitalsSync),
                    "the barrier runs inside the real elemental vitals transaction");
                vitalsLocked.TrySetResult();
                projectionStarting.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            };
            registry.MonsterCombatTargetProjectionStartingHook = () => projectionStarting.TrySetResult();
            elemental = Task.Run(() => registry.CommitPveElementalHits(authority,
                CombatEventProvenance.DirectBasicAttack, [new(eventId, 0, primary)], at));
            await vitalsLocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Exercise the actual target-projection/owner-advance stage. The
            // outer elemental registry lock now correctly serializes earlier
            // full-world preflight, which must not be part of this barrier.
            world = Task.Run(() => advanceTargets.Invoke(registry, [runtime, at]));
            await Task.WhenAll(elemental, world).WaitAsync(TimeSpan.FromSeconds(5));
            var committed = await elemental;
            Check.True(committed.ControlCommits is [{ Applied: true }] &&
                committed.ControlCommits[0].ObjectId == objectId,
                $"{mode}: genuine Shock owner mutation completes while monster projection waits on vitals");
            Check.True(registry.GetMapPopulation(character.CurrentMap) == 1 &&
                registry.TryGetMonsterSnapshot(socket.Session, character.CurrentMap, objectId, out var monster) &&
                monster.StunnedUntil > at && monster.CurrentHealth == primary.AfterHealth,
                $"{mode}: the owner remains usable and the one committed primary hit and Shock are retained");
            registry.PveElementalVitalsLockedHook = null;
            registry.MonsterCombatTargetProjectionStartingHook = null;
            await registry.AdvanceMonsterWorldOnceAsync(at.AddMilliseconds(1), CancellationToken.None);
        }
        finally
        {
            // Both real mailbox invocations are bounded. Release test barriers,
            // observe accepted work, and drain membership before fixture disposal.
            projectionStarting.TrySetResult();
            registry.PveElementalVitalsLockedHook = null;
            registry.MonsterCombatTargetProjectionStartingHook = null;
            foreach (var task in new Task?[] { elemental, world })
            {
                if (task is null) continue;
                try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { /* Preserve the original failure after observing both tasks. */ }
            }
            registry.Remove(socket.Session);
            registry.RemoveAccountSession(character.AccountId, socket.Session);
        }
    }
}
