using Godswar.Server.Application.World;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

// Capture pins the published rank and live death identity before settlement;
// RecordCommitted applies it only after the common reward boundary succeeds.
internal static class AtlantisMonsterKillScoring
{
    internal sealed record CapturedKill(
        WorldInstanceId WorldInstanceId, uint ObjectId, uint SpawnGeneration, string? Rank);

    internal static AtlantisKillResult RecordCommitted(
        MapInstance map,
        GameplayContentCatalog content,
        WorldInstanceId expectedInstanceId,
        MonsterDamageResult damage,
        DateTimeOffset committedAt)
    {
        if (map.WorldInstanceId != expectedInstanceId ||
            !AtlantisEncounterPolicy.IsAtlantisInstance(map.Descriptor))
        {
            return new(AtlantisKillOutcome.WrongInstance, 0, null);
        }

        var captured = Capture(map, content, expectedInstanceId, damage);
        return captured is null
            ? new(AtlantisKillOutcome.InvalidMonsterIdentity, 0, null)
            : RecordCommitted(map, captured, committedAt);
    }

    internal static CapturedKill? Capture(
        MapInstance map,
        GameplayContentCatalog content,
        WorldInstanceId expectedInstanceId,
        MonsterDamageResult damage)
    {
        if (map.WorldInstanceId != expectedInstanceId ||
            !AtlantisEncounterPolicy.IsAtlantisInstance(map.Descriptor))
        {
            return null;
        }

        if (!damage.Killed || damage.AfterHealth != 0 ||
            damage.HealthMutation is not { } mutation ||
            damage.ObjectId != mutation.ObjectId ||
            !map.TryGetMonsterSnapshot(damage.ObjectId, out var current) ||
            current.IsAlive || current.CurrentHealth != 0 ||
            current.Definition.MapId != map.MapId ||
            current.RuntimeInstanceId != damage.Monster.RuntimeInstanceId ||
            current.SpawnGeneration != damage.Monster.SpawnGeneration ||
            current.SpawnGeneration != mutation.SpawnGeneration ||
            current.HealthRevision != damage.Monster.HealthRevision ||
            current.HealthRevision != mutation.AfterHealthRevision)
        {
            return null;
        }

        var candidates = content.MonsterTemplates.Where(template =>
            string.Equals(template.TemplateKey, current.Definition.TemplateKey,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        var exact = candidates.Where(template =>
            template.SourceMapId == current.Definition.MapId).ToArray();
        var ranks = (exact.Length > 0
                ? exact
                : candidates.Where(template => template.SourceMapId is null))
            .Select(template => template.Rank)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Missing or conflicting published ranks award nothing; the combat
        // profile's defensive unknown-as-boss fallback is not score authority.
        return new(
            expectedInstanceId, current.ObjectId, current.SpawnGeneration,
            ranks.Length == 1 ? ranks[0] : null);
    }

    internal static AtlantisKillResult RecordCommitted(
        MapInstance map, CapturedKill captured, DateTimeOffset committedAt) =>
        map.RecordCommittedAtlantisMonsterKill(
            captured.WorldInstanceId, captured.ObjectId, captured.SpawnGeneration,
            captured.Rank, committedAt);
}
