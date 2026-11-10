using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// The stock client ships two normal-storage tellers per capital. The primary
/// tellers were routed first; these checks pin the secondary tellers so a
/// later interaction-id edit cannot silently turn a routed teller back into a
/// dead NPC. An unrouted teller opens nothing at all: the client dialog-open
/// handler returns without a packet when no route resolves.
/// </summary>
internal static partial class WarehouseHandlerChecks
{
    public const string SecondaryTellerCheckName =
        "Secondary capital warehouse teller handlers";

    private const uint SpartaSecondaryTellerId = 5097;
    private const uint AthensSecondaryTellerId = 5238;

    public static async Task RunSecondaryTellerAsync()
    {
        await CheckSecondaryTellerOpensAsync("Sparta_100", SpartaSecondaryTellerId);
        await CheckSecondaryTellerOpensAsync("Athens_100", AthensSecondaryTellerId);
    }

    private static async Task CheckSecondaryTellerOpensAsync(
        string npcKey,
        uint interactionId)
    {
        var character = CharacterSnapshot(
            GameDefaults.EmptyKitBag,
            BeforeInventoryRevision,
            $"secondary-teller-{npcKey}");
        var hydrated = CharacterLoadSnapshotHydrator.Hydrate(character) ??
            throw new InvalidOperationException(
                "Secondary teller fixture did not hydrate.");
        var npc = CreateSecondaryTellerNpc(hydrated.Character, npcKey, interactionId);
        await using var fixture = await CreateFixtureAsync(
            character,
            [character],
            [
                WarehouseSnapshot(BeforeInventoryRevision, containsKey: false),
                WarehouseSnapshot(BeforeInventoryRevision, containsKey: false)
            ],
            new WarehouseTransferExecutor(),
            additionalNpcs: [npc]);

        var before = fixture.ReadPackets().Count;
        await InvokeAsync(fixture.Handler, CreateNpcClick(interactionId));

        var packets = fixture.ReadPackets().Skip(before).ToArray();
        Check.True(
            packets.Length == 1 &&
            packets[0].SequenceEqual(
                PacketBuilder.WarehouseDialogOpenAck(interactionId, npcKey)),
            $"{npcKey} opens normal storage with its own client script key");

        await InvokeAsync(fixture.Handler, CreateWarehousePageRequest(interactionId));
        var opened = fixture.ReadPackets().Skip(before + 1).ToArray();
        Check.True(
            opened.Length == 4 &&
            opened.All(packet =>
                ReadOpcode(packet) == Opcodes.WarehouseSnapshot) &&
            fixture.Warehouses.ReadCount == 1,
            $"{npcKey} serves the authoritative warehouse page snapshot");
    }

    private static NpcSpawnDefinition CreateSecondaryTellerNpc(
        GameCharacter character,
        string npcKey,
        uint interactionId) => new(
        character.CurrentMap,
        "Athens",
        npcKey,
        $"{npcKey}_Female1",
        interactionId,
        character.PositionX + 1f,
        character.PositionZ,
        interactionId,
        AppearanceType: 1,
        Facing: 1.7f,
        Detail10077: [],
        Detail10080: []);
}
