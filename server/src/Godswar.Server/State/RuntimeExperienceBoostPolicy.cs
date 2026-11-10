// ROUND3: session EXP uses authored channels and competes with durable sources without duplicating them.
namespace Godswar.Server.State;

internal readonly record struct ExperienceBoostBonuses(int Fighter, int Talent, int Pet);

internal static class RuntimeExperienceBoostPolicy
{
    internal static ExperienceBoostState Merge(ExperienceBoostState durable,
        IEnumerable<ActiveRuntimeStatus> runtime, DateTimeOffset now)
    {
        var incoming = runtime.Where(status => status.ExpiresAt > now && status.ExperienceBonuses is not null)
            .OrderBy(status => status.Kind).ToArray();
        var activeDurable = durable.ActiveBoosts.Where(boost => boost.ExpiresAt is null || boost.ExpiresAt > now).ToArray();
        if (incoming.Length == 0)
            return activeDurable.Length == durable.ActiveBoosts.Count ? durable : new ExperienceBoostState(activeDurable);
        var effective = activeDurable.ToList();
        foreach (var status in incoming)
        {
            // Durable potion channels intentionally differ from some native Kind values.
            // Preserve that channel when the exact status already has a durable source.
            var sameStatus = effective.Where(boost => boost.StatusId == status.StatusId)
                .OrderByDescending(boost => boost.Priority).FirstOrDefault();
            var kind = sameStatus?.Kind ?? status.Kind;
            var competing = effective.Where(boost => boost.Kind == kind || boost.StatusId == status.StatusId).ToArray();
            if (competing.Any(boost => boost.Priority > status.Priority)) continue;
            effective.RemoveAll(boost => boost.Kind == kind || boost.StatusId == status.StatusId);
            effective.Add(new ActiveExperienceBoost(checked((int)status.StatusId), kind, 0,
                status.Priority, status.ExpiresAt, "runtime-status") { ExplicitBonuses = status.ExperienceBonuses });
        }
        return new ExperienceBoostState(effective);
    }
}
