using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Godswar.Server.Game;
using Godswar.Server.Operations.Health;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// W6A (the admin 2026-11-13): login health records, the login check and "one bad row must not block a login".
// The live case: character_items row with prop 10109 (packed Seal Jade) and no sealed_pet_items row made
// EnterGame throw "A packed Seal Jade requires a native-width linked pet ID." on every login.
internal static class SessionHealthChecks
{
    public const string CheckName = "W6A login health records, login check and unsendable-item quarantine";

    public static Task RunAsync()
    {
        // The address is cut to its network before it is kept.
        Check.Equal("203.0.113.0", SessionJourney.TruncateAddress("203.0.113.57:50112"), "IPv4 keeps three octets");
        Check.Equal("203.0.113.0", SessionJourney.TruncateAddress("[::ffff:203.0.113.57]:1"), "IPv4-mapped IPv6 is cut as IPv4");
        Check.Equal("2001:db8:aa::", SessionJourney.TruncateAddress("[2001:db8:aa:bb:1:2:3:4]:7000"), "IPv6 keeps 48 bits");
        Check.True(SessionJourney.TruncateAddress("fixture:1234") is null, "a fixture endpoint has no network");
        Check.True(SessionJourney.TruncateAddress(null) is null, "no endpoint, no network");

        var journey = new SessionJourney("game", 1, "198.51.100.9:4000");
        journey.Mark("game_login");
        journey.Mark("game_login");
        journey.Mark("entered");
        var stages = JsonNode.Parse(journey.StagesJson())!.AsObject();
        Check.Equal(2, stages.Count, "a stage is recorded once");
        Check.True(journey.Reached("entered") && !journey.Reached("ui_ready"), "Reached answers per stage");
        for (var index = 0; index < 40; index++)
        {
            journey.Mark("s" + index);
        }
        Check.Equal(SessionJourney.MaximumStages, JsonNode.Parse(journey.StagesJson())!.AsObject().Count, "stages are bounded");
        journey.SetError(new InvalidDataException(new string('x', 900) + "\n"));
        Check.Equal(SessionJourney.MaximumErrorTextLength, journey.ErrorText!.Length, "error text is clipped");
        journey.Refuse("banned");
        journey.Refuse("other");
        Check.Equal("banned", journey.Reason, "the first refusal reason is kept");

        // The recorder never stores a connection that sent no login packet, and never throws.
        var recorded = new List<SessionJourney>();
        SessionHealthRecorder.UseSinkForTests(recorded.Add);
        try
        {
            SessionHealthRecorder.Record(null);
            SessionHealthRecorder.Record(new SessionJourney("login", 1, "198.51.100.9:1"));
            Check.Equal(0, recorded.Count, "a port scan (no login packet) is not recorded");
            var attempted = new SessionJourney("login", 1, "198.51.100.9:1") { Attempted = true };
            SessionHealthRecorder.Record(attempted);
            Check.Equal(1, recorded.Count, "an attempted login is recorded");
            SessionHealthRecorder.UseSinkForTests(static _ => throw new InvalidOperationException("sink down"));
            SessionHealthRecorder.Record(attempted);
        }
        finally
        {
            SessionHealthRecorder.UseSinkForTests(null);
        }
        Check.Equal(30, SessionHealthRecorder.ReadRetentionDays(null), "retention defaults to 30 days");
        Check.Equal(7, SessionHealthRecorder.ReadRetentionDays("7"), "retention can be set");
        Check.Equal(30, SessionHealthRecorder.ReadRetentionDays("0"), "retention ignores nonsense");

        // The packed Seal Jade without a pet: detected, hidden from the client, kept in the bag.
        var orphan = CompactItemEntry.Empty with { Id = PetItemCatalog.PackedSealJade, Quality = 1, Grade = 1, Stack = 1 };
        var linked = orphan with { LinkedSealedPetId = 77 };
        var potion = CompactItemEntry.Empty with { Id = 1035, Quality = 1, Grade = 1, Stack = 1 };
        Check.True(PacketBuilder.ItemFault(orphan) is { } why && why.Contains("Seal Jade", StringComparison.Ordinal),
            "a packed Seal Jade with no pet is a fault");
        Check.True(PacketBuilder.ItemFault(linked) is null, "a packed Seal Jade with its pet is fine");
        Check.True(PacketBuilder.ItemFault(potion) is null && PacketBuilder.ItemFault(default) is null,
            "ordinary items and empty slots are fine");

        var bag = KitBagSlots.SetSlot(GameDefaults.EmptyKitBag, 0, potion.ToCompactString());
        bag = KitBagSlots.SetSlot(bag, 17, orphan.ToCompactString());
        bag = KitBagSlots.SetSlot(bag, 18, linked.ToCompactString());
        var character = new GameCharacter { KitBag = bag };
        var before = character.KitBag;
        var pages = PacketBuilder.KitBagDetailPages(character); // threw before W6A
        var indexes = PacketBuilder.KitBagSlotIndexes(character);
        Check.Equal(before, character.KitBag, "building the entry packets does not change the bag (slot stays taken)");
        // 12 records per packet from offset 24, 72 bytes each: slot 17 is record 5 of packet 1, slot 18 is record 6.
        Check.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(pages[1].AsSpan(24 + (5 * 72), 4)),
            "the pet-less Seal Jade is sent as an empty slot");
        Check.Equal(PetItemCatalog.PackedSealJade, BinaryPrimitives.ReadUInt32LittleEndian(pages[1].AsSpan(24 + (6 * 72), 4)),
            "the linked Seal Jade is sent");
        Check.Equal(77u, BinaryPrimitives.ReadUInt32LittleEndian(pages[1].AsSpan(24 + (6 * 72) + 56, 4)),
            "the linked Seal Jade carries its pet id");
        Check.Equal(1035u, BinaryPrimitives.ReadUInt32LittleEndian(pages[0].AsSpan(24, 4)), "other items are untouched");
        Check.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(indexes[17].AsSpan(20, 4)), "its slot index packet says empty too");
        Check.Equal(unchecked((int)PetItemCatalog.PackedSealJade), BinaryPrimitives.ReadInt32LittleEndian(indexes[18].AsSpan(20, 4)),
            "the linked jade's slot index is sent");

        var unsendable = PacketBuilder.UnsendableItems(character);
        Check.Equal(1, unsendable.Count, "exactly the bad item is reported");
        Check.Equal("bag", unsendable[0].Container, "reported container");
        Check.Equal(17, unsendable[0].Slot, "reported slot");
        Check.Equal(PetItemCatalog.PackedSealJade, unsendable[0].ItemId, "reported item");
        Check.Equal(0, PacketBuilder.UnsendableItems(new GameCharacter { KitBag = GameDefaults.EmptyKitBag }).Count,
            "a clean bag reports nothing");
        ItemGrantGuardChecks.Run();
        return Task.CompletedTask;
    }
}

internal static class ItemGrantGuardChecks
{
    public static void Run()
    {
        Check.True(PetItemCatalog.CannotBeGrantedReason(PetItemCatalog.PackedSealJade) is not null,
            "a packed Seal Jade cannot be granted by id");
        Check.True(PetItemCatalog.CannotBeGrantedReason(PetItemCatalog.EmptySealJade) is null,
            "an empty Seal Jade can be granted");
        Check.True(PetItemCatalog.CannotBeGrantedReason(1035) is null, "ordinary items can be granted");
    }
}
