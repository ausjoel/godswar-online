using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    private static async Task CheckMonsterDebuffCorpseExpiryAsync(MonsterRuntimeMode monsters,
        PlayerRuntimeMode players)
    {
        await using var f = await Fixture.CreateAsync(1, monsters, players);
        await CommitDebuffVisibilityAsync(f);
        var silence = await ApplyTimedBossControlAsync(f, 604);
        var death = f.DirectHit("alpha", silence.Monster.CurrentHealth);
        Check.True(death.Killed && f.Runtime.Map.TryGetMonsterSnapshot(death.ObjectId, out var corpse) &&
            !corpse.IsAlive && corpse.IsSpawned,
            "the controlled boss leaves a retained loot corpse");
        await f.Registry.AdvanceMonsterWorldOnceAsync(silence.ExpiresAt!.Value, CancellationToken.None);
        await f.FlushAsync();
        Check.Equal(0, ReadMonsterStatusIds(MonsterStatusPackets(f, death.ObjectId).Last()).Length,
            "the deadline also clears old debuff icons on the retained boss corpse");
    }

    private static async Task CheckDelayedMonsterDebuffExpiryAsync(MonsterRuntimeMode monsters,
        PlayerRuntimeMode players)
    {
        await using var f = await Fixture.CreateAsync(1, monsters, players);
        await CommitDebuffVisibilityAsync(f);
        var silence = await ApplyTimedBossControlAsync(f, 604);
        var held = await f.Registry.BeginMonsterVisibilityTransitionAsync(f.Session, 207,
            f.Character.PositionX, f.Character.PositionZ, CancellationToken.None)
            ?? throw new InvalidOperationException("Expiry race visibility lease unavailable.");
        Task? expired = null;
        DateTimeOffset refreshedUntil;
        try
        {
            expired = f.Registry.ReconcileMonsterControlsOnceAsync(silence.ExpiresAt!.Value, CancellationToken.None);
            Check.True(!expired.IsCompleted, "expiry publication waits for an in-flight visibility transition");
            f.Now = silence.ExpiresAt.Value.AddSeconds(1);
            refreshedUntil = await Task.Run(() =>
            {
                MonsterControlSkillPolicy.TryGet(354, out var freeze);
                f.Character.Profession = freeze.RequiredProfession;
                var boss = f.Monster("alpha");
                Check.True(f.Registry.TryCapturePlayerMonsterTarget(f.Session, 207, boss.ObjectId,
                        out var target, out var authority) &&
                    f.Registry.TryCommitPlayerMonsterControl(f.Session, target, authority, freeze, f.Now, out _),
                    "a new control can commit while old expiry waits without locking the registry");
                return f.Now + freeze.Duration;
            }).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { await held.DisposeAsync(); }
        await expired!.WaitAsync(TimeSpan.FromSeconds(5));
        await f.FlushAsync();
        Check.True(ReadMonsterStatusIds(MonsterStatusPackets(f, silence.Monster.ObjectId).Last()).SequenceEqual([305u]),
            "delayed Silence expiry publishes the newer Freeze instead of erasing it");
        await f.Registry.AdvanceMonsterWorldOnceAsync(refreshedUntil, CancellationToken.None);
        await f.FlushAsync();
        Check.Equal(0, ReadMonsterStatusIds(MonsterStatusPackets(f, silence.Monster.ObjectId).Last()).Length,
            "the replacement debuff still clears at its own deadline after the publication race");
    }
}
