using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

/// <summary>
/// ARENA build: player-versus-player admission for the Duel Arena.
/// </summary>
/// <remarks>
/// IGG "Duel Arena" (igg-acid=372), PvP rules:
/// "2. Adventurers in the combat area are allowed to attack one another regardless
/// of faction. 3. ... adventurers from the same faction are allowed to team up.
/// Players who have teamed up will not be able to cause damage to teammates.
/// 4. There are no time, spell or mode limits in the combat area."
/// <para>
/// Every player-versus-player admission goes through
/// <see cref="EvaluatePlayerPvpAdmission"/>. With <c>GODSWAR_DUEL_ARENA_PVP</c> off,
/// or for any pair that is not both inside the arena combat area, it returns
/// exactly what <see cref="PvpWorldAuthorityCatalog.EvaluateOpposingFaction"/>
/// returns, so every other map keeps its default-deny faction rule. Inside the
/// combat area two players who are not in the same party receive a
/// <see cref="PvpEntitlementKind.MutualDuel"/> grant (the duel entitlement the
/// catalog reserved, never issued before this build) and are then evaluated by
/// the same <see cref="PvpCombatEligibilityPolicy"/> as faction PvP.
/// </para>
/// </remarks>
internal sealed partial class GameSessionRegistry
{
    private readonly ConcurrentDictionary<int, (int Wins, int Losses)>
        _duelArenaRecords = new();

    internal PvpEligibilityResult EvaluatePlayerPvpAdmission(
        GameCharacter attacker,
        GameCharacter target,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(target);
        if (!ArenaFeatureGates.DuelArenaPvp ||
            !DuelArenaCombatArea.Contains(
                attacker.CurrentMap,
                attacker.PositionX,
                attacker.PositionZ) ||
            !DuelArenaCombatArea.Contains(
                target.CurrentMap,
                target.PositionX,
                target.PositionZ))
        {
            return _gameplayCatalogs.PvpWorldAuthority
                .EvaluateOpposingFaction(attacker, target, now);
        }

        bool sameParty;
        lock (_gate)
        {
            sameParty =
                _partiesByCharacter.TryGetValue(attacker.Id, out var first) &&
                _partiesByCharacter.TryGetValue(target.Id, out var second) &&
                ReferenceEquals(first, second);
        }

        return EvaluateDuelArenaPair(attacker, target, now, sameParty);
    }

    /// <summary>The arena rule itself, without registry state (checked directly).</summary>
    internal static PvpEligibilityResult EvaluateDuelArenaPair(
        GameCharacter attacker,
        GameCharacter target,
        DateTimeOffset now,
        bool sameParty)
    {
        var attackerParticipant = new PvpCombatParticipant(
            attacker.Id,
            attacker.CurrentMap,
            attacker.CurrentHp > 0,
            IsInSafeZone: false,
            attacker.Camp);
        var targetParticipant = new PvpCombatParticipant(
            target.Id,
            target.CurrentMap,
            target.CurrentHp > 0,
            IsInSafeZone: false,
            target.Camp);
        var entitlement = sameParty
            ? (PvpCombatEntitlement?)null
            : new PvpCombatEntitlement(
                CreateDuelArenaEntitlementId(
                    attacker.Id,
                    target.Id,
                    attacker.CurrentMap),
                PvpEntitlementKind.MutualDuel,
                attacker.Id,
                target.Id,
                attacker.CurrentMap,
                now.AddSeconds(-1),
                now.AddSeconds(5),
                attacker.Camp,
                target.Camp);
        return PvpCombatEligibilityPolicy.Evaluate(
            attackerParticipant,
            targetParticipant,
            entitlement,
            now);
    }

    /// <summary>
    /// GENERATED win/loss handling (the IGG page names no reward): every player on
    /// the arena map is told who defeated whom, with each duellist's tally since the
    /// server started. Nothing is paid and nothing is taken; faction kill credit and
    /// quest player kills are untouched (quest player kills already ignore
    /// same-faction victims and only count on map 68).
    /// </summary>
    private async Task AnnounceDuelArenaDefeatsAsync(
        PvpBasicAttackDecision decision,
        CancellationToken cancellationToken)
    {
        if (!decision.Accepted ||
            decision.Eligibility.EntitlementKind != PvpEntitlementKind.MutualDuel ||
            decision.Attacker is not { } attacker ||
            decision.Target is not { } target)
        {
            return;
        }

        var defeats = new List<(GameSessionContext Winner, GameSessionContext Loser)>();
        if (decision.TargetKilled)
        {
            defeats.Add((attacker, target));
        }
        if (decision.AttackerKilled)
        {
            defeats.Add((target, attacker));
        }

        foreach (var (winner, loser) in defeats)
        {
            var won = _duelArenaRecords.AddOrUpdate(
                winner.CharacterId, (1, 0), static (_, value) => (value.Wins + 1, value.Losses));
            var lost = _duelArenaRecords.AddOrUpdate(
                loser.CharacterId, (0, 1), static (_, value) => (value.Wins, value.Losses + 1));
            var line = DuelArenaDefeatLine(
                winner.CharacterName, won, loser.CharacterName, lost);
            Console.WriteLine(
                $"[duel-arena] defeat winner={winner.CharacterName} loser={loser.CharacterName} " +
                $"record={won.Wins}-{won.Losses}/{lost.Wins}-{lost.Losses}");
            foreach (var recipient in GetWorldInstanceSessions(attacker.WorldInstanceId))
            {
                if (recipient.MapId != DuelArenaCombatArea.MapId)
                {
                    continue;
                }
                try
                {
                    await recipient.Session.SendAsync(
                        PacketBuilder.ServerNote(line),
                        cancellationToken,
                        "DuelArenaDefeat");
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Console.WriteLine($"[duel-arena] defeat notice failed: {error.Message}");
                }
            }
        }
    }

    internal static string DuelArenaDefeatLine(
        string winner,
        (int Wins, int Losses) winnerRecord,
        string loser,
        (int Wins, int Losses) loserRecord)
    {
        static string Ascii(string value) =>
            new(value.Select(static c => c is >= ' ' and <= '~' ? c : '?').ToArray());
        return $"Duel Arena: {Ascii(winner)} ({winnerRecord.Wins}W {winnerRecord.Losses}L) " +
            $"defeated {Ascii(loser)} ({loserRecord.Wins}W {loserRecord.Losses}L).";
    }

    private static Guid CreateDuelArenaEntitlementId(
        int attackerId,
        int targetId,
        byte mapId)
    {
        Span<byte> source = stackalloc byte[13];
        BinaryPrimitives.WriteInt32LittleEndian(source, attackerId);
        BinaryPrimitives.WriteInt32LittleEndian(source[4..], targetId);
        source[8] = mapId;
        BinaryPrimitives.WriteUInt32LittleEndian(
            source[9..],
            0x4C455544); // "DUEL"
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(source, digest);
        return new Guid(digest[..16]);
    }
}
