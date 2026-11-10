using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    public const string ElementalRegistryLockCheckName =
        "Wonderland derived elemental damage preserves registry-first locking and remains usable with recovery";

    public static async Task RunElementalRegistryLockAsync()
    {
        foreach (var mode in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
            await CheckElementalRegistryLockAsync(mode);
    }

    private static async Task CheckElementalRegistryLockAsync(MonsterRuntimeMode mode)
    {
        await using var f = await Fixture.CreateAsync(1, mode, PlayerRuntimeMode.Ecs);
        f.Character.MaxHp = f.Character.CurrentHp = 731_455;
        typeof(GameCharacter).GetProperty(nameof(GameCharacter.ElementalEquipment))!.SetValue(
            f.Character, RegistryLockLightningProfile());
        for (var ordinal = 1; ordinal <= 3; ordinal++)
        {
            using var prime = f.Registry.CapturePveElementalCommitAuthority(f.Session, f.Character)
                ?? throw new InvalidOperationException("Missing priming authority.");
            var hit = f.DirectHit("alpha", 1);
            var result = f.Registry.CommitPveElementalHits(prime, CombatEventProvenance.DirectBasicAttack,
                [new(770_000UL + (ulong)ordinal, 0, hit)], f.Now);
            Check.True(result.DamageCommits.Count == 0, "the first three hits prime the real Zeus cadence");
        }

        using var authority = f.Registry.CapturePveElementalCommitAuthority(f.Session, f.Character)
            ?? throw new InvalidOperationException("Missing fourth-hit authority.");
        var primary = f.DirectHit("alpha", 1_000);
        var registryGate = typeof(GameSessionRegistry).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(f.Registry)!;
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<PveElementalCommitResult>? commit = null;
        Task<RegistryLockProbe>? probe = null;
        var outerHeldAtCommit = false;
        try
        {
            f.Registry.PveElementalVitalsLockedHook = () =>
            {
                Check.True(Monitor.IsEntered(f.Character.VitalsSync), "the real PvE transaction retains source vitals");
                outerHeldAtCommit = Monitor.IsEntered(registryGate);
                held.TrySetResult();
                release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            };
            commit = Task.Run(() => f.Registry.CommitPveElementalHits(authority,
                CombatEventProvenance.DirectBasicAttack, [new(770_004, 0, primary)], f.Now));
            await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
            probe = Task.Run(() => ProbePlayerBurnLockOrder(registryGate, f.Character.VitalsSync));
            var observed = await probe.WaitAsync(TimeSpan.FromSeconds(5));
            release.TrySetResult();
            var result = await commit.WaitAsync(TimeSpan.FromSeconds(5));
            f.Registry.PveElementalVitalsLockedHook = null;
            Check.True(result.DamageCommits is [{ Kind: ResonanceDamageKind.ZeusBolt }] &&
                result.DamageCommits[0].DamageResult.ObjectId == primary.ObjectId &&
                result.DamageCommits[0].DamageResult.AfterHealth < primary.AfterHealth,
                $"{mode}: fourth-hit derived damage traverses the actual Wonderland mutation and commits once");
            var after = f.Monster("alpha");
            Check.True(after.CurrentHealth == result.DamageCommits[0].DamageResult.AfterHealth &&
                f.Registry.CommitPveElementalHits(authority, CombatEventProvenance.DirectBasicAttack,
                    [new(770_004, 0, primary)], f.Now) == PveElementalCommitResult.Empty &&
                f.Monster("alpha").CurrentHealth == after.CurrentHealth,
                "the captured authority cannot replay the primary or derived damage");
            Check.True(outerHeldAtCommit && !observed.RegistryAcquired && !observed.VitalsBlocked,
                $"{mode}: periodic recovery cannot retain registry while waiting on a derived hit's vitals; " +
                $"commit-held-registry={outerHeldAtCommit}, probe-acquired-registry={observed.RegistryAcquired}, " +
                $"probe-blocked-vitals={observed.VitalsBlocked}");
            await f.Registry.AdvancePlayerRecoveryOnceAsync(f.Now.AddMilliseconds(1), CancellationToken.None);
            await f.Registry.AdvanceMonsterWorldOnceAsync(f.Now.AddMilliseconds(2), CancellationToken.None);
            Check.True(f.Registry.GetWorldInstancePopulation(f.Runtime.InstanceId) == 1,
                "actual periodic recovery, monster world, and membership remain responsive after derived damage");
            await Task.Run(() => f.Registry.Remove(f.Session)).WaitAsync(TimeSpan.FromSeconds(5));
            Check.True(f.Registry.GetWorldInstancePopulation(f.Runtime.InstanceId) == 0,
                "session cleanup can retire the player's membership after the elemental transaction");
        }
        finally
        {
            release.TrySetResult();
            f.Registry.PveElementalVitalsLockedHook = null;
            foreach (var task in new Task?[] { commit, probe })
            {
                if (task is null) continue;
                try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { /* Retain the original assertion while draining both participants. */ }
            }
        }
    }

    private static RegistryLockProbe ProbePlayerBurnLockOrder(object registryGate, object vitals)
    {
        // This is the live CommitDuePlayerElementalBurns registry -> vitals
        // boundary. Bound only the test probe so a red run can expose the
        // inversion and release both real transactions instead of hanging CI.
        if (!Monitor.TryEnter(registryGate, TimeSpan.FromMilliseconds(250))) return new(false, false);
        try
        {
            var enteredVitals = Monitor.TryEnter(vitals, TimeSpan.FromMilliseconds(250));
            if (enteredVitals) Monitor.Exit(vitals);
            return new(true, !enteredVitals);
        }
        finally { Monitor.Exit(registryGate); }
    }

    private static ElementalEquipmentProfile RegistryLockLightningProfile()
    {
        var elements = Enum.GetValues<ElementKind>();
        var totals = elements.ToDictionary(static element => element, static _ => default(ElementalEffectTotals));
        var counts = elements.ToDictionary(static element => element, static element => element == ElementKind.Lightning ? 3 : 0);
        var active = elements.ToDictionary(static element => element,
            element => ElementalResonanceCatalog.ActiveFor(element, counts[element]));
        return new(totals, counts, active);
    }

    private readonly record struct RegistryLockProbe(bool RegistryAcquired, bool VitalsBlocked);
}
