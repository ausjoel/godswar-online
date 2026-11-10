using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal bool TryProjectLegacyInstanceOpalKitBag(
        LegacyInstancePartyMember member,
        string authoritativeKitBag,
        out byte[][] evictionPackets,
        out byte[][] detailPackets,
        out byte[][] indexPackets)
    {
        ArgumentNullException.ThrowIfNull(member);
        lock (_gate)
        {
            evictionPackets = [];
            detailPackets = [];
            indexPackets = [];
            if (!_sessions.TryGetValue(member.Session, out var context) ||
                context.AccountId != member.AccountId ||
                context.CharacterId != member.CharacterId ||
                context.Ownership != member.Ownership ||
                !IsCurrentAccountSession(
                    context.AccountId,
                    context.Session,
                    context.Ownership))
            {
                return false;
            }

            var previousKitBag = context.Character.KitBag;
            context.Character.KitBag = authoritativeKitBag;
            evictionPackets = PacketBuilder
                .KitBagMutationDeletionAcknowledgements(
                    previousKitBag,
                    authoritativeKitBag);
            detailPackets = PacketBuilder.KitBagDetailPages(
                context.Character);
            indexPackets = PacketBuilder.KitBagSlotIndexes(
                context.Character);
            return true;
        }
    }

    internal bool TryProjectLegacyInstanceOpalMutation(
        LegacyInstancePartyMember member,
        LegacyInstanceOpalInventoryMutation mutation,
        out byte[][] evictionPackets,
        out byte[][] detailPackets,
        out byte[][] indexPackets)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(mutation);
        lock (_gate)
        {
            evictionPackets = [];
            detailPackets = [];
            indexPackets = [];
            if (mutation.AccountId != member.AccountId ||
                mutation.CharacterId != member.CharacterId ||
                !_sessions.TryGetValue(member.Session, out var context) ||
                context.AccountId != member.AccountId ||
                context.CharacterId != member.CharacterId ||
                context.Ownership != member.Ownership ||
                !IsCurrentAccountSession(
                    context.AccountId,
                    context.Session,
                    context.Ownership))
            {
                return false;
            }

            var currentCompact = KitBagSlots.GetEntry(
                context.Character.KitBag,
                mutation.KitBagSlot);
            if (string.Equals(
                    currentCompact,
                    mutation.AfterCompactItemState,
                    StringComparison.Ordinal))
            {
                return true;
            }
            if (!string.Equals(
                    currentCompact,
                    mutation.BeforeCompactItemState,
                    StringComparison.Ordinal))
            {
                return false;
            }

            var previousKitBag = context.Character.KitBag;
            context.Character.KitBag = KitBagSlots.SetSlot(
                previousKitBag,
                mutation.KitBagSlot,
                mutation.AfterCompactItemState);
            evictionPackets = PacketBuilder
                .KitBagMutationDeletionAcknowledgements(
                    previousKitBag,
                    context.Character.KitBag);
            detailPackets = PacketBuilder.KitBagDetailPages(
                context.Character);
            indexPackets = PacketBuilder.KitBagSlotIndexes(
                context.Character);
            return true;
        }
    }
}
