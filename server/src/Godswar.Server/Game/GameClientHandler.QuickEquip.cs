using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

// The client's own gear swap window (QuickEquipUI, key W; Origin.exe 0x5e01e0,
// 0x5dfe00, 0x5dfa80). The client decides every move itself and sends them as
// ordinary 10052 transfers; the server only has to answer three steps of a 10056
// handshake, and to give each item the 64-bit identity the client matches a saved
// set against. Step and identity layouts were read from the exe's handler at
// 0x4dff03: step at frame +8, bag page +12 and index +16, worn slot +24, identity +32.
internal sealed partial class GameClientHandler
{
    private const int QuickEquipFrameBytes = 40;
    private const uint QuickEquipBagIdentityStep = 1;

    private async Task<bool> TryHandleQuickEquipStepAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (packet.Buffer.Length != QuickEquipFrameBytes)
        {
            return false;
        }

        var step = BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.Slice(4, sizeof(uint)));
        if (step is not (2 or 3 or 4))
        {
            return false;
        }

        // Everything but the step is stale client stack, so none of it is read.
        await _session.SendAsync(
            PacketBuilder.QuickEquipStep(step),
            cancellationToken,
            "QuickEquipStep");
        return true;
    }

    private async Task<bool> TryAnswerQuickEquipIdentityAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null || packet.Buffer.Length != QuickEquipFrameBytes)
        {
            return false;
        }

        var payload = packet.Payload;
        var page = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8, sizeof(uint)));
        var index = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(12, sizeof(uint)));
        var itemId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(16, sizeof(uint)));
        if (BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, sizeof(uint))) != QuickEquipBagIdentityStep ||
            page >= 4 ||
            index >= 24)
        {
            return false;
        }

        var item = KitBagSlots.GetItem(_character.KitBag, (int)(page * 24 + index));
        if (item.IsEmpty || item.Id != itemId)
        {
            return false;
        }

        await _session.SendAsync(
            PacketBuilder.QuickEquipBagIdentity(packet.Buffer, item),
            cancellationToken,
            "QuickEquipBagIdentity");
        return true;
    }

    // The client only asks about bag items; worn ones have to be told. A failed
    // transfer also lands here, which is harmless: the identities do not change.
    private async Task SendQuickEquipWornIdentitiesAsync(CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        foreach (var frame in PacketBuilder.QuickEquipWornIdentities(_character))
        {
            await _session.SendAsync(frame, cancellationToken, "QuickEquipWornIdentity");
        }
    }
}
