using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Rewards;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly IMonsterDeathRewardCommandExecutor?
        _monsterDeathRewardCommands;
    private readonly bool _requiresDurableMonsterRewardCommands;

    private async Task<MonsterRewardSettlement?>
        SettleMonsterRewardWithImmediateRetryAsync(
            MonsterDamageResult damageResult,
            int awardedExperience,
            int awardedTalentExperience,
            int awardedPetExperience,
            DateTimeOffset receivedAt,
            bool partyShare = false)
    {
        Action? recordAtlantisKill = null;
        try
        {
            // PARTY (r02): a party member's share never scores the encounter;
            // the killer's own settlement already did.
            recordAtlantisKill = partyShare
                ? null
                : _registry.CaptureAtlantisMonsterKill(
                    _session, damageResult);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[atlantis] death capture failed monster={damageResult.ObjectId}: {ex.Message}");
        }

        // INSTANCES: Bay Under Attack kills (team score, Christina ends the run)
        // are recorded at the same committed-settlement point. Null elsewhere.
        try
        {
            var recordHarborKill = _registry.CaptureHarborAttackMonsterKill(
                _session, damageResult);
            if (recordHarborKill is not null)
            {
                recordAtlantisKill = recordAtlantisKill is null
                    ? recordHarborKill
                    : recordAtlantisKill + recordHarborKill;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[harbor] death capture failed monster={damageResult.ObjectId}: {ex.Message}");
        }

        // TRAINING: Training Ground kills (team score, 300 clears) at the same point.
        recordAtlantisKill = ChainTrainingGroundKill(recordAtlantisKill, damageResult);

        return await MonsterDeathRewardCommitBoundary.ExecuteAsync(
            cancellationToken => SettleMonsterRewardAsync(
                damageResult,
                awardedExperience,
                awardedTalentExperience,
                awardedPetExperience,
                receivedAt,
                recordAtlantisKill,
                cancellationToken,
                partyShare),
            allowImmediateReplay:
                _monsterDeathRewardCommands is not null,
            firstFailure =>
                // A database commit can succeed while its acknowledgement is
                // lost. Replay the same server-derived death identity once.
                Console.Error.WriteLine(
                    $"[reward] immediate durable replay death={damageResult.ObjectId} reason={firstFailure.GetType().Name}"),
            onSettled: settlement =>
            {
                if (settlement is not null)
                {
                    // Replayed receipts also enter this idempotent boundary:
                    // a lost acknowledgement must not lose the encounter kill.
                    try
                    {
                        recordAtlantisKill?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"[atlantis] committed kill projection failed monster={damageResult.ObjectId}: {ex.Message}");
                    }
                }
            });
    }

    private async Task<MonsterRewardSettlement?> SettleMonsterRewardAsync(
        MonsterDamageResult damageResult,
        int awardedExperience,
        int awardedTalentExperience,
        int awardedPetExperience,
        DateTimeOffset receivedAt,
        Action? recordAtlantisKill,
        CancellationToken cancellationToken,
        bool partyShare = false)
    {
        // PARTY (r02): a party member's share settles under its own death
        // identity, derived from the monster's runtime instance and the
        // member's character id, so the global death claim stays the killer's
        // and each member's share is exactly-once (a replay pays nothing).
        var rewardRuntimeInstanceId = partyShare && _character is not null
            ? MonsterDeathRewardCommandEnvelope.DerivePartyShareRuntimeInstanceId(
                damageResult.Monster.RuntimeInstanceId,
                _character.Id)
            : damageResult.Monster.RuntimeInstanceId;
        if (_account is null ||
            _character is null ||
            !damageResult.Killed ||
            damageResult.AfterHealth != 0 ||
            damageResult.HealthMutation is not { } mutation ||
            mutation.AfterHealthRevision !=
                damageResult.Monster.HealthRevision ||
            mutation.SpawnGeneration !=
                damageResult.Monster.SpawnGeneration ||
            damageResult.Monster.Definition.MapId !=
                _character.CurrentMap ||
            !MonsterDeathRewardCommandEnvelope.TryCreateCommand(
                rewardRuntimeInstanceId,
                _character.CurrentMap,
                damageResult.ObjectId,
                mutation.SpawnGeneration,
                mutation.AfterHealthRevision,
                awardedExperience,
                awardedTalentExperience,
                out var command))
        {
            Console.Error.WriteLine(
                "[reward] rejected invalid server-derived monster death");
            return null;
        }

        if (_monsterDeathRewardCommands is not null)
        {
            var transport = _session.IsSecure
                ? CommandTransportKind.SecureTlsLegacy
                : CommandTransportKind.LegacyTcp;
            var unownedEnvelope =
                MonsterDeathRewardCommandEnvelope.Create(
                new CommandSubject(_account.Id, _character.Id),
                new CommandConnectionCorrelation(
                    _commandConnectionId,
                    transport),
                receivedAt,
                command);
            if (!TryBindCurrentPlayerOwnership(
                    unownedEnvelope,
                    out var envelope,
                    out var ownership))
            {
                return null;
            }

            var execution = recordAtlantisKill is null
                ? await _monsterDeathRewardCommands.ExecuteAsync(envelope, cancellationToken)
                : await _monsterDeathRewardCommands.ExecuteWithCommitObserverAsync(
                    envelope,
                    receipt =>
                    {
                        if (receipt.DeathEventId != command.DeathEventId ||
                            receipt.CharacterId != envelope.Subject.CharacterId)
                        {
                            throw new InvalidDataException("Monster commit observer received unrelated death evidence.");
                        }
                        try
                        {
                            recordAtlantisKill();
                        }
                        catch (Exception error)
                        {
                            Console.Error.WriteLine(
                                $"[atlantis] committed death observation failed monster={damageResult.ObjectId}: {error.Message}");
                        }
                    },
                    cancellationToken);
            if (!RevalidateCurrentPlayerOwnership(ownership))
            {
                return null;
            }

            if (execution.Receipt is null ||
                execution.Projection is null)
            {
                Console.Error.WriteLine(
                    $"[reward] durable settlement rejected death={command.DeathEventId:N} disposition={execution.Disposition}");
                return null;
            }

            var settlement = new MonsterRewardSettlement(
                command.DeathEventId,
                ToLegacyProgressionResult(execution.Receipt),
                execution.Projection,
                execution.Disposition ==
                    MonsterDeathRewardExecutionDisposition.Committed,
                IsDurable: true);
            if (partyShare)
            {
                // PARTY (r02): pet EXP stays with the killer.
                return settlement;
            }

            return await AttachPetMonsterExperienceAsync(
                settlement,
                awardedPetExperience,
                cancellationToken);
        }

        if (_requiresDurableMonsterRewardCommands)
        {
            // PostgreSQL production composition must never bypass the global
            // death claim and exactly-once reward transaction.
            Console.Error.WriteLine(
                $"[reward] durable reward provider unavailable death={command.DeathEventId:N}");
            return null;
        }

        var legacy = await SettleLegacyMonsterRewardAsync(
            command.DeathEventId,
            awardedExperience,
            awardedTalentExperience,
            cancellationToken);
        return legacy is null
            ? null
            : partyShare
            ? legacy
            : await AttachPetMonsterExperienceAsync(
                legacy,
                awardedPetExperience,
                cancellationToken);
    }

    private async Task<MonsterRewardSettlement>
        AttachPetMonsterExperienceAsync(
            MonsterRewardSettlement settlement,
            int awardedPetExperience,
            CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return settlement;
        }

        var result = await _monsterRewardExtras.ApplyPetMonsterKillExperienceAsync(
            _account.Id,
            _character.Id,
            settlement.DeathEventId,
            awardedPetExperience,
            cancellationToken);
        return settlement with { PetExperience = result };
    }

    private static CharacterProgressionResult ToLegacyProgressionResult(
        MonsterDeathRewardExecutionReceipt receipt) =>
        new(
            receipt.ExperienceGained,
            receipt.PreviousLevel,
            receipt.CurrentLevel,
            receipt.CurrentExperience,
            receipt.NextLevelExperience,
            receipt.LevelUps
                .Select(static levelUp =>
                    new PlayerLevelUpProgression(
                        levelUp.Level,
                        levelUp.CurrentExperience,
                        levelUp.NextLevelExperience))
                .ToArray(),
            receipt.TalentExperienceGained,
            receipt.CurrentTalentExperience,
            receipt.TalentPointsGained,
            receipt.CurrentTalentPoints);

    private sealed record MonsterRewardSettlement(
        Guid DeathEventId,
        CharacterProgressionResult Progression,
        MonsterDeathRewardProjection? Projection,
        bool IsFirstCommit,
        bool IsDurable,
        PetMonsterExperienceResult? PetExperience = null);
}
