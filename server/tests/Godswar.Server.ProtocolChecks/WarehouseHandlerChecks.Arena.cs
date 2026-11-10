using System.Buffers.Binary;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WarehouseHandlerChecks
{
    public const string ArenaSupportCheckName =
        "Duel Arena Vendor and Akou warehouse handlers";

    public static async Task RunArenaSupportAsync()
    {
        await CheckArenaVendorDescriptionAsync();
        await CheckArenaWarehouseDepositAsync();
        await CheckArenaWarehouseOpenReadGuardsAsync();
        await CheckArenaWarehouseTransferGuardsAsync();
    }

    private static async Task CheckArenaVendorDescriptionAsync()
    {
        var initial = ArenaCharacterSnapshot(
            GameDefaults.EmptyKitBag, BeforeInventoryRevision,
            "arena-vendor", DuelArenaCapturedLayout.VendorSpawnX,
            DuelArenaCapturedLayout.VendorSpawnZ);
        await using var fixture = await CreateArenaFixtureAsync(
            initial, [], [], new WarehouseTransferExecutor());
        var npcId = DuelArenaCapturedLayout.VendorNpcId;

        await InvokeAsync(fixture.Handler, ArenaShortClick(npcId));
        Check.Equal(0, fixture.ReadPackets().Count,
            "Arena Vendor rejects a short NPC click");
        await InvokeAsync(fixture.Handler, CreateNpcClick(npcId));
        var opened = fixture.ReadPackets();
        Check.True(opened.Count == 1 && opened[0].SequenceEqual(
                PacketBuilder.NpcDescriptionDialogOpenAck(npcId, "Arena_001")),
            "captured Vendor advertises flags zero, no function and Arena_001 text");
        await InvokeAsync(fixture.Handler, ArenaPageRequest(npcId));
        Check.True(fixture.ReadPackets().Count == 1 &&
                fixture.Warehouses.ReadCount == 0 &&
                fixture.Executor.ExecuteCount == 0,
            "Vendor page request does not invent a shop or warehouse");

        fixture.Character.PositionX += 13f;
        await InvokeAsync(fixture.Handler, CreateNpcClick(npcId));
        fixture.Character.PositionX -= 13f;
        fixture.Character.CurrentHp = 0;
        await InvokeAsync(fixture.Handler, CreateNpcClick(npcId));
        fixture.Character.CurrentHp = 1;
        fixture.Character.CurrentMap = 1;
        await InvokeAsync(fixture.Handler, CreateNpcClick(npcId));
        fixture.Character.CurrentMap = 57;
        await InvokeAsync(fixture.Handler, CreateNpcClick(5_196));
        Check.Equal(1, fixture.ReadPackets().Count,
            "Vendor rejects distance, death, wrong map and unknown identity");
    }

    private static async Task CheckArenaWarehouseDepositAsync()
    {
        var beforeBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag, KitBagSlot, StorageKey.ToCompactString());
        var beforeCharacter = ArenaCharacterSnapshot(
            beforeBag, BeforeInventoryRevision, "arena-warehouse-before",
            DuelArenaCapturedLayout.DuelArenaSpawnX,
            DuelArenaCapturedLayout.DuelArenaSpawnZ);
        var afterCharacter = ArenaCharacterSnapshot(
            GameDefaults.EmptyKitBag, AfterInventoryRevision,
            "arena-warehouse-after", DuelArenaCapturedLayout.DuelArenaSpawnX,
            DuelArenaCapturedLayout.DuelArenaSpawnZ);
        var beforeWarehouse = WarehouseSnapshot(
            BeforeInventoryRevision, containsKey: false);
        var afterWarehouse = WarehouseSnapshot(
            AfterInventoryRevision, containsKey: true);
        var executor = new WarehouseTransferExecutor
        {
            ExecuteResult = WarehouseTransferExecutionResult.Terminal(
                WarehouseTransferExecutionDisposition.Committed,
                DepositReceipt())
        };
        await using var fixture = await CreateArenaFixtureAsync(
            beforeCharacter, [beforeCharacter, afterCharacter],
            [beforeWarehouse, beforeWarehouse, afterWarehouse], executor);
        var npcId = WarehouseNpcProtocol.DuelArenaWarehouseNpcId;

        Check.True(WarehouseNpcProtocol.IsWarehouseEndpoint(
                "DuelArena_001", npcId) &&
            !WarehouseNpcProtocol.IsWarehouseEndpoint("DuelArena_001", 5_201) &&
            !WarehouseNpcProtocol.IsWarehouseEndpoint("Arena_001", npcId) &&
            WarehouseNpcProtocol.ClientScriptKey("Athens_025",
                WarehouseNpcProtocol.AthensWarehouseNpcId) == "Athens_025" &&
            WarehouseNpcProtocol.ClientScriptKey("Sparta_023",
                WarehouseNpcProtocol.SpartaWarehouseNpcId) == "Sparta_023",
            "Akou endpoint and ACK alias do not change capital identities");

        await InvokeAsync(fixture.Handler, ArenaShortClick(npcId));
        fixture.Character.PositionX += 13f;
        await InvokeAsync(fixture.Handler, CreateNpcClick(npcId));
        await InvokeAsync(fixture.Handler, ArenaPageRequest(npcId));
        fixture.Character.PositionX -= 13f;
        fixture.Character.CurrentHp = 0;
        await InvokeAsync(fixture.Handler, ArenaPageRequest(npcId));
        fixture.Character.CurrentHp = 1;
        Check.True(fixture.ReadPackets().Count == 0 &&
                fixture.Warehouses.ReadCount == 0,
            "Akou rejects malformed, distant and dead opens before reading storage");

        await InvokeAsync(fixture.Handler, CreateNpcClick(npcId));
        Check.True(fixture.ReadPackets() is [var ack] &&
                ack.SequenceEqual(PacketBuilder.WarehouseDialogOpenAck(
                    npcId, "Sparta_023")) &&
                fixture.Warehouses.ReadCount == 0 &&
                GetHandlerField<WarehouseAccessContext>(fixture.Handler,
                    "_warehouseAccessContext") is null,
            "Akou sends the captured flags-32 Sparta_023 ACK before reading state");

        await InvokeAsync(fixture.Handler, ArenaPageRequest(npcId));
        var context = GetHandlerField<WarehouseAccessContext>(
            fixture.Handler, "_warehouseAccessContext");
        Check.True(fixture.Warehouses.ReadCount == 1 &&
                context is { NpcInteractionId: 5_202, MapId: 57 } &&
                fixture.ReadPackets().Skip(1).Count(packet =>
                    ReadOpcode(packet) == Opcodes.WarehouseSnapshot) == 4,
            "Akou page request uses the normal warehouse snapshot and map-bound lease");

        var beforeTransfer = fixture.ReadPackets().Count;
        await InvokeAsync(fixture.Handler, CreateDepositRequest());
        var emitted = fixture.ReadPackets().Skip(beforeTransfer).ToArray();
        Check.True(executor.ReplayCount == 1 && executor.ExecuteCount == 1 &&
                executor.Envelope?.Command is { } command &&
                command.ExpectedInventoryRevision == BeforeInventoryRevision &&
                command.ExpectedWarehouseRevision == 0 &&
                command.ExpectedSourceCompactItemState == StorageKey.ToCompactString() &&
                fixture.Character.KitBag == GameDefaults.EmptyKitBag &&
                fixture.Character.CurrentMap == 57 &&
                fixture.Warehouses.ReadCount == 3 && fixture.Characters.ReadCount == 2,
            "Akou reuses the replay-first durable warehouse transfer and authoritative projections");
        Check.True(emitted.Count(packet =>
                    ReadOpcode(packet) == Opcodes.WarehouseSnapshot) == 4 &&
                emitted.All(packet =>
                    ReadOpcode(packet) != Opcodes.WarehouseTransfer),
            "Akou transfer converges through normal snapshots without a non-idempotent ACK");
    }

    private static CharacterAccountSnapshot ArenaCharacterSnapshot(
        string kitBag, long inventoryRevision, string token, float x, float z)
    {
        var snapshot = CharacterSnapshot(kitBag, inventoryRevision, token);
        return snapshot with
        {
            Character = snapshot.Character! with
            {
                Location = snapshot.Character.Location with
                {
                    CurrentMap = 57, PositionX = x, PositionZ = z
                }
            }
        };
    }

    private static async Task CheckArenaWarehouseOpenReadGuardsAsync()
    {
        foreach (var death in new[] { false, true })
        {
            var initial = ArenaCharacterSnapshot(GameDefaults.EmptyKitBag,
                BeforeInventoryRevision, "arena-warehouse-open-guard",
                DuelArenaCapturedLayout.DuelArenaSpawnX,
                DuelArenaCapturedLayout.DuelArenaSpawnZ);
            var stored = WarehouseSnapshot(BeforeInventoryRevision, containsKey: false);
            GameCharacter? character = null;
            await using var fixture = await CreateArenaFixtureAsync(
                initial, [], [stored], new WarehouseTransferExecutor(),
                afterWarehouseRead: _ =>
                {
                    if (death) { character!.CurrentHp = 0; }
                    else { character!.PositionX += 13f; }
                });
            character = fixture.Character;
            await InvokeAsync(fixture.Handler,
                CreateNpcClick(WarehouseNpcProtocol.DuelArenaWarehouseNpcId));
            await InvokeAsync(fixture.Handler,
                ArenaPageRequest(WarehouseNpcProtocol.DuelArenaWarehouseNpcId));
            Check.True(fixture.ReadPackets().Count == 1 &&
                    fixture.Warehouses.ReadCount == 1 &&
                    GetHandlerField<WarehouseAccessContext>(fixture.Handler,
                        "_warehouseAccessContext") is null,
                $"Akou rejects {(death ? "death" : "distance")} during the open snapshot " +
                "without opening storage or issuing access");
        }
    }

    private static async Task CheckArenaWarehouseTransferGuardsAsync()
    {
        foreach (var afterRead in new[] { false, true })
        foreach (var death in new[] { false, true })
        {
            var bag = KitBagSlots.SetSlot(GameDefaults.EmptyKitBag,
                KitBagSlot, StorageKey.ToCompactString());
            var initial = ArenaCharacterSnapshot(bag, BeforeInventoryRevision,
                "arena-warehouse-guard", DuelArenaCapturedLayout.DuelArenaSpawnX,
                DuelArenaCapturedLayout.DuelArenaSpawnZ);
            var stored = WarehouseSnapshot(BeforeInventoryRevision, containsKey: false);
            var executor = new WarehouseTransferExecutor();
            GameCharacter? character = null;
            void InvalidateAccess()
            {
                if (death) { character!.CurrentHp = 0; }
                else { character!.PositionX += 13f; }
            }
            await using var fixture = await CreateArenaFixtureAsync(
                initial, [initial, initial], [stored, stored, stored], executor,
                afterWarehouseRead: count =>
                {
                    if (afterRead && count == 2) { InvalidateAccess(); }
                });
            character = fixture.Character;
            await InvokeAsync(fixture.Handler,
                CreateNpcClick(WarehouseNpcProtocol.DuelArenaWarehouseNpcId));
            await InvokeAsync(fixture.Handler,
                ArenaPageRequest(WarehouseNpcProtocol.DuelArenaWarehouseNpcId));
            if (!afterRead) { InvalidateAccess(); }
            await InvokeAsync(fixture.Handler, CreateDepositRequest());
            Check.True(executor.ExecuteCount == 0 &&
                    executor.ReplayCount == (afterRead ? 1 : 0) &&
                    fixture.Character.KitBag == bag &&
                    (afterRead || fixture.Warehouses.ReadCount == 1),
                $"Akou rejects {(death ? "death" : "distance")} " +
                $"{(afterRead ? "after the async snapshot" : "before durable providers")} without mutation");
        }
    }

    private static GamePacket ArenaShortClick(uint npcId)
    {
        var bytes = CreateNpcClick(npcId).Buffer[..47];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 47);
        return new GamePacket(bytes);
    }

    private static GamePacket ArenaPageRequest(uint npcId)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2),
            Opcodes.NpcDialogPageRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), npcId);
        return new GamePacket(bytes);
    }

    private static async Task<WarehouseFixture> CreateArenaFixtureAsync(
        CharacterAccountSnapshot initial,
        IEnumerable<CharacterAccountSnapshot> characterReads,
        IEnumerable<WarehouseSnapshot> warehouseReads,
        WarehouseTransferExecutor executor,
        Action<int>? afterWarehouseRead = null)
    {
        var character = CharacterLoadSnapshotHydrator.Hydrate(initial)?.Character ??
            throw new InvalidOperationException("Arena warehouse fixture did not hydrate.");
        var npcs = NpcContentBaselineV7.LoadDefinitions()
            .Where(static npc => npc.MapId == 57).ToArray();
        var content = PinnedWorldContentReader.Create(
            "arena-support-handler-v1", [57], npcs, [], []);
        var transport = new FactionCrierCaptureTransport();
        var session = new ClientSession(transport);
        var registry = GameHandlerOwnershipTestFences.CreateRegistry(
            session, initial.AccountId, character);
        var characters = new WarehouseCharacterSnapshotReader(characterReads);
        var warehouses = new WarehouseSnapshotReader(warehouseReads);
        IWarehouseSnapshotReader reads = afterWarehouseRead is null
            ? warehouses
            : new ArenaWarehouseReadHook(warehouses, afterWarehouseRead);
        var handler = new GameClientHandler(session, new WarehouseGameStore(),
            registry, characters, content, warehouseSnapshots: reads,
            warehouseTransferCommands: executor);
        SetHandlerField(handler, "_account",
            new AccountIdentity(AccountId, "arena-support-handler-check"));
        SetHandlerField(handler, "_character", character);
        var catalog = await registry.PublishMapNpcDefinitionsAsync(
            character.CurrentMap, npcs, originSession: null, CancellationToken.None);
        InstallNpcCatalogMethod.Invoke(handler, [catalog]);
        var visibility = GetHandlerField<WorldSectorVisibilityTracker<NpcSpawnDefinition>>(
            handler, "_npcVisibility") ??
            throw new InvalidOperationException("Arena NPC visibility was not installed.");
        Check.True(visibility.TryCalculate(character.PositionX,
            character.PositionZ, out var delta), "Arena NPC visibility calculates");
        visibility.Commit(delta);
        return new WarehouseFixture(session, transport, handler, executor,
            characters, warehouses, registry, character);
    }

    private sealed class ArenaWarehouseReadHook(
        WarehouseSnapshotReader inner,
        Action<int> afterRead) : IWarehouseSnapshotReader
    {
        public async Task<WarehouseSnapshot?> ReadAsync(
            CommandSubject subject,
            PlayerOwnershipFence ownership,
            CancellationToken cancellationToken = default)
        {
            var snapshot = await inner.ReadAsync(subject, ownership, cancellationToken);
            afterRead(inner.ReadCount);
            return snapshot;
        }
    }
}
