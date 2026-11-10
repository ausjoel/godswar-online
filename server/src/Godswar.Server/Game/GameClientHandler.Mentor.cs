using System.Buffers.Binary;
using Godswar.Server.Application.Mentor;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

// ECONOMY mentor (Master and Apprentice, IGG guide igg-acid=111). Off unless GODSWAR_MENTOR=1; with it off this file
// returns false before reading anything, so message 10234 keeps the old "[game] unknown" path. Frame layouts and the
// proven/unproven split: Packets/PacketBuilder.Mentor.cs. Every step runs on the acting character's own handler; a
// change that concerns another online character reaches it as a frame (invitation, tip, list) or as a "due" mark its
// own handler picks up at its next packet (graduation rewards). No pop-up windows are added: the tips are the stock
// client's own chat-area lines.
internal sealed partial class GameClientHandler
{
    private bool _mentorLoginChecked;
    private bool _mentorBusy;
    private bool? _mentorIsApprentice;
    private int _mentorSeenLevel = -1;

    /// <summary>Called after any level-up (monster kill incl. party share, quest, EXP item): an apprentice owes its
    /// master a per-level award (STAGE3) and graduates at level 50.</summary>
    private void MarkMentorLevelUp()
    {
        if (!MentorPolicy.Enabled || _mentorBusy || _character is null || _mentorIsApprentice == false)
        {
            return;
        }

        _registry.MarkMentorDue(_character.Id);
    }

    private async Task<bool> TryHandleMentorPacketAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (!MentorPolicy.Enabled || _registry.Mentor is not { } store || _character is null || _account is null)
        {
            return false;
        }

        var isMentorFrame = packet.Opcode == PacketBuilder.MentorOpcode;
        try
        {
            // Every mentor frame (the Friends window sends one each time it opens) also re-checks graduation, so a
            // level gained on a path that does not mark the character due (GM /level) is still picked up.
            // STAGE3: any level change since the last upkeep (a path that did not mark the character due) also runs it.
            if (!_mentorLoginChecked || _registry.TakeMentorDue(_character.Id) ||
                (_mentorIsApprentice != false && _mentorSeenLevel >= 0 && _mentorSeenLevel != _character.Level) ||
                (isMentorFrame && _character.Level >= MentorPolicy.GraduationLevel && _mentorIsApprentice != false))
            {
                await RunMentorUpkeepAsync(store, cancellationToken);
            }

            if (isMentorFrame)
            {
                await HandleMentorFrameAsync(store, packet, cancellationToken);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A mentor fault must never end the session: log it and let the next packet try again.
            Console.Error.WriteLine($"[mentor] fault character={_character?.Name} op={packet.Opcode}: " +
                $"{error.GetType().Name}: {error.Message}");
        }

        return isMentorFrame;
    }

    // Graduation and owed rewards. Runs once after login and whenever this character is marked due.
    private async Task RunMentorUpkeepAsync(IMentorStore store, CancellationToken cancellationToken)
    {
        var character = _character!;
        var self = _registry.FindOnlineCharacter(character.Name);
        if (self is null || !ReferenceEquals(self.Session, _session))
        {
            // Not in the world yet (login or map change): try again at a later packet.
            if (_mentorLoginChecked) _registry.MarkMentorDue(character.Id);
            return;
        }

        _mentorLoginChecked = true;
        _mentorBusy = true;
        _mentorSeenLevel = character.Level;
        try
        {
            // STAGE3: per-level master awards first (before a graduation removes the relationship).
            if (_mentorIsApprentice != false && await store.RecordLevelAwardsAsync(character.Id, cancellationToken) is { } owed)
            {
                _mentorIsApprentice = true;
                _registry.MarkMentorDue(owed);
                Console.WriteLine($"[mentor] level award owed apprentice={character.Name} level={character.Level} master={owed}");
            }

            if (character.Level >= MentorPolicy.GraduationLevel)
            {
                var masterId = await store.GraduateAsync(character.Id, cancellationToken);
                if (masterId is { } master)
                {
                    _mentorIsApprentice = false;
                    _registry.MarkMentorDue(master);
                    Console.WriteLine($"[mentor] graduated apprentice={character.Name} master={master}");
                }
            }

            var claim = await store.ClaimRewardsAsync(character.Id, _account!.Id, cancellationToken);
            if (claim is not null)
            {
                await PayMentorClaimAsync(claim, cancellationToken);
            }
        }
        finally
        {
            _mentorBusy = false;
        }
    }

    private async Task PayMentorClaimAsync(MentorClaim claim, CancellationToken cancellationToken)
    {
        var character = _character!;
        character.Silver = claim.SilverAfter;
        character.BindingGold = claim.BindingGoldAfter;
        character.TalentPoints = claim.TalentPointsAfter;
        var experience = (int)Math.Min(int.MaxValue, claim.Rewards.Sum(reward => reward.Experience));
        // Same progression path and frame order as a quest hand-in (GameClientHandler.Quests.cs): the status first,
        // then one 10030 level-up notice per level, so the client never renders a level it was not told about.
        // STAGE3: a claim of per-level B-Gold awards only carries no EXP; skip the progression write then.
        var progression = experience > 0
            ? await _store.ApplyMonsterKillRewardAsync(_account!.Id, character.Id, experience, 0, cancellationToken)
            : null;
        var levelUps = progression?.LevelUps ?? [];
        if (progression is not null)
        {
            character.Level = progression.CurrentLevel;
            character.Experience = progression.CurrentExperience;
            character.TalentExperience = progression.CurrentTalentExperience;
            character.TalentPoints = progression.CurrentTalentPoints;
        }

        if (levelUps.Count > 0)
        {
            await RefreshLevelUpStatsAsync(character, cancellationToken);
        }

        _registry.UpdateCharacter(_session, character, advanceWorldRevision: false);
        await _session.SendAsync(BuildLocalPlayerStatusUpdate(), cancellationToken, "MentorRewardStatus");
        foreach (var levelUp in levelUps)
        {
            var maximum = PlayerExperienceCatalog.GetClientExperienceMaximum(levelUp.Level, character.FighterLevelSealed);
            await _session.SendAsync(
                PacketBuilder.PlayerLevelUp(LocalPlayerObjectId, levelUp.Level, maximum, levelUp.CurrentExperience,
                    character.MaxHp, character.CurrentHp, character.MaxMp, character.CurrentMp),
                cancellationToken, "MentorRewardLevelUp");
            await _registry.BroadcastToMapAsync(character.CurrentMap,
                PacketBuilder.PlayerLevelUp(CurrentPlayerObjectId, levelUp.Level, maximum, levelUp.CurrentExperience,
                    character.MaxHp, character.CurrentHp, character.MaxMp, character.CurrentMp),
                cancellationToken, _session, "MentorRewardLevelUpWorld");
        }

        if (levelUps.Count > 0)
        {
            await SendQuestLevelUpRefreshAsync(cancellationToken);
        }

        foreach (var reward in claim.Rewards)
        {
            if (reward.IsLevelAward)
            {
                // STAGE3: chat-area line "obtained N B-Gold" only (no pop-up).
                await SendMentorTipAsync(_session, 15, reward.BindingGold, null, cancellationToken);
                Console.WriteLine($"[mentor] level award paid master={character.Name} apprentice={reward.PartnerName} " +
                    $"level={reward.AwardLevel} bgold={reward.BindingGold}");
                continue;
            }

            await SendMentorTipAsync(_session, reward.AsMaster ? 9 : 10, 0, reward.PartnerName, cancellationToken);
            await SendMentorTipAsync(_session, 11, (int)Math.Min(int.MaxValue, reward.Experience), null, cancellationToken);
            await SendMentorTipAsync(_session, 12, reward.TalentPoints, null, cancellationToken);
            await SendMentorTipAsync(_session, 13, reward.Silver, null, cancellationToken);
            if (reward.BindingGold > 0)
                await SendMentorTipAsync(_session, 15, reward.BindingGold, null, cancellationToken);
            Console.WriteLine($"[mentor] graduation reward character={character.Name} role=" +
                $"{(reward.AsMaster ? "master" : "apprentice")} exp={reward.Experience} tp={reward.TalentPoints} " +
                $"silver={reward.Silver} bgold={reward.BindingGold} level={character.Level}");
        }
    }

    private async Task HandleMentorFrameAsync(IMentorStore store, GamePacket packet, CancellationToken cancellationToken)
    {
        var payload = packet.Payload;
        if (payload.Length < PacketBuilder.MentorFrameBytes - 4)
        {
            Console.WriteLine($"[mentor] short frame len={packet.Length}");
            return;
        }

        var operation = BinaryPrimitives.ReadInt32LittleEndian(payload);
        var masterName = PacketBuilder.ReadMentorName(payload.Slice(4, 32));
        var apprenticeName = PacketBuilder.ReadMentorName(payload.Slice(36, 32));
        var character = _character!;
        Console.WriteLine($"[mentor] op={operation} from={character.Name} master='{masterName}' apprentice='{apprenticeName}'");
        switch (operation)
        {
            case 0:
                await InviteApprenticeAsync(store, apprenticeName, cancellationToken);
                break;
            case 2:
                await LeaveMasterAsync(store, cancellationToken);
                break;
            case 3:
                await ExpelApprenticeAsync(store, apprenticeName, cancellationToken);
                break;
            case 5:
                await SendMentorListAsync(store, _session, character.Id, sendEmpty: false, cancellationToken);
                break;
            case 6:
                await AcceptMasterAsync(store, masterName, cancellationToken);
                break;
            case 7:
                await RefuseMasterAsync(masterName, cancellationToken);
                break;
            default:
                Console.WriteLine($"[mentor] unknown operation {operation}");
                break;
        }
    }

    private async Task InviteApprenticeAsync(IMentorStore store, string apprenticeName, CancellationToken cancellationToken)
    {
        var master = _character!;
        var target = _registry.FindOnlineCharacter(apprenticeName);
        var party = _registry.GetPartyMembership(_session);
        if (target is null || target.CharacterId == master.Id || party is null ||
            !party.MemberCharacterIds.Contains(target.CharacterId) ||
            !MentorPolicy.LevelsAllow(master.Level, target.Character.Level))
        {
            await SendMentorTipAsync(_session, 25, 0, null, cancellationToken); // Prentice_Cannot
            return;
        }

        var mine = await store.ReadAsync(master.Id, cancellationToken);
        var theirs = await store.ReadAsync(target.CharacterId, cancellationToken);
        if (theirs.MasterId is not null)
        {
            await SendMentorTipAsync(_session, 27, 0, target.CharacterName, cancellationToken); // AlreadyHadMaster
            return;
        }

        if (mine.MasterId is not null || theirs.Apprentices.Count > 0 || mine.LockedUntil is not null ||
            theirs.LockedUntil is not null)
        {
            await SendMentorTipAsync(_session, 25, 0, null, cancellationToken);
            return;
        }

        if (mine.Apprentices.Count >= MentorPolicy.MaximumApprentices)
        {
            await SendMentorTipAsync(_session, 26, 0, null, cancellationToken); // Prenitce_Full
            return;
        }

        _registry.OpenMentorInvitation(target.CharacterId, master.Id, DateTimeOffset.UtcNow);
        await target.Session.SendAsync(PacketBuilder.MentorInvitation(master.Name, target.CharacterName),
            cancellationToken, "MentorInvitation");
    }

    private async Task AcceptMasterAsync(IMentorStore store, string masterName, CancellationToken cancellationToken)
    {
        var apprentice = _character!;
        var master = _registry.FindOnlineCharacter(masterName);
        if (master is null ||
            !_registry.TakeMentorInvitation(apprentice.Id, master.CharacterId, DateTimeOffset.UtcNow) ||
            !MentorPolicy.LevelsAllow(master.Character.Level, apprentice.Level))
        {
            await SendMentorTipAsync(_session, 25, 0, null, cancellationToken);
            return;
        }

        var status = await store.CreateAsync(master.CharacterId, apprentice.Id, cancellationToken);
        Console.WriteLine($"[mentor] accept apprentice={apprentice.Name} master={master.CharacterName} status={status}");
        switch (status)
        {
            case MentorCreateStatus.Created:
                _mentorIsApprentice = true;
                await SendMentorTipAsync(_session, 29, 0, master.CharacterName, cancellationToken); // SUCCESSFUL
                await SendMentorListAsync(store, _session, apprentice.Id, sendEmpty: false, cancellationToken);
                await SendMentorListAsync(store, master.Session, master.CharacterId, sendEmpty: false, cancellationToken);
                break;
            case MentorCreateStatus.AlreadyHasMaster:
                await SendMentorTipAsync(_session, 27, 0, apprentice.Name, cancellationToken);
                break;
            case MentorCreateStatus.MasterFull:
                await SendMentorTipAsync(_session, 26, 0, null, cancellationToken);
                await SendMentorTipAsync(master.Session, 26, 0, null, cancellationToken);
                break;
            default:
                await SendMentorTipAsync(_session, 25, 0, null, cancellationToken);
                break;
        }
    }

    private async Task RefuseMasterAsync(string masterName, CancellationToken cancellationToken)
    {
        var master = _registry.FindOnlineCharacter(masterName);
        if (master is null ||
            !_registry.TakeMentorInvitation(_character!.Id, master.CharacterId, DateTimeOffset.UtcNow))
        {
            return;
        }

        await SendMentorTipAsync(master.Session, 28, 0, _character.Name, cancellationToken); // Disagreed
    }

    private async Task LeaveMasterAsync(IMentorStore store, CancellationToken cancellationToken)
    {
        var apprentice = _character!;
        var view = await store.ReadAsync(apprentice.Id, cancellationToken);
        var masterId = await store.EndAsync(apprentice.Id, null, cancellationToken);
        if (masterId is null)
        {
            return;
        }

        _mentorIsApprentice = false;
        await SendMentorTipAsync(_session, 19, 0, view.Master?.Name, cancellationToken); // AbandonMaster
        await SendMentorListAsync(store, _session, apprentice.Id, sendEmpty: true, cancellationToken);
        if (view.Master is { } master && _registry.FindOnlineCharacter(master.Name) is { } online)
        {
            await SendMentorTipAsync(online.Session, 22, 0, apprentice.Name, cancellationToken); // BeAbandonedByPrentice
            await SendMentorListAsync(store, online.Session, online.CharacterId, sendEmpty: true, cancellationToken);
        }
    }

    private async Task ExpelApprenticeAsync(IMentorStore store, string apprenticeName, CancellationToken cancellationToken)
    {
        var master = _character!;
        var view = await store.ReadAsync(master.Id, cancellationToken);
        var apprentice = view.MasterId is null
            ? view.Apprentices.FirstOrDefault(row => string.Equals(row.Name, apprenticeName, StringComparison.OrdinalIgnoreCase))
            : null;
        if (apprentice is null || await store.EndAsync(apprentice.CharacterId, master.Id, cancellationToken) is null)
        {
            await SendMentorTipAsync(_session, 25, 0, null, cancellationToken);
            return;
        }

        await SendMentorTipAsync(_session, 20, 0, apprentice.Name, cancellationToken); // AbandonPrentice
        await SendMentorListAsync(store, _session, master.Id, sendEmpty: true, cancellationToken);
        if (_registry.FindOnlineCharacter(apprentice.Name) is { } online)
        {
            await SendMentorTipAsync(online.Session, 21, 0, master.Name, cancellationToken); // BeAbandonedByMaster
            await SendMentorListAsync(store, online.Session, online.CharacterId, sendEmpty: true, cancellationToken);
        }
    }

    // The list goes only to a character that has a relationship. sendEmpty clears a window after a relationship ended
    // (an empty frame is UNPROVEN on a real client; see the README).
    private async Task SendMentorListAsync(IMentorStore store, ClientSession session, int characterId, bool sendEmpty,
        CancellationToken cancellationToken)
    {
        var view = await store.ReadAsync(characterId, cancellationToken);
        if (!view.HasRelationship && !sendEmpty)
        {
            return;
        }

        PacketBuilder.MentorListRow Row(MentorMember member)
        {
            var online = _registry.FindOnlineCharacter(member.Name);
            return new(member.Name, (ushort)Math.Clamp(online?.Character.CurrentMap ?? member.MapId, 0, ushort.MaxValue),
                (ushort)Math.Clamp(online?.Character.Level ?? member.Level, 0, ushort.MaxValue), online is not null);
        }

        var frame = view.HasRelationship
            ? PacketBuilder.MentorList(Row(view.Master!), view.Apprentices.Select(Row).ToArray())
            : PacketBuilder.MentorList(null, []);
        await session.SendAsync(frame, cancellationToken, "MentorList");
    }

    private static Task SendMentorTipAsync(ClientSession session, int code, int value, string? name,
        CancellationToken cancellationToken) =>
        session.SendAsync(PacketBuilder.MentorTip(code, value, name), cancellationToken, "MentorTip");
}
