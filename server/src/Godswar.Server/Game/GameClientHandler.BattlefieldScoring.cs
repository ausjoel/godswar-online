using Godswar.Server.Application.Rates;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Game;

// Pindus Mountains (map 38) and Ni Mini Valley (map 34) scoring. A kill counts when killer and victim are on the same
// battlefield map, of opposite factions, inside an open window (BattlefieldSchedulePolicy, as the transporter sees
// it). Pindus (IGG acid=123 "5 points and 1 contribution"): 5 points to the killer's faction
// (battlefield.pindus_kill_points) and 1 personal point to the killer. The personal 1 is a reading, matching the
// client's own Crier text NF_L0_W212/W232 ("1 point to you, 5 to your faction"); the guide's "to all team members"
// party share is not built (party sharing belongs to the party work), so only the killer is credited.
// Ni Mini (IGG acid=182): 1 point to the faction and to the killer.
// Gate: battlefield.scoring (0 = nothing is recorded).
internal sealed partial class GameClientHandler
{
    private static bool BattlefieldScoringEnabled =>
        ServerRates.Current.Absolute(ServerRateKeys.BattlefieldScoring) == 1;

    private async Task RecordBattlefieldPlayerKillAsync(QuestPlayerDeath victim, CancellationToken cancellationToken)
    {
        if (!BattlefieldScoringEnabled || _character is not { } killer || BattlefieldStore is not { } store ||
            victim.KillerId != killer.Id || victim.VictimId == killer.Id || victim.Camp == killer.Camp ||
            victim.KillerMap != victim.VictimMap)
        {
            return;
        }

        var battlefield = BattlefieldRewardPolicy.ForMap(victim.KillerMap);
        // BATTLEFIELD2: on Pindus only the 60-200 band scores (battlefield.pindus_band).
        if (battlefield == 0 ||
            battlefield == BattlefieldRewardPolicy.Pindus && !BattlefieldRewardPolicy.PindusLevelEligible(killer.Level))
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            if (BattlefieldRewardPolicy.CurrentRound(battlefield, _realmCalendar, now) is not { } round)
            {
                return;
            }
            var points = battlefield == BattlefieldRewardPolicy.Pindus ? BattlefieldRewardPolicy.PindusKillPoints() : 1;
            var credited = await store.RecordBattlefieldKillAsync(
                battlefield, round.Start, round.End, killer.Id, killer.Camp, victim.VictimId, victim.Camp,
                victim.Level, points, BattlefieldRewardPolicy.PersonalKillPoints, cancellationToken);
            Console.WriteLine(
                $"[battlefield] kill character={killer.Name} victim={victim.VictimId} battlefield={battlefield} " +
                $"round={round.Start:u} points={points} credited={credited}");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Scoring must never break the kill or the quest credit that follows it.
            Console.Error.WriteLine($"[battlefield] kill scoring failed character={killer.Name}: {error.Message}");
        }
    }

    /// <summary>
    /// IGG acid=123: "Killing it will earn the faction a whopping 1,000 points ... only the faction that deals the last
    /// blow will walk away with all the points." The last hitter also gets them as personal non-kill points (the
    /// ranking the "Score King" / "High Scorer" titles use): GENERATED reading, the guide names only the faction.
    /// </summary>
    private async Task RecordBattlefieldBossKillAsync(MonsterDamageResult damageResult, CancellationToken cancellationToken)
    {
        if (!BattlefieldScoringEnabled || _character is not { } killer || BattlefieldStore is not { } store ||
            damageResult.Monster.Definition.MapId != BattlefieldRewardPolicy.PindusMapId ||
            !string.Equals(damageResult.Monster.Definition.TemplateKey, BattlefieldRewardPolicy.PindusDragonTemplateKey,
                StringComparison.Ordinal) ||
            killer.Camp > 1 || !BattlefieldRewardPolicy.PindusLevelEligible(killer.Level)) // BATTLEFIELD2: band
        {
            return;
        }

        try
        {
            if (BattlefieldRewardPolicy.CurrentRound(BattlefieldRewardPolicy.Pindus, _realmCalendar, DateTimeOffset.UtcNow)
                is not { } round)
            {
                return;
            }
            var credited = await store.RecordBattlefieldTaskPointsAsync(
                BattlefieldRewardPolicy.Pindus, round.Start, round.End, killer.Id, killer.Camp, 0,
                BattlefieldRewardPolicy.PindusDragonPoints, cancellationToken);
            Console.WriteLine(
                $"[battlefield] Pindus Dragon slain character={killer.Name} faction={killer.Camp} " +
                $"round={round.Start:u} points={BattlefieldRewardPolicy.PindusDragonPoints} credited={credited}");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[battlefield] dragon scoring failed character={killer.Name}: {error.Message}");
        }
    }

    private async Task RecordBattlefieldEntryAsync(BattlefieldDestinationKind kind, CancellationToken cancellationToken)
    {
        var battlefield = BattlefieldRewardPolicy.ForDestination(kind);
        if (battlefield == 0 || !BattlefieldScoringEnabled || _character is not { } character ||
            BattlefieldStore is not { } store ||
            battlefield == BattlefieldRewardPolicy.Pindus &&
            !BattlefieldRewardPolicy.PindusLevelEligible(character.Level)) // BATTLEFIELD2: band
        {
            return;
        }

        try
        {
            if (BattlefieldRewardPolicy.CurrentRound(battlefield, _realmCalendar, DateTimeOffset.UtcNow) is not { } round)
            {
                return;   // a GM entering outside the window is not a participant
            }
            await store.RecordBattlefieldParticipantAsync(
                battlefield, round.Start, round.End, character.Id, character.Camp, cancellationToken);
            Console.WriteLine(
                $"[battlefield] participant character={character.Name} battlefield={battlefield} round={round.Start:u}");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[battlefield] participation failed character={character.Name}: {error.Message}");
        }
    }
}
