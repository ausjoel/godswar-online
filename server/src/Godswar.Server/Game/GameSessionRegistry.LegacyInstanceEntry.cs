using Godswar.Server.Application.Npc;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal LegacyInstanceEntryStatus TryCaptureLegacyInstanceParty(
        ClientSession requestingSession,
        InstanceCallerEntryDestination destination,
        float npcX,
        float npcZ,
        float maximumDistance,
        out LegacyInstancePartySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(requestingSession);
        ArgumentNullException.ThrowIfNull(destination);
        if (!float.IsFinite(npcX) ||
            !float.IsFinite(npcZ) ||
            !float.IsFinite(maximumDistance) ||
            maximumDistance <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDistance));
        }

        lock (_gate)
        {
            return TryCaptureLegacyInstancePartyLocked(
                requestingSession,
                destination,
                npcX,
                npcZ,
                maximumDistance,
                requireLeader: true,
                out snapshot,
                out _);
        }
    }

    private LegacyInstanceEntryStatus TryCaptureLegacyInstancePartyLocked(
        ClientSession requestingSession,
        InstanceCallerEntryDestination destination,
        float npcX,
        float npcZ,
        float maximumDistance,
        bool requireLeader,
        out LegacyInstancePartySnapshot snapshot,
        out bool requesterIsLeader)
    {
        snapshot = null!;
        requesterIsLeader = false;
        if (!_sessions.TryGetValue(
                requestingSession,
                out var requester) ||
            !IsLegacyInstanceEntryReady(
                requester,
                requester.WorldInstanceId,
                requester.MapId,
                npcX,
                npcZ,
                maximumDistance))
        {
            return LegacyInstanceEntryStatus.PartyUnavailable;
        }

        PartyState? party = null;
        if (_partiesByCharacter.TryGetValue(
                requester.CharacterId,
                out party))
        {
            NormalizePartyLocked(party);
            if (!_partiesByCharacter.TryGetValue(
                    requester.CharacterId,
                    out party))
            {
                party = null;
            }
        }

        var leaderCharacterId = party?.MemberCharacterIds[0] ??
            requester.CharacterId;
        requesterIsLeader = requester.CharacterId == leaderCharacterId;
        if (requireLeader && !requesterIsLeader)
        {
            return LegacyInstanceEntryStatus.LeaderRequired;
        }
        if (!TryFindCurrentContextLocked(
                leaderCharacterId,
                out var leader) ||
            !IsLegacyInstanceEntryReady(
                leader,
                requester.WorldInstanceId,
                requester.MapId,
                npcX,
                npcZ,
                maximumDistance))
        {
            return LegacyInstanceEntryStatus.PartyUnavailable;
        }

        var characterIds = party?.MemberCharacterIds.ToArray() ??
            [leader.CharacterId];
        if (destination.RequiredPartySize is { } required)
        {
            if (characterIds.Length < required)
            {
                return LegacyInstanceEntryStatus.PartyTooSmall;
            }
            if (characterIds.Length > required)
            {
                return LegacyInstanceEntryStatus.PartyTooLarge;
            }
        }
        else
        {
            // The cap is the shared party maximum; a destination may also demand
            // a minimum roster (港湾遇袭 requires at least three players).
            if (characterIds.Length > PartyProtocol.MaximumMembers)
            {
                return LegacyInstanceEntryStatus.PartyTooLarge;
            }
            if (characterIds.Length < destination.MinimumPartySize)
            {
                return LegacyInstanceEntryStatus.PartyTooSmall;
            }
        }

        var members = new List<LegacyInstancePartyMember>(
            characterIds.Length);
        foreach (var characterId in characterIds)
        {
            if (!TryFindCurrentContextLocked(
                    characterId,
                    out var member) ||
                !IsLegacyInstanceEntryReady(
                    member,
                    leader.WorldInstanceId,
                    leader.MapId,
                    npcX,
                    npcZ,
                    maximumDistance) ||
                member.CharacterId != leader.CharacterId &&
                !_authoritativeInstanceTransitionSinks.ContainsKey(
                    member.Session))
            {
                return LegacyInstanceEntryStatus.PartyUnavailable;
            }
            // FINALISE: editor level band (public.level_band_overrides), read hot.
            var (bandMinimum, bandMaximum) = LevelBandGates.Apply(
                // WARRIOR (F-51): the same kind -> gate map, now including the four Warriors' grounds.
                Godswar.Server.Application.WorldInstances.LegacyInstanceAdmissionPolicy.GateKey(destination.Kind),
                destination.MinimumLevel,
                destination.MaximumLevel);
            if (member.Character.Level < bandMinimum ||
                member.Character.Level > bandMaximum)
            {
                return LegacyInstanceEntryStatus.LevelRequirementNotMet;
            }

            members.Add(new LegacyInstancePartyMember(
                member.Session,
                member.AccountId,
                member.CharacterId,
                member.CharacterName,
                member.Character.Level,
                member.Character.Profession,
                member.RealmId,
                member.WorldInstanceId,
                member.MapId,
                member.Ownership));
        }

        snapshot = new LegacyInstancePartySnapshot(
            party?.Id,
            leader.RealmId,
            leader.CharacterId,
            members.ToArray());
        return LegacyInstanceEntryStatus.Ready;
    }

    private bool IsLegacyInstanceEntryReady(
        GameSessionContext context,
        Domain.World.Instances.WorldInstanceId sourceWorldInstanceId,
        byte sourceMapId,
        float npcX,
        float npcZ,
        float maximumDistance)
    {
        var deltaX = (double)context.Character.PositionX - npcX;
        var deltaZ = (double)context.Character.PositionZ - npcZ;
        var maximum = (double)maximumDistance;
        return context.WorldReady &&
            !context.Session.IsDisconnected &&
            context.WorldInstanceId == sourceWorldInstanceId &&
            context.MapId == sourceMapId &&
            context.Character.CurrentMap == sourceMapId &&
            !Domain.World.Instances.DynamicDungeonContentMapPolicy
                .IsDynamicDungeonMap(sourceMapId) &&
            context.Ownership.IsValid &&
            double.IsFinite(deltaX) &&
            double.IsFinite(deltaZ) &&
            deltaX * deltaX + deltaZ * deltaZ <= maximum * maximum &&
            IsCurrentAccountSession(
                context.AccountId,
                context.Session,
                context.Ownership);
    }
}
