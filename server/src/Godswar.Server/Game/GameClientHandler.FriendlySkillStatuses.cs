using Godswar.Server.Application.Finalise2;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// W2 friendly skill statuses, run after a Priest heal has been applied, persisted and published:
    /// Area Heal 4/5 leave Radiance of Renewal / Grace of Vitality (heal over time on the item HoT tick) on everyone
    /// they healed; Heal 4/5 cast Divine Intervention (remove debuffs) on their target.
    /// See <see cref="FriendlySkillStatusCatalog"/>.
    /// </summary>
    private async Task ApplyFriendlyHealStatusesAsync(
        int skillId,
        IReadOnlyList<PriestHealResult> results,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (FriendlySkillStatusCatalog.TryGetHealOverTime(skillId, out var hot) &&
            Finalise2Rates.On(Finalise2RateKeys.SkillHealOverTime))
        {
            var perSecond = FriendlySkillStatusCatalog.ScaleHealPerSecond(
                hot.HpPerSecond, Finalise2Rates.Amount(Finalise2RateKeys.SkillHealOverTimePercent));
            if (perSecond <= 0)
            {
                return;
            }

            hot = hot with { HpPerSecond = perSecond };
            var applied = 0;
            foreach (var result in results)
            {
                if (result.Before <= 0)
                {
                    continue;
                }

                try
                {
                    if (await _registry.ApplyRuntimeStatusAndPublishAsync(
                            result.Target.Session, hot, now, $"skill-{skillId}-hot", cancellationToken))
                    {
                        applied++;
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    Console.WriteLine(
                        $"[skill] heal over time publish failed target={result.Target.Character.Name}: {ex.Message}");
                }
            }

            Console.WriteLine(
                $"[skill] heal over time skill={skillId} status={hot.StatusId} hp/s={perSecond} " +
                $"for={hot.Duration.TotalSeconds:F0}s targets={applied}/{results.Count}");
            return;
        }

        if (FriendlySkillStatusCatalog.TryGetCleanse(skillId, out var icon) &&
            Finalise2Rates.On(Finalise2RateKeys.SkillCleanse))
        {
            foreach (var result in results)
            {
                if (result.Before <= 0)
                {
                    continue;
                }

                try
                {
                    var removed = await _registry.CleansePlayerDebuffsAndPublishAsync(
                        result.Target.Session, now, $"skill-{skillId}-cleanse", cancellationToken);
                    // The client's 1 s "Divine Intervention" icon; it carries no stat effect.
                    await _registry.ApplyRuntimeStatusAndPublishAsync(
                        result.Target.Session, icon, now, $"skill-{skillId}-cleanse-icon", cancellationToken);
                    Console.WriteLine(
                        $"[skill] divine intervention skill={skillId} target={result.Target.Character.Name} removed={removed}");
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    Console.WriteLine(
                        $"[skill] divine intervention publish failed target={result.Target.Character.Name}: {ex.Message}");
                }
            }
        }
    }
}
