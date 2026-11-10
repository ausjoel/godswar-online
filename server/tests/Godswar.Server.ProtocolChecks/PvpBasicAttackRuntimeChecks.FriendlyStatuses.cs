using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>W2 friendly skill statuses: Radiance / Grace heal over time and Divine Intervention.</summary>
internal static partial class PvpBasicAttackRuntimeChecks
{
    public const string FriendlyStatusesCheckName =
        "W2 friendly skill statuses (Radiance / Grace heal over time, Divine Intervention cleanse)";

    public static async Task RunFriendlyStatusesAsync()
    {
        ServerRates.ResetToDefaults();
        Check.True(Finalise2Rates.On(Finalise2RateKeys.SkillHealOverTime), "combat.skill_heal_over_time defaults to on");
        Check.Equal(100, Finalise2Rates.Amount(Finalise2RateKeys.SkillHealOverTimePercent), "HoT percent defaults to stock");
        Check.True(Finalise2Rates.On(Finalise2RateKeys.SkillCleanse), "combat.skill_cleanse defaults to on");

        Check.True(FriendlySkillStatusCatalog.TryGetHealOverTime(763, out var radiance) &&
                   radiance is { StatusId: 1434, Kind: 1001, Priority: 4, Beneficial: true, HpPerSecond: 1_500 } &&
                   radiance.Duration == TimeSpan.FromSeconds(8),
            "Area Heal 4 = Radiance of Renewal 1500 HP/s for 8 s (Status.ini 1434)");
        Check.True(FriendlySkillStatusCatalog.TryGetHealOverTime(764, out var grace) &&
                   grace is { StatusId: 1435, Kind: 1001, HpPerSecond: 1_750 } && grace.Duration == TimeSpan.FromSeconds(8),
            "Area Heal 5 = Grace of Vitality 1750 HP/s for 8 s (Status.ini 1435)");
        Check.True(!FriendlySkillStatusCatalog.TryGetHealOverTime(762, out _) &&
                   !FriendlySkillStatusCatalog.TryGetHealOverTime(753, out _), "ranks 1-3 and single Heal carry no HoT");
        Check.True(FriendlySkillStatusCatalog.TryGetCleanse(753, out var icon4) &&
                   FriendlySkillStatusCatalog.TryGetCleanse(754, out _) &&
                   icon4 is { StatusId: 1439, Kind: 1002, Beneficial: true } && icon4.Duration == TimeSpan.FromSeconds(1),
            "Heal 4/5 = Divine Intervention (Status.ini 1439, 1 s)");
        Check.True(!FriendlySkillStatusCatalog.TryGetCleanse(752, out _) &&
                   !FriendlySkillStatusCatalog.TryGetCleanse(763, out _), "other heals do not cleanse");
        Check.Equal(750, FriendlySkillStatusCatalog.ScaleHealPerSecond(1_500, 50), "percent rate scales the tick");
        Check.Equal(0, FriendlySkillStatusCatalog.ScaleHealPerSecond(1_500, -5), "negative percent heals nothing");

        foreach (var mode in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
        {
            await using var socket = await RuntimePolicySessionSocket.CreateAsync();
            var registry = Registry(mode);
            var player = Player(1450, GameDefaults.SpartaCamp);
            Join(registry, socket, player);
            var now = DateTimeOffset.UtcNow;

            // Heal over time: the item HoT tick heals the stock amount each second and stops at max HP.
            lock (player.VitalsSync)
            {
                player.CurrentHp = 5_000;
            }

            Check.True(await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, radiance, now, "fixture-hot",
                CancellationToken.None), $"{mode}: Radiance applies");
            await registry.AdvanceStatusPeriodicRecoveryOnceAsync(now.AddSeconds(1), CancellationToken.None);
            Check.Equal(6_500, player.CurrentHp, $"{mode}: one tick heals 1500");
            await registry.AdvanceStatusPeriodicRecoveryOnceAsync(now.AddSeconds(1.5), CancellationToken.None);
            Check.Equal(6_500, player.CurrentHp, $"{mode}: no second tick inside the same second");
            for (var second = 2; second <= 5; second++)
            {
                await registry.AdvanceStatusPeriodicRecoveryOnceAsync(now.AddSeconds(second), CancellationToken.None);
            }

            Check.Equal(10_000, player.CurrentHp, $"{mode}: heal over time is capped at max HP");

            // Divine Intervention: hostile timed statuses and PvP DoTs go; buffs and the mount stay.
            var hostile = new SkillStatusEffectDefinition(70, 331, 13, 1, false, TimeSpan.FromSeconds(30), TimeSpan.Zero, 0, 0);
            Check.True(await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, hostile, now, "fixture-stun",
                CancellationToken.None), $"{mode}: hostile control applies");
            Check.True(SkillStatusEffectCatalog.TryGet(774, out var gaia), "authored Gaia exists");
            gaia = gaia with { Duration = TimeSpan.FromMinutes(10) };
            Check.True(await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, gaia, now, "fixture-buff",
                CancellationToken.None), $"{mode}: buff applies");
            await registry.SetPersistentRuntimeStatusAndPublishAsync(socket.Session, MountCatalog.RuntimeStatusKind,
                60_001, 1, false, 0.5f, true, now, "fixture-mount", CancellationToken.None);
            Check.True(SkillDamageOverTimeCatalog.TryGet(63, out var bleed) &&
                       registry.TryApplyPlayerSkillDot(socket.Session, bleed, 9_999, 77, now, 800),
                $"{mode}: PvP Bleeding applies");

            var removed = await registry.CleansePlayerDebuffsAndPublishAsync(socket.Session, now.AddSeconds(1),
                "fixture-cleanse", CancellationToken.None);
            Check.Equal(2, removed, $"{mode}: the stun and the Bleeding are removed");
            var later = now.AddSeconds(1);
            Check.True(!registry.IsRuntimeStatusActive(socket.Session, hostile.Kind, later), $"{mode}: stun gone");
            Check.True(registry.IsRuntimeStatusActive(socket.Session, gaia.Kind, later), $"{mode}: buff kept");
            Check.True(registry.IsRuntimeStatusActive(socket.Session, MountCatalog.RuntimeStatusKind, later),
                $"{mode}: mount kept");
            Check.Equal(0, await registry.CleansePlayerDebuffsAndPublishAsync(socket.Session, later, "fixture-again",
                CancellationToken.None), $"{mode}: nothing left to remove");
        }

        Check.True(!GameSessionRegistry.IsCleansableDebuff(
                new ActiveRuntimeStatus(1, MountCatalog.RuntimeStatusKind, 1, false, DateTimeOffset.UtcNow.AddHours(1),
                    ClientStatusAggregate.Empty, 1), DateTimeOffset.UtcNow),
            "the mount kind is never cleansed even with an expiry");
        ServerRates.ResetToDefaults();
    }
}
