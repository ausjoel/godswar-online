using System.Buffers.Binary;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Game;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// B21 (F1, 2026-11-21): main-line quest 546 (Sparta) / 1546 (Athens) asks for one elite, as the client's
/// en_us and zh_cn quest text say, and the elite its kill must carry is a client template of that suburb.
/// </summary>
internal static class B21FollowupChecks
{
    public const string CheckName = "B21 follow-ups: quest 546/1546 one elite kill, nine warehouse boxes";

    public static Task RunAsync()
    {
        CheckQuest(546u, 4, "[Elite]Divine Staff Selayan", "A_elite_StaffSoulsly_004", "Woodland Wolf");
        CheckQuest(1546u, 2, "[Elite]Young Armored Dragon", "A_elite_FireDragontj_000", "Young Jade Dragon");
        CheckQuestElitePacket(2, "Athens_Newbie", "A_elite_FireDragontj_000", "[Elite]Young Armored Dragon", 41546);
        CheckQuestElitePacket(4, "Sparta_Newbie", "A_elite_StaffSoulsly_004", "[Elite]Divine Staff Selayan", 40546);
        CheckWarehousePages();
        return Task.CompletedTask;
    }

    private static void CheckQuest(uint questId, short mapId, string displayName, string templateKey, string otherMonster)
    {
        var objectives = StarterQuestObjectives.For(questId);
        Check.True(objectives.Count == 1 && objectives[0].Required == 1 && objectives[0].MapId == (uint)mapId,
            $"quest {questId}: one objective, one kill, on map {mapId}");
        Check.True(StarterQuestObjectives.Matches(objectives[0], (uint)mapId, displayName, -153f, -211f) &&
                !StarterQuestObjectives.Matches(objectives[0], (uint)mapId, otherMonster, -153f, -211f) &&
                !StarterQuestObjectives.Matches(objectives[0], (uint)(mapId == 2 ? 4 : 2), displayName, -153f, -211f),
            $"quest {questId}: a kill of {displayName} counts; {otherMonster} or the other suburb does not");
        Check.True(StarterQuestObjectives.IsSatisfied(objectives, StarterQuestObjectives.WithCounter(0, 0, 1)),
            $"quest {questId}: one kill satisfies it");
        Check.True(MonsterTemplateSeeds.Monsters.Any(seed =>
                seed.TemplateKey == templateKey && seed.SourceMapId == mapId && seed.IsElite && seed.DisplayName == displayName),
            $"quest {questId}: {templateKey} is an elite client template of map {mapId} (the GM spawn layer admits it)");
    }

    /// <summary>The GM spawn row of sql/B-quest546-elites.sql (packet built exactly as its generator does) passes
    /// every filter world entry applies: packet validation, reserved player ids and the visibility grid.</summary>
    private static void CheckQuestElitePacket(short mapId, string scene, string template, string display, uint objectId)
    {
        var packet = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 108);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), 10020);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 0x212u | ((uint)mapId << 16));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), 27);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20), 35_856);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(24), 35_856);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(28), -153f);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(36), -211f);
        System.Text.Encoding.ASCII.GetBytes(template).CopyTo(packet, 44);
        var spawn = new CapturedMonsterSpawn(mapId, scene, template, display, objectId, -153f, -211f, packet);
        spawn.Validate(mapId);
        Check.True(spawn.Tier == 27 && spawn.FactionCamp == -1 &&
                !WorldObjectIds.IsReservedForPlayer(objectId) &&
                WorldSectorVisibilityTracker<CapturedMonsterSpawn>.TryGetCell(spawn.AppearanceX, spawn.AppearanceZ, out _),
            $"quest elite {template} on map {mapId}: valid packet, Lv27, no faction, not a player id, inside the grid");
    }

    private static void CheckWarehousePages()
    {
        Check.True(WarehousePageCommand.TryParse("/whpage 1", out var first) && first == 1 &&
                WarehousePageCommand.TryParse("/WHPAGE 9", out var last) && last == 9 &&
                WarehousePageCommand.TryParseChatLine("Nativete: /whpage 5", out var prefixed) && prefixed == 5 &&
                !WarehousePageCommand.TryParse("/whpage 0", out _) &&
                !WarehousePageCommand.TryParse("/whpage 10", out _) &&
                !WarehousePageCommand.TryParse("/whpage -1", out _) &&
                !WarehousePageCommand.TryParse("/whpage", out _) &&
                !WarehousePageCommand.TryParse("/whpage 3 4", out _),
            "/whpage takes exactly one box number 1-9");
        Check.True(WarehouseCapacityPolicy.MaximumSupportedBoxCount == 9 &&
                WarehouseCapacityPolicy.IsValidCapacity(360) && !WarehouseCapacityPolicy.IsValidCapacity(400),
            "the server supports nine 40-cell boxes");
        Check.True(WarehousePageCommand.ToLogicalSlot(0, 0) == 0 && WarehousePageCommand.ToLogicalSlot(77, 0) == 77 &&
                WarehousePageCommand.ToLogicalSlot(0, 8) == 320 && WarehousePageCommand.ToLogicalSlot(39, 8) == 359 &&
                WarehousePageCommand.ToLogicalSlot(-1, 4) == -1 && WarehousePageCommand.ToLogicalSlot(45, 4) == 45,
            "physical cells rebase onto the selected box; page 0, automatic and 1.5.3 tab slots are unchanged");
        Check.True(WarehousePageCommand.TryToVisibleSlot(205, 5, out var cell) && cell == 5 &&
                !WarehousePageCommand.TryToVisibleSlot(45, 5, out _) &&
                WarehousePageCommand.TryToVisibleSlot(45, 0, out var stock) && stock == 45 &&
                WarehousePageCommand.TryToVisibleSlot(-1, 5, out var auto) && auto == -1,
            "only slots of the visible box are acknowledged, as physical cells");
        var move = new WarehouseTransferIntent(WarehouseTransferOperation.InternalMove, 3, -1, 7, 0, WarehouseStorageType.Normal);
        var rebased = WarehousePageCommand.ToLogical(move, 2);
        var deposit = WarehousePageCommand.ToLogical(
            new WarehouseTransferIntent(WarehouseTransferOperation.Deposit, -1, 12, -1, 0, WarehouseStorageType.Normal), 6);
        Check.True(rebased.WarehouseSlot == 83 && rebased.DestinationWarehouseSlot == 87 && rebased.KitBagSlot == -1 &&
                deposit.WarehouseSlot == -1 && deposit.KitBagSlot == 12 &&
                WarehousePageCommand.ToLogical(move, 0) == move,
            "a move on box 3 rebases both warehouse slots; the bag slot never moves");
    }
}
