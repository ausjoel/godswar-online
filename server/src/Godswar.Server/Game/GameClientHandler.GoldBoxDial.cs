using System.Buffers.Binary;
using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

// The box dial. Right-clicking a box with its key sends 10118 type 0; the client has already locked
// both. The open is the keyed gift pack open: box and key used up and the reward granted in one
// transaction. The dial then shows that reward, and the bag refresh that reveals it is held until the
// player claims, closes the window, opens another box, or ten minutes pass. Nothing is granted twice:
// a disconnect leaves the committed bag, which the next login loads.
internal sealed partial class GameClientHandler
{
    private const byte GoldBoxOpen = 0;
    private const byte GoldBoxStart = 1;
    private const byte GoldBoxClose = 2;
    private const byte GoldBoxExtraDraw = 6;
    private const byte GoldBoxClaim = 7;
    private static readonly TimeSpan GoldBoxDialHoldLimit = TimeSpan.FromMinutes(10);

    private sealed class GoldBoxDialSession
    {
        public required int CharacterId { get; init; }
        public required int BoxSlot { get; init; }
        public required DateTimeOffset OpenedAt { get; init; }
        public bool Capturing { get; set; }
        public HeldGiftPackProjection? Held { get; set; }
        public int Target { get; set; } = -1;
        public int RewardSlot { get; set; } = -1;
        public bool Spinning { get; set; }
    }

    private sealed record HeldGiftPackProjection(PetDurableReceipt Receipt,
        PetDurableExecutionDisposition Disposition, string BagBefore);

    private GoldBoxDialSession? _goldBoxDial;

    // Called by the gift pack projection: while a dial open is running, a successful open is kept back.
    private bool TryHoldGiftPackProjection(PetDurableReceipt receipt, PetDurableExecutionDisposition disposition,
        string bagBefore)
    {
        if (_goldBoxDial is not { Capturing: true } dial || receipt.Status != PetDurableReceiptStatus.GiftPackOpened ||
            disposition != PetDurableExecutionDisposition.Committed || receipt.GiftPack is null ||
            receipt.KitBagSlot != dial.BoxSlot)
            return false;
        dial.Held = new(receipt, disposition, bagBefore);
        return true;
    }

    private async Task HandleGoldBoxAsync(GamePacket packet, CancellationToken token)
    {
        var payload = packet.Payload;
        if (payload.Length < 9 || _account is null || _character is null)
        {
            Console.WriteLine($"[goldbox] ignored len={packet.Length}");
            return;
        }
        var type = payload[0];
        var boxId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4));
        var slot = (int)payload[8];
        Console.WriteLine($"[goldbox] type={type} box={boxId} slot={slot} character={_character.Id}");

        if (type == GoldBoxOpen)
        {
            await OpenGoldBoxAsync(boxId, slot, token);
            return;
        }

        // Types 1, 2 and 7 follow an open. Only the server's own record of it is used.
        var dial = _goldBoxDial is { } current && current.CharacterId == _character.Id ? current : null;
        switch (type)
        {
            case GoldBoxStart when dial is { Held: not null, Spinning: false }:
                dial.Spinning = true;
                await _session.SendAsync(PacketBuilder.GoldBoxReturn(PacketBuilder.GoldBoxReturnSpin,
                    dial.Target, dial.RewardSlot), token, "GoldBoxSpin");
                return;
            case GoldBoxClaim when dial is { Spinning: true }:
                // The client copies the ring item into the reward slot; the refresh after it is the truth.
                dial.Spinning = false;
                await _session.SendAsync(PacketBuilder.GoldBoxReturn(PacketBuilder.GoldBoxReturnClaimed,
                    dial.Target, dial.RewardSlot), token, "GoldBoxClaimed");
                if (dial.Held is null) await SendKitBagRefreshAsync(token);
                else await ReleaseGoldBoxDialAsync(token);
                _goldBoxDial = null;
                return;
            case GoldBoxClose when dial is not null:
                // Closed before Start: the box is already open, so the reward arrives without the spin.
                await ReleaseGoldBoxDialAsync(token);
                _goldBoxDial = null;
                return;
            default:
                // Extra draw is not offered (InDial sends no extra item). Anything else out of order
                // gets the bag, which also unlocks whatever the client locked.
                Console.WriteLine($"[goldbox] refused type={type}{(type == GoldBoxExtraDraw ? " (extra draw)" : "")}");
                await ReleaseGoldBoxDialAsync(token);
                await SendKitBagRefreshAsync(token);
                return;
        }
    }

    private async Task OpenGoldBoxAsync(uint boxId, int slot, CancellationToken token)
    {
        await ReleaseGoldBoxDialAsync(token);
        _goldBoxDial = null;
        var character = _character!;
        var templates = RequireItemContent().Templates;
        var bagBefore = character.KitBag;
        var box = slot is >= 0 and < 96 ? KitBagSlots.GetItem(bagBefore, slot) : CompactItemEntry.Empty;
        string? refusal =
            !GoldBoxDialPlan.IsEnabledFor(_account!.Id) ? "dial not enabled for this account" :
            _session.IsSecure ? "secure session" :
            box.IsEmpty || box.Id != boxId ? "slot does not hold that box" :
            !GiftPackPolicy.TryResolve(templates, boxId, out var pack) || pack.SkillId != 0 || pack.KeyItemId == 0
                ? "no box table" :
            !Enumerable.Range(0, 96).Any(i => KitBagSlots.GetItemId(bagBefore, i) == pack.KeyItemId) ? "no key" :
            !IsHostileStatusItemUseAllowed(DateTimeOffset.UtcNow) ? "item use blocked" :
            null;
        if (refusal is not null)
        {
            Console.WriteLine($"[goldbox] open refused box={boxId} slot={slot}: {refusal}");
            await SendKitBagRefreshAsync(token);
            return;
        }
        if (!AllowLegacyPlayerMutationFallback("gold_box_open")) return;

        var dial = new GoldBoxDialSession
        {
            CharacterId = character.Id, BoxSlot = slot, OpenedAt = DateTimeOffset.UtcNow, Capturing = true
        };
        _goldBoxDial = dial;
        try
        {
            // Not held (refused, bag full, cooldown): the ordinary projection has already answered.
            await HandleDurableBagItemActivationAsync(
                PetCommandOperationIdentity.RawLocalServer(Guid.NewGuid(), _commandConnectionId), slot, token);
        }
        finally
        {
            dial.Capturing = false;
        }
        if (dial.Held is not { } held)
        {
            _goldBoxDial = null;
            Console.WriteLine($"[goldbox] open box={boxId} slot={slot}: not opened");
            return;
        }

        GiftPackPolicy.TryResolve(templates, boxId, out var definition);
        var rewards = held.Receipt.GiftPack!.Rewards;
        var (items, target) = GoldBoxDialPlan.Build(definition, rewards, box, character.Profession,
            character.Gender == 1, id => templates.TryGet(id, out _), Random.Shared);
        dial.Target = target;
        dial.RewardSlot = GoldBoxDialPlan.RewardSlot(held.BagBefore, slot, box);
        Console.WriteLine($"[goldbox] opened box={boxId} slot={slot} rewards={held.Receipt.GiftPack.Contents} " +
            $"target={target} rewardSlot={dial.RewardSlot}");
        await _session.SendAsync(PacketBuilder.GoldBoxInDial(dial.RewardSlot, items), token, "GoldBoxInDial");
    }

    // Sends a held reward, if any. The dial session itself is left to the caller.
    private async Task ReleaseGoldBoxDialAsync(CancellationToken token)
    {
        if (_goldBoxDial is not { Held: { } held } dial) return;
        dial.Held = null;
        if (_character is null || _character.Id != dial.CharacterId) return;
        await SendGiftPackProjectionAsync(held.Receipt, held.Disposition, held.BagBefore, token);
    }

    // On the first packet after the hold limit. Other packets do not release it: the client reports its bag
    // (10114, 10056) as soon as the placeholder appears, and any other bag change sends the whole bag anyway.
    private async Task SettleGoldBoxDialAsync(ushort opcode, CancellationToken token)
    {
        if (_goldBoxDial is not { } dial || opcode == PacketBuilder.GoldBoxRequestOpcode ||
            DateTimeOffset.UtcNow - dial.OpenedAt < GoldBoxDialHoldLimit)
            return;
        if (dial.Held is not null)
        {
            Console.WriteLine($"[goldbox] released after the hold limit by opcode={opcode}");
            await ReleaseGoldBoxDialAsync(token);
        }
        // A spin still running keeps its session so a late claim still gets its reply and a refresh.
        if (!dial.Spinning) _goldBoxDial = null;
    }
}
