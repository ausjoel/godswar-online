using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class IntonedCombatSkillHandlerChecks
{
    private static async Task PrimeFlamePulseStylesAsync(
        Fixture fixture, SkillCombatDefinition combat, int ordinal)
    {
        var fields = FlameFields(fixture.Handler);
        ulong fieldId;
        lock (fields.Gate) fieldId = fields.Tasks.Keys.Single();
        for (var index = 0; index < 2; index++)
        {
            var desired = (index + ordinal) % 2 == 0
                ? CombatHitOutcome.Critical : CombatHitOutcome.Normal;
            var objectId = MonsterObjectId + (uint)index;
            for (var attempt = 0; ; attempt++)
            {
                Check.True(attempt < 512, "bounded Flame normal/critical event priming succeeds");
                Check.True(fixture.Registry.TryGetMonsterSnapshot(0, objectId, out var monster),
                    "Flame style priming uses a real admitted field target");
                var target = GameplayContentTestFixtures.Runtime.MonsterCombatProfiles
                    .Resolve(monster.Definition).ToTargetStats();
                var eventId = CombatEventIdentity.ForFlameBlastPulse(fixture.Character.Id,
                    objectId, monster.SpawnGeneration, monster.HealthRevision,
                    fieldId, FlameSkillId, ordinal, index);
                var resolution = SkillCombatResolver.ResolveDamage(
                    fixture.Character, combat, target, eventId, index);
                if (resolution.Outcome == desired) break;
                Check.True(fixture.Registry.TryApplyMonsterDamage(0, objectId, 1,
                        fixture.Character.Id, monster.SpawnGeneration, DateTimeOffset.UtcNow,
                        out var changed) && !changed.Killed,
                    "test-only health priming selects a deterministic outcome without changing combat rules");
            }
        }
        await RefreshLifeAbsorptionAreaVisibilityAsync(fixture);
    }
}
