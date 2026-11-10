using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// W3 PvP part 2: the intonation of a single-target damage skill aimed at another player. Mirrors the monster
/// intonation (mana check, range, cast visual to self and the map) but checks the player target; the completion
/// re-enters HandleSkillCastAsync, which resolves the hit on the PvP route (admission is checked again there).
/// </summary>
internal sealed partial class GameClientHandler
{
    private async Task BeginIntonedPvpSkillCastAsync(
        GamePacket packet,
        SkillCastRequest cast,
        SkillCombatDefinition combat,
        CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null)
        {
            return;
        }

        int currentMana;
        lock (character.VitalsSync)
        {
            currentMana = character.CurrentMp;
        }

        if (currentMana < Math.Max(0, combat.Mp))
        {
            await SendInsufficientManaRejectionAsync(currentMana, cancellationToken, "IntonedPvpSkillManaRejected");
            return;
        }

        if (!IsPvpIntonationTargetValid(cast, combat))
        {
            Console.WriteLine(
                $"[pvp-skill] rejected intonation target character={character.Name} skill={cast.SkillId} target={cast.TargetObjectId}");
            return;
        }

        var worldObjectId = CurrentPlayerObjectId;
        var started = await TryBeginPendingSkillCastAsync(
            cast.SkillId,
            combat.CastTime,
            "pvp",
            async token =>
            {
                await _session.SendAsync(
                    PacketBuilder.SkillCastVisual(packet.Buffer, LocalPlayerObjectId),
                    token,
                    "IntonedPvpSkillCastSelf");
                await _registry.BroadcastToMapAsync(
                    character.CurrentMap,
                    PacketBuilder.SkillCastVisual(packet.Buffer, worldObjectId),
                    token,
                    _session,
                    "IntonedPvpSkillCastWorld");
            },
            token => HandleSkillCastAsync(
                packet,
                token,
                intonationCompleted: true,
                expectedTargetSpawnGeneration: null,
                intonedCombatSnapshot: combat),
            cancellationToken,
            () => IsPvpIntonationTargetValid(cast, combat));
        Console.WriteLine(
            $"[pvp-skill] intonation {(started ? "started" : "refused: another cast is pending")} " +
            $"character={character.Name} skill={cast.SkillId} target={cast.TargetObjectId} cast={combat.CastTime.TotalSeconds:0.##}s");
    }

    private bool IsPvpIntonationTargetValid(SkillCastRequest cast, SkillCombatDefinition combat)
    {
        var character = _character;
        return character is not null &&
               _registry.TryGetCurrentWorldSessionByObjectId(
                   _session, character.CurrentMap, cast.TargetObjectId, out var target) &&
               !ReferenceEquals(target.Character, character) &&
               target.Character.CurrentHp > 0 &&
               SkillCombatResolver.IsWithinRange(
                   character.PositionX, character.PositionZ,
                   target.Character.PositionX, target.Character.PositionZ,
                   combat);
    }
}

internal sealed partial class GameClientHandler
{
    /// <summary>W3 PvP area skills: the enemy players inside an area cast that already went through.</summary>
    private async Task TryHitPvpPlayersInAreaAsync(
        GamePacket packet,
        SkillCastRequest cast,
        SkillCombatDefinition authoredCombat,
        CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null ||
            !Godswar.Server.Application.Finalise2.Finalise2Rates.On(
                Godswar.Server.Application.Finalise2.Finalise2RateKeys.PvpSkills) ||
            !TrainingDummyDamageSkillPolicy.IsAuthoritativeArea(_gameplayCatalogs, authoredCombat, allowCastTime: true))
        {
            return;
        }

        var decision = await _registry.ResolveTrainingDummyDamageAreaAsync(
            _session,
            cast.CasterObjectId,
            packet.Buffer,
            authoredCombat,
            NextAdmittedLegacyCombatRevision,
            DateTimeOffset.UtcNow,
            cancellationToken,
            pvpSecondaryPass: true);
        if (!decision.Handled)
        {
            return;
        }

        await EndHermesHoodOnAttackAsync( // F-156: the first real player caught in the area (dummies skip)
            decision.Combats.Select(static c => c.Target?.Character).FirstOrDefault(c => c is not null &&
                !_registry.IsTrainingDummy(c)), cancellationToken);
        await ApplyPvpHitStunsAsync(cast.SkillId, decision.Combats, cancellationToken);
        ApplyPvpHitDots(cast.SkillId, decision.Combats);
        Console.WriteLine(
            $"[pvp-skill] area character={character.Name} skill={cast.SkillId} accepted={decision.Accepted} " +
            $"reason={decision.RejectionReason} players={decision.Combats.Count} " +
            $"hits={decision.Combats.Count(static c => c.Resolution.Hit)} " +
            $"applied={decision.Combats.Aggregate(0UL, static (t, c) => t + c.AppliedDamage)}");
    }
}

internal sealed partial class GameClientHandler
{
    // Status.ini kinds of the stun statuses SkillHitStunCatalog uses (Dizzy 1446/1447 and Frozen 1433/1436 are
    // Kind 13, Concussed 1444 is Kind 1005). PlayerSkillCastControlCatalog already treats all five as stuns.
    private static int StunStatusKind(uint statusId) => statusId == 1444 ? 1005 : 13;

    /// <summary>
    /// W3 PvP statuses: a PvP hit from a skill whose stock status stops the target (Dizzy, Concussed, Ice Cone
    /// freeze; SkillHitStunCatalog) puts that status on the victim through the player status store, which blocks
    /// the victim's actions server-side and tells the victim's client (ApplyRuntimeStatusAndPublishAsync).
    /// Switch combat.skill_stuns, chance combat.skill_dot_chance (same as on monsters). Dummies keep their own path.
    /// </summary>
    private async Task ApplyPvpHitStunsAsync(
        uint skillId,
        IEnumerable<PvpBasicAttackDecision> hits,
        CancellationToken cancellationToken)
    {
        if (skillId > int.MaxValue ||
            !SkillHitStunCatalog.TryGet((int)skillId, out var statusId, out var durationMs) ||
            !Godswar.Server.Application.Finalise2.Finalise2Rates.On(
                Godswar.Server.Application.Finalise2.Finalise2RateKeys.SkillStuns))
        {
            return;
        }

        var chance = Math.Clamp(Godswar.Server.Application.Finalise2.Finalise2Rates.Amount(
            Godswar.Server.Application.Finalise2.Finalise2RateKeys.SkillDotChance), 0, 100) * 100;
        foreach (var hit in hits)
        {
            if (hit.Target is not { } target || hit.TargetKilled || !hit.Resolution.Hit ||
                _registry.IsTrainingDummy(target.Character) ||
                Godswar.Server.World.Systems.Combat.DeterministicCombatRandom.RollBasisPoints(
                    hit.Resolution.EventId, 0,
                    Godswar.Server.World.Systems.Combat.CombatRandomStage.SkillStun) >= chance)
            {
                continue;
            }

            var definition = new SkillStatusEffectDefinition(
                (int)skillId, statusId, StunStatusKind(statusId), 1, false,
                TimeSpan.FromMilliseconds(durationMs), TimeSpan.Zero, 0, 0);
            var applied = await _registry.ApplyRuntimeStatusAndPublishAsync(
                target.Session, definition, DateTimeOffset.UtcNow, "pvp-skill-stun", cancellationToken);
            Console.WriteLine(
                $"[pvp-skill] stun status={statusId} skill={skillId} target={target.Character.Name} {durationMs} ms applied={applied}");
        }
    }
}

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// W3 PvP control casts (TrainingDummyHostileStatusSkillCatalog single-target controls: Stun 70-74 status 331,
    /// Freeze 350-354 301-305, Silence 600-604 360-364, Shackle 790-794 400-408) aimed at an enemy player. Range,
    /// the live PvP admission, MP, then the landing chance (combat.skill_dot_chance) and the status through the
    /// player status store (PlayerSkillCastControlCatalog blocks/stops on these ids; the victim's client is told).
    /// A cast time runs first through the PvP intonation. pvp.skills gates it.
    /// </summary>
    private async Task<bool> TryHandlePvpControlCastAsync(
        GamePacket packet,
        SkillCastRequest cast,
        SkillCombatDefinition combat,
        HostileStatusEffectDefinition control,
        bool intonationCompleted,
        CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null ||
            control.TargetMode != HostileStatusTargetMode.SingleTarget ||
            control.Control == HostileStatusControlFlags.None ||
            control.RequiredProfession != character.Profession ||
            !Godswar.Server.Application.Finalise2.Finalise2Rates.On(
                Godswar.Server.Application.Finalise2.Finalise2RateKeys.PvpSkills) ||
            !_registry.TryGetCurrentWorldSessionByObjectId(
                _session, character.CurrentMap, cast.TargetObjectId, out var target) ||
            ReferenceEquals(target.Character, character) ||
            _registry.IsTrainingDummy(target.Character))
        {
            return false;
        }

        if (!intonationCompleted && combat.CastTime > TimeSpan.Zero)
        {
            await BeginIntonedPvpSkillCastAsync(packet, cast, combat, cancellationToken);
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        var admission = _registry.EvaluatePvpSkillAdmission(character, target.Character, now);
        var inRange = SkillCombatResolver.IsWithinRange(
            character.PositionX, character.PositionZ,
            target.Character.PositionX, target.Character.PositionZ, combat);
        if (!admission.Allowed || !inRange || target.Character.CurrentHp <= 0 ||
            ResolvePlayerSkillCastControl(now) != PlayerSkillCastControl.None)
        {
            Console.WriteLine(
                $"[pvp-skill] control refused character={character.Name} skill={cast.SkillId} target={target.Character.Name} " +
                $"admitted={admission.Allowed} inRange={inRange}");
            return true;
        }

        int mana;
        lock (character.VitalsSync)
        {
            if (character.CurrentMp < control.ManaCost)
            {
                mana = -1;
            }
            else
            {
                character.CurrentMp -= control.ManaCost;
                character.MarkVitalsChanged();
                mana = character.CurrentMp;
            }
        }

        if (mana < 0)
        {
            await SendInsufficientManaRejectionAsync(character.CurrentMp, cancellationToken, "PvpControlManaRejected");
            return true;
        }

        _registry.UpdateCharacter(_session, character, advanceWorldRevision: false);
        await _session.SendAsync(PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, mana), cancellationToken,
            "PvpControlMana");
        await EndHermesHoodOnAttackAsync(target.Character, cancellationToken); // F-156
        if (!intonationCompleted)
        {
            await _session.SendAsync(PacketBuilder.SkillCastVisual(packet.Buffer, LocalPlayerObjectId),
                cancellationToken, "PvpControlCastSelf");
            await _registry.BroadcastToMapAsync(character.CurrentMap,
                PacketBuilder.SkillCastVisual(packet.Buffer, CurrentPlayerObjectId), cancellationToken, _session,
                "PvpControlCastWorld");
        }

        var chance = Math.Clamp(Godswar.Server.Application.Finalise2.Finalise2Rates.Amount(
            Godswar.Server.Application.Finalise2.Finalise2RateKeys.SkillDotChance), 0, 100) * 100;
        var roll = Random.Shared.Next(10_000);
        var applied = false;
        if (roll < chance)
        {
            applied = await _registry.ApplyRuntimeStatusAndPublishAsync(
                target.Session,
                new SkillStatusEffectDefinition(control.SkillId, control.StatusId, control.Kind, control.Priority,
                    false, control.Duration, TimeSpan.Zero, 0, 0),
                now, "pvp-skill-control", cancellationToken);
        }

        Console.WriteLine(
            $"[pvp-skill] control character={character.Name} skill={cast.SkillId} target={target.Character.Name} " +
            $"status={control.StatusId} {control.Duration.TotalSeconds:0.#}s roll={roll}/{chance} applied={applied} mp={mana}");
        return true;
    }
}

internal sealed partial class GameClientHandler
{
    /// <summary>W3 PvP: Bleeding / Electrocuted / Burned from a PvP hit (same switches as on monsters).</summary>
    private void ApplyPvpHitDots(uint skillId, IEnumerable<PvpBasicAttackDecision> hits)
    {
        var character = _character;
        if (character is null || skillId > int.MaxValue ||
            !SkillDamageOverTimeCatalog.TryGet((int)skillId, out var definition) ||
            !Godswar.Server.Application.Finalise2.Finalise2Rates.On(
                Godswar.Server.Application.Finalise2.Finalise2RateKeys.SkillDots))
        {
            return;
        }

        var chance = Math.Clamp(Godswar.Server.Application.Finalise2.Finalise2Rates.Amount(
            Godswar.Server.Application.Finalise2.Finalise2RateKeys.SkillDotChance), 0, 100) * 100;
        var percent = Math.Max(0, Godswar.Server.Application.Finalise2.Finalise2Rates.Amount(
            Godswar.Server.Application.Finalise2.Finalise2RateKeys.SkillDotDamagePercent));
        var tick = (int)Math.Min(int.MaxValue, (long)definition.TickDamage * percent / 100);
        foreach (var hit in hits)
        {
            if (hit.Target is not { } target || hit.TargetKilled || !hit.Resolution.Hit ||
                _registry.IsTrainingDummy(target.Character) ||
                Godswar.Server.World.Systems.Combat.DeterministicCombatRandom.RollBasisPoints(
                    hit.Resolution.EventId, 0,
                    Godswar.Server.World.Systems.Combat.CombatRandomStage.SkillDot) >= chance)
            {
                continue;
            }

            var applied = _registry.TryApplyPlayerSkillDot(
                target.Session, definition, character.Id, hit.Resolution.EventId, DateTimeOffset.UtcNow, tick);
            Console.WriteLine(
                $"[pvp-skill] dot {definition.Family} skill={skillId} target={target.Character.Name} tick={tick} " +
                $"x{definition.TickCount} applied={applied}");
        }
    }
}
