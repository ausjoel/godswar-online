using Godswar.Server.Networking;

namespace Godswar.Server.Game;

// PARTY (r02): party kill sharing - the registry half.
//
// the admin's rule (Development/reference-analysis/DECISIONS-2026-11-03.md r02):
// everyone in the party on the same map near the kill gets kill-quest credit,
// and the kill EXP is split equally between them. The IGG guide (acid=45) says
// only "The quests completed can be shared by them on the same map".
//
// This file only answers "who is eligible"; the paying half is
// GameClientHandler.PartyKillShare.cs. Nothing here mutates a party.

/// <summary>A party member who shares a kill, captured under the registry gate.</summary>
internal sealed record PartyKillShareCandidate(
    int CharacterId,
    string CharacterName,
    int Level,
    Func<PartyKillShareFact, bool> Receive);

/// <summary>
/// One immutable kill fact handed to a sharing party member. The member's own
/// packet loop applies it under that member's character gate.
/// </summary>
internal sealed record PartyKillShareFact(
    MonsterDamageResult DamageResult,
    Guid DeathEventId,
    int ShareExperience,
    int ShareTalentExperience,
    int Divisor,
    string ClaimantName);

internal sealed partial class GameSessionRegistry
{
    internal void RegisterPartyKillShareReceiver(
        ClientSession session,
        Func<PartyKillShareFact, bool> receiver)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(receiver);
        lock (_gate)
        {
            if (_sessions.TryGetValue(session, out var context))
            {
                _sessions[session] = context with
                {
                    ReceivePartyKillShare = receiver
                };
            }
        }
    }

    /// <summary>
    /// The claimant's party members who share this kill: online and world
    /// ready, in the claimant's realm, world instance and map, alive, and within
    /// <paramref name="cellRadius"/> 32-unit cells of the cell the monster died
    /// in. The claimant itself is never listed. An empty list means a solo kill.
    /// </summary>
    internal IReadOnlyList<PartyKillShareCandidate> GetPartyKillShareCandidates(
        ClientSession claimantSession,
        MonsterDamageResult damageResult,
        int cellRadius)
    {
        ArgumentNullException.ThrowIfNull(claimantSession);
        ArgumentNullException.ThrowIfNull(damageResult);
        if (cellRadius < 0 ||
            !WorldSectorVisibilityTracker<object>.TryGetCell(
                damageResult.Monster.X,
                damageResult.Monster.Z,
                out var killCell))
        {
            return [];
        }

        lock (_gate)
        {
            if (!_sessions.TryGetValue(claimantSession, out var claimant) ||
                !_partiesByCharacter.TryGetValue(
                    claimant.CharacterId,
                    out var party))
            {
                return [];
            }

            var candidates = new List<PartyKillShareCandidate>();
            foreach (var characterId in party.MemberCharacterIds)
            {
                if (characterId == claimant.CharacterId ||
                    !TryFindOnlinePartyMemberByIdLocked(
                        characterId,
                        out var member) ||
                    member.ReceivePartyKillShare is null ||
                    !IsPartyKillShareEligible(
                        claimant,
                        member,
                        killCell,
                        cellRadius))
                {
                    continue;
                }

                candidates.Add(new PartyKillShareCandidate(
                    member.CharacterId,
                    member.DisplayName,
                    member.Character.Level,
                    member.ReceivePartyKillShare));
            }

            return candidates;
        }
    }

    internal static bool IsPartyKillShareEligible(
        GameSessionContext claimant,
        GameSessionContext member,
        WorldGridCell killCell,
        int cellRadius) =>
        member.WorldReady &&
        !member.Session.IsDisconnected &&
        member.RealmId == claimant.RealmId &&
        member.WorldInstanceId == claimant.WorldInstanceId &&
        member.MapId == claimant.MapId &&
        member.Character.CurrentMap == claimant.MapId &&
        member.Character.CurrentHp > 0 &&
        IsWithinPartyKillShareRange(
            killCell,
            member.Character.PositionX,
            member.Character.PositionZ,
            cellRadius);

    /// <summary>
    /// "Near the kill": the member's 32-unit world cell is at most
    /// <paramref name="cellRadius"/> cells from the kill cell on both axes.
    /// </summary>
    /// <remarks>
    /// Inferred, not retail text. The 2008 server shares team EXP only with
    /// teammates for whom <c>Map::IsTwoCellCross(myCell, mateCell)</c> holds
    /// (GodsWar-1.0 PlayerAttack.cpp CalculateExpAndLevel); that function's
    /// body is not in the archive. Reborn's world cells are 32 units with a
    /// 3x3 view (WorldSectorVisibilityTracker), and two 3x3 views cross when
    /// their centres are at most two cells apart, hence the default radius 2
    /// (server_rates party.kill_share_cells).
    /// </remarks>
    internal static bool IsWithinPartyKillShareRange(
        WorldGridCell killCell,
        float x,
        float z,
        int cellRadius) =>
        WorldSectorVisibilityTracker<object>.TryGetCell(
            x,
            z,
            out var memberCell) &&
        Math.Abs((long)memberCell.X - killCell.X) <= cellRadius &&
        Math.Abs((long)memberCell.Z - killCell.Z) <= cellRadius;
}
