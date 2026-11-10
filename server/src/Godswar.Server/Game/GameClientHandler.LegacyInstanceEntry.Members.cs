using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task TransferLegacyInstanceFollowersAsync(
        LegacyInstancePartySnapshot party, InstanceCallerEntryDestination destination,
        NpcSpawnDefinition npc, WorldInstanceDescriptor target, Guid reservationId, bool opalsCharged,
        IReadOnlySet<int>? confirmedFollowerCharacterIds = null)
    {
        var leader = party.Members[0];
        var failedMembers = new List<LegacyInstancePartyMember>();
        var unconfirmedMembers = new List<LegacyInstancePartyMember>();
        foreach (var member in party.Members.Skip(1))
        {
            // 港湾遇袭 gives every member the leader's own sixty-second window. A
            // member who never confirmed it is not transferred, and the release
            // below returns the daily attempt they never spent.
            if (confirmedFollowerCharacterIds is { } confirmed &&
                !confirmed.Contains(member.CharacterId))
            {
                unconfirmedMembers.Add(member);
                continue;
            }

            if (!_registry.IsLegacyInstancePartyMemberSnapshotCurrent(
                    party,
                    member,
                    npc.X,
                    npc.Z,
                    InstanceCallerProtocol.MaximumInteractionDistance))
            {
                failedMembers.Add(member);
                Console.Error.WriteLine(
                    "[instance-caller] member changed party or source " +
                    $"before transfer character={member.CharacterName}");
                continue;
            }

            var command = BuildLegacyInstanceTransitionCommand(
                member,
                target.InstanceId,
                destination);
            bool moved;
            try
            {
                moved = await _registry
                    .TransitionPartyMemberToAuthoritativeInstanceAsync(
                        member.Session,
                        command,
                        CancellationToken.None);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(
                    "[instance-caller] member transfer fault " +
                    $"character={member.CharacterName}: {error.Message}");
                moved = false;
            }

            if (!moved && _registry.IsSessionInWorldInstance(
                    member.Session,
                    target.InstanceId))
            {
                moved = true;
                Console.Error.WriteLine(
                    "[instance-caller] member entered before transfer " +
                    "publication failed; claim and Opal retained " +
                    $"character={member.CharacterName}");
            }

            if (!moved)
            {
                failedMembers.Add(member);
            }
            else
            {
                _ = await RecordLegacyInstanceAdmissionsAsync(
                    reservationId,
                    new[] { member.CharacterId });
            }
        }

        if (failedMembers.Count != 0)
        {
            if (!opalsCharged)
            {
                await ReleaseLegacyInstanceDailyEntryMembersAsync(
                    reservationId,
                    failedMembers
                        .Select(static member => member.CharacterId)
                        .ToArray());
            }
            await SendLegacyInstanceUnavailableAsync(
                CancellationToken.None);
        }

        // The same release path serves a member who simply never confirmed their
        // own window, without reporting a transfer failure that did not happen.
        if (unconfirmedMembers.Count != 0 && !opalsCharged)
        {
            await ReleaseLegacyInstanceDailyEntryMembersAsync(
                reservationId,
                unconfirmedMembers
                    .Select(static member => member.CharacterId)
                    .ToArray());
            Console.WriteLine(
                "[instance-caller] member windows closed without entry " +
                $"count={unconfirmedMembers.Count} " +
                $"characters={string.Join(',', unconfirmedMembers.Select(static member => member.CharacterName))}");
        }

        if (opalsCharged)
        {
            var releasedCharacterIds = failedMembers
                .Concat(unconfirmedMembers)
                .Select(static member => member.CharacterId)
                .ToHashSet();
            _ = await SettleLegacyInstanceOpalsAsync(
                reservationId,
                party,
                party.Members
                    .Where(member =>
                        !releasedCharacterIds.Contains(member.CharacterId))
                    .Select(static member => member.CharacterId)
                    .ToArray());
        }

        Console.WriteLine(
            "[instance-caller] instance admitted " +
            $"leader={leader.CharacterName} " +
            $"destination={destination.DisplayName} " +
            $"instance={target.InstanceId} party={party.Members.Count} " +
            $"member-transfer-failures={failedMembers.Count} " +
            $"member-windows-closed={unconfirmedMembers.Count}");
    }
}
