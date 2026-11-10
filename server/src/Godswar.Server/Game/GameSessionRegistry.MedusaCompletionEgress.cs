using System.Collections.Concurrent;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private const string HeirOfPerseusAnnouncementTitle =
        "'Heir of Perseus'";
    private const string CrimsonHeirOfPerseusAnnouncementTitle =
        "|cffDC143C'Heir of Perseus'|cFFFFFFFF";

    private readonly ConcurrentDictionary<WorldInstanceId, byte>
        _medusaCompletionEgressInFlight = [];
    private readonly ConcurrentDictionary<WorldInstanceId, byte>
        _medusaCompletionRewardSettled = [];
    private readonly ConcurrentDictionary<WorldInstanceId, byte>
        _medusaCompletionExitSettled = [];

    private MedusaCompletionEgress? CaptureMedusaCompletionEgress(
        WorldInstanceRuntime runtime,
        DateTimeOffset now)
    {
        if (_medusaCompletionExitSettled.ContainsKey(runtime.InstanceId))
        {
            return null;
        }

        var ownership = InvokeWorldOwner(
            runtime,
            static map => map.TryGetMedusaOwnershipSnapshot(
                    out var snapshot)
                ? snapshot
                : null);
        var run = ownership?.Run;
        if (run is not
            {
                State: MedusaRunState.Completed,
                CompletionMarker: not null
            } ||
            !MedusaIslandRosterPolicy.TryResolveClientSceneIdByContentMap(
                ownership!.ContentMapId.Value, out var clientSceneId))
        {
            return null;
        }

        // Nobody's click carries the party out: the member who pressed leave
        // during the countdown left on his own, through the shared clicker-only
        // leave, and everyone still inside is carried home when the shared
        // thirty-second countdown expires.
        var exitPlayers =
            now >= run.CompletionMarker.Value.CompletedAt +
                InstanceRunEndDelay;
        if (!exitPlayers)
        {
            return null;
        }

        var members = new List<MedusaCompletionEgressMember>();
        lock (_gate)
        {
            foreach (var context in _sessions.Values)
            {
                if (!context.WorldReady ||
                    context.Session.IsDisconnected ||
                    context.WorldInstanceId != runtime.InstanceId ||
                    context.Character.CurrentMap != context.MapId ||
                    context.MapId is not (200 or 204) ||
                    !context.Ownership.IsValid ||
                    !IsCurrentAccountSession(
                        context.AccountId,
                        context.Session,
                        context.Ownership))
                {
                    continue;
                }

                members.Add(new(
                    context.Session,
                    context.CharacterId,
                    context.Character.Name,
                    context.WorldInstanceId,
                    context.MapId,
                    context.Ownership,
                    context.Character.Camp));
            }
        }

        var leaderId = InstanceRunLeaderCharacterId(runtime.InstanceId);
        return new(
                runtime.InstanceId,
                runtime.RealmId,
                run.Difficulty,
                leaderId != 0 ? leaderId : run.AdmittedCharacterIds.FirstOrDefault(),
                run.CompletionMarker.Value,
                run.AdmittedCharacterIds.ToArray(),
                members,
                ExitPlayers: true,
                BeginInstanceRunEnd(runtime.InstanceId, InstanceRunKind.Medusa,
                    "Medusa", runtime.MapId, checked((ushort)clientSceneId),
                    MedusaDailyEntryLimit(runtime.InstanceId), run.TeamScore,
                    run.CompletionMarker.Value.CompletedAt));
    }

    private async Task PublishMedusaCompletionEgressAsync(
        MedusaCompletionEgress egress,
        CancellationToken cancellationToken)
    {
        if (!_medusaCompletionEgressInFlight.TryAdd(
                egress.SourceWorldInstanceId,
                0))
        {
            return;
        }

        try
        {
            if (!await SettleMedusaCompletionRewardAsync(
                    egress,
                    cancellationToken))
            {
                return;
            }
            if (!egress.ExitPlayers)
            {
                return;
            }

            // The leader's registration is dropped once the run's panel is done;
            // the native list clear is not sent here, because the shared
            // end-of-run flow sends the one 10231 with zero seconds when the
            // member actually leaves the instance. An empty 10218 roster is never
            // published: the stock handler reads a first member when the count is
            // zero.
            _medusaLeaderUi.TryRemove(egress.SourceWorldInstanceId, out _);

            // The one automatic exit every dungeon shares. Only the members this
            // capture still found inside are carried home; whoever pressed leave
            // during the countdown already left on his own.
            var allTransferred = await EgressInstanceRunEndAsync(
                egress.Ending,
                [.. egress.Members.Select(static member => new InstanceRunEndMember(
                    member.Session, member.CharacterId, member.Ownership,
                    member.Camp))],
                TransitionMedusaMemberHomeAsync,
                cancellationToken);

            if (allTransferred)
            {
                _medusaCompletionExitSettled.TryAdd(
                    egress.SourceWorldInstanceId,
                    0);
                ForgetInstanceRunEnd(egress.SourceWorldInstanceId);
                Console.WriteLine(
                    "[instance] Medusa final score " +
                    $"instance={egress.SourceWorldInstanceId} " +
                    $"points={egress.Completion.FinalScore}");
            }
        }
        finally
        {
            _medusaCompletionEgressInFlight.TryRemove(
                egress.SourceWorldInstanceId,
                out _);
        }
    }

    private async Task<bool> SettleMedusaCompletionRewardAsync(
        MedusaCompletionEgress egress,
        CancellationToken cancellationToken)
    {
        if (!MedusaCompletionRewardPolicy.SupportsSettlement(
                egress.Difficulty))
        {
            _medusaCompletionRewardSettled.TryAdd(
                egress.SourceWorldInstanceId,
                0);
            return true;
        }

        var store = _medusaCompletionRewards;
        if (store is null ||
            _medusaCompletionRewardSettled.ContainsKey(
                egress.SourceWorldInstanceId))
        {
            return true;
        }

        MedusaCompletionRewardReceipt receipt;
        try
        {
            // Entitlement is the instance's own result granted to every character
            // still inside it at completion: a member who confirmed the party
            // window after the run was sealed is entitled exactly like the
            // leader, and a character who left or dropped before this moment is
            // not. The egress list itself stays untouched, so whoever must be
            // carried home still is.
            var recipients = egress.Members
                .Select(member => member.CharacterId)
                .Distinct()
                .Order()
                .ToArray();
            if (recipients.Length == 0)
            {
                _medusaCompletionRewardSettled.TryAdd(
                    egress.SourceWorldInstanceId,
                    0);
                return true;
            }
            var request = new MedusaCompletionRewardRequest(
                egress.SourceWorldInstanceId,
                egress.RealmId,
                egress.Difficulty,
                egress.Completed,
                egress.Completion.CompletedAt,
                egress.Completion.Elapsed,
                egress.Completion.FinalScore,
                recipients);
            receipt = await store.SettleAsync(request, cancellationToken);
        }
        catch (Exception error) when (
            error is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                "[instance] Medusa completion reward will retry " +
                $"instance={egress.SourceWorldInstanceId}: " +
                error.Message);
            return false;
        }

        if (!receipt.Succeeded)
        {
            Console.Error.WriteLine(
                "[instance] Medusa completion reward rejected " +
                $"instance={egress.SourceWorldInstanceId} " +
                $"status={receipt.Status}");
            return false;
        }

        _medusaCompletionRewardSettled.TryAdd(
            egress.SourceWorldInstanceId,
            0);
        ApplyMedusaRewardProjections(egress, receipt);
        if (receipt.Status == MedusaCompletionRewardStatus.Applied)
        {
            await PublishMedusaRewardPacketsAsync(
                egress,
                receipt,
                cancellationToken);
            var leaderName = egress.Members.FirstOrDefault(member =>
                    member.CharacterId == egress.LeaderCharacterId)
                .CharacterName ??
                egress.Members.FirstOrDefault().CharacterName;
            if (!string.IsNullOrWhiteSpace(leaderName))
            {
                await PublishMedusaFactionNoticeAsync(
                    receipt,
                    leaderName,
                    cancellationToken);
            }
        }

        Console.WriteLine(
            "[instance] Medusa completion reward " +
            $"instance={egress.SourceWorldInstanceId} " +
            $"status={receipt.Status} " +
            $"hard-points={receipt.Award.HardPoints} " +
            $"title={receipt.Award.Title?.DisplayName ?? "none"}");
        return true;
    }

    private void ApplyMedusaRewardProjections(
        MedusaCompletionEgress egress,
        MedusaCompletionRewardReceipt receipt)
    {
        var rewards = receipt.Members.ToDictionary(
            static member => member.CharacterId);
        lock (_gate)
        {
            foreach (var member in egress.Members)
            {
                if (!rewards.TryGetValue(
                        member.CharacterId,
                        out var reward) ||
                    !_sessions.TryGetValue(
                        member.Session,
                        out var current) ||
                    current.CharacterId != member.CharacterId ||
                    current.WorldInstanceId != member.SourceWorldInstanceId ||
                    current.Ownership != member.Ownership ||
                    !IsCurrentAccountSession(
                        current.AccountId,
                        current.Session,
                        current.Ownership))
                {
                    continue;
                }

                ApplyMedusaRewardProjection(current.Character, reward);
            }
        }
    }

    internal static void ApplyMedusaRewardProjection(
        GameCharacter character,
        MedusaCompletionRewardMember reward)
    {
        if (reward.AwardedTitleId != 0)
            character.AddOwnedTitle(reward.AwardedTitleId);

        // Manual title selection shares this revision with reward writes.
        // A delayed receipt still grants ownership, but cannot undo a later
        // selected title or replace its newer wallet projection.
        if (character.MedusaRewardRevision > reward.RewardRevision) return;

        character.MedusaHonorPoints = reward.HonorAfter;
        character.MedusaRewardRevision = reward.RewardRevision;
        if (reward.AwardedTitleId != 0)
            character.SelectedTitleId = reward.AwardedTitleId;
    }

    private async Task PublishMedusaRewardPacketsAsync(
        MedusaCompletionEgress egress,
        MedusaCompletionRewardReceipt receipt,
        CancellationToken cancellationToken)
    {
        var rewardedMembers = receipt.Members.ToDictionary(
            static member => member.CharacterId);
        foreach (var member in egress.Members)
        {
            if (!rewardedMembers.TryGetValue(
                    member.CharacterId,
                    out var reward))
            {
                continue;
            }

            byte[]? designationPacket = null;
            byte[]? selectedTitlePacket = null;
            byte[]? honorDetailPacket = null;
            lock (_gate)
            {
                if (_sessions.TryGetValue(
                        member.Session,
                        out var current) &&
                    current.CharacterId == member.CharacterId &&
                    current.WorldInstanceId ==
                        member.SourceWorldInstanceId &&
                    current.Ownership == member.Ownership &&
                    IsCurrentAccountSession(
                        current.AccountId,
                        current.Session,
                        current.Ownership))
                {
                    honorDetailPacket =
                        PacketBuilder.PlayerDetail(current.Character);
                    if (reward.AwardedTitleId != 0)
                    {
                        designationPacket =
                            PacketBuilder.MedusaDesignationInfo(
                                current.Character.SelectedTitleId,
                                current.Character.OwnedTitleIds);
                        selectedTitlePacket = PacketBuilder.PlayerTitleInfo(
                            current.Character,
                            current.ObjectId);
                    }
                }
            }

            try
            {
                if (designationPacket is not null &&
                    selectedTitlePacket is not null)
                {
                    await member.Session.SendAsync(
                        designationPacket,
                        cancellationToken,
                        "MedusaTitleOwnership");
                    await member.Session.SendAsync(
                        selectedTitlePacket,
                        cancellationToken,
                        "MedusaSelectedTitleSelf");
                    await BroadcastToWorldInstanceAsync(
                        member.SourceWorldInstanceId,
                        selectedTitlePacket,
                        cancellationToken,
                        member.Session,
                        "MedusaSelectedTitleObservers");
                }

                if (receipt.Award.HardPoints > 0)
                {
                    await member.Session.SendAsync(
                        PacketBuilder.RepetitionReward(
                            receipt.Award.HardPoints),
                        cancellationToken,
                        "MedusaCompletionHardPoints");
                }
                if (honorDetailPacket is not null)
                {
                    await member.Session.SendAsync(
                        honorDetailPacket,
                        cancellationToken,
                        "MedusaCompletionHonorRefresh",
                        framed: false);
                }
            }
            catch (Exception error) when (
                error is IOException or ObjectDisposedException)
            {
                Remove(member.Session);
            }
        }
    }

    internal async Task<int> PublishMedusaFactionNoticeAsync(
        MedusaCompletionRewardReceipt receipt,
        string teamLeaderName,
        CancellationToken cancellationToken)
    {
        var camps = receipt.Members
            .Select(static member => member.Camp)
            .ToHashSet();
        ClientSession[] recipients;
        lock (_gate)
        {
            recipients = _sessions.Values
                .Where(context =>
                    context.WorldReady &&
                    !context.Session.IsDisconnected &&
                    camps.Contains(context.Character.Camp) &&
                    IsCurrentAccountSession(
                        context.AccountId,
                        context.Session,
                        context.Ownership))
                .Select(static context => context.Session)
                .Distinct()
                .ToArray();
        }

        var packet = PacketBuilder.CenteredAnnouncement(
            BuildMedusaFactionAnnouncement(
                teamLeaderName,
                receipt.Award.NotificationText));
        var delivered = 0;
        foreach (var recipient in recipients)
        {
            try
            {
                await recipient.SendAsync(
                    packet,
                    cancellationToken,
                    "MedusaCompletionFactionNotice");
                delivered++;
            }
            catch (Exception error) when (
                error is IOException or ObjectDisposedException)
            {
                Remove(recipient);
            }
        }

        return delivered;
    }

    internal static string BuildMedusaFactionAnnouncement(
        string teamLeaderName,
        string notificationText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamLeaderName);
        ArgumentException.ThrowIfNullOrWhiteSpace(notificationText);
        const string genericSubject = "The team";
        var body = notificationText.StartsWith(
                genericSubject,
                StringComparison.Ordinal)
            ? notificationText[genericSubject.Length..]
            : $" {notificationText}";
        body = body.Replace(
            HeirOfPerseusAnnouncementTitle,
            CrimsonHeirOfPerseusAnnouncementTitle,
            StringComparison.Ordinal);
        var message = $"{teamLeaderName}'s Team{body}";
        if (message.Length <=
            PacketBuilder.CenteredAnnouncementMaximumTextLength)
        {
            return message;
        }

        var compactBody = body
            .Replace(
                " has defeated Medusa within ",
                " cleared Medusa in ",
                StringComparison.Ordinal)
            .Replace(
                " and earned the title of ",
                " and earned ",
                StringComparison.Ordinal);
        return $"{teamLeaderName}'s Team{compactBody}";
    }

    private sealed record MedusaCompletionEgress(
        WorldInstanceId SourceWorldInstanceId,
        RealmId RealmId,
        MedusaEncounterDifficulty Difficulty,
        int LeaderCharacterId,
        MedusaRunCompletionMarker Completion,
        IReadOnlyList<int> AdmittedCharacterIds,
        IReadOnlyList<MedusaCompletionEgressMember> Members,
        bool ExitPlayers,
        InstanceRunEnding Ending,
        bool Completed = true);

    private readonly record struct MedusaCompletionEgressMember(
        ClientSession Session,
        int CharacterId,
        string CharacterName,
        WorldInstanceId SourceWorldInstanceId,
        byte SourceMapId,
        PlayerOwnershipFence Ownership,
        byte Camp);
}
