using Godswar.Server.Application.Combat;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// B22 B23-F (the admin d86/d87): the Warrior / Champion / Priest class fields also hit ENEMY PLAYERS standing in them, on
/// the cast and on every one of the field's passes, at combat.field_pvp_percent of the damage (default 50, read on every
/// pass so the slider is hot; 0 = no player pass at all). Admission is the ordinary PvP skill admission (pvp.skills,
/// other faction / PvP map / duels, outside safe zones); no MP, cooldown or cast visual is spent again. Monsters are hit
/// by the shared Flame Blast field code. The Mage's Flame Blast is not a class field and never reaches this.
/// </summary>
internal sealed partial class GameClientHandler
{
    private async Task HitPvpPlayersInClassFieldAsync(
        SkillCombatDefinition combat,
        float centerX,
        float centerZ,
        string pass,
        CancellationToken cancellationToken)
    {
        var character = _character;
        var percent = ClassFieldSkills.PvpPercent();
        if (character is null || percent <= 0m || !ClassFieldSkills.IsField(combat.SkillId) ||
            !Godswar.Server.Application.Finalise2.Finalise2Rates.On(
                Godswar.Server.Application.Finalise2.Finalise2RateKeys.PvpSkills))
        {
            return;
        }
        // The registry admits only the process-pinned definition (a zodiac-adjusted copy would be refused).
        if (!_gameplayCatalogs.SkillCombat.TryGet(combat.SkillId, out var authored))
        {
            return;
        }

        var decision = await _registry.ResolveTrainingDummyDamageAreaAsync(
            _session,
            LocalPlayerObjectId,
            ReadOnlyMemory<byte>.Empty,
            authored,
            NextAdmittedLegacyCombatRevision,
            DateTimeOffset.UtcNow,
            cancellationToken,
            pvpSecondaryPass: true,
            groundFieldCenter: (centerX, centerZ),
            playerDamagePercent: percent);
        if (!decision.Handled || decision.Combats.Count == 0)
        {
            return;
        }

        await EndHermesHoodOnAttackAsync(
            decision.Combats.Select(static c => c.Target?.Character).FirstOrDefault(c => c is not null &&
                !_registry.IsTrainingDummy(c)), cancellationToken);
        Console.WriteLine(
            $"[pvp-skill] field {pass} character={character.Name} skill={combat.SkillId} percent={percent:0.##} " +
            $"reason={decision.RejectionReason} players={decision.Combats.Count} " +
            $"hits={decision.Combats.Count(static c => c.Resolution.Hit)} " +
            $"applied={decision.Combats.Aggregate(0UL, static (t, c) => t + c.AppliedDamage)}");
    }

    /// <summary>The cast (pass 0) of a class field: the same centre the monster field uses.</summary>
    private async Task<bool> TryHitPvpPlayersInClassFieldCastAsync(
        SkillCastRequest cast,
        SkillCombatDefinition combat,
        CancellationToken cancellationToken)
    {
        if (!ClassFieldSkills.IsField(combat.SkillId))
        {
            return false;
        }

        var character = _character;
        if (character is not null && cast.HasTargetPosition &&
            SkillCombatResolver.TryResolveHostileMonsterAreaCenter(
                character.PositionX, character.PositionZ, cast.TargetX, cast.TargetZ, combat,
                out var centerX, out var centerZ))
        {
            await HitPvpPlayersInClassFieldAsync(combat, centerX, centerZ, "cast", cancellationToken);
        }

        return true;
    }
}
