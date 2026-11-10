using System.Buffers.Binary;
using Godswar.Server.Application.World.Content;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Locks the capture-proven drop channel: the database-owned tables and their
/// migration seed, the deterministic roll, the ground-item identity written
/// into opcode 10029, and the 10056 pickup descriptor the installed client
/// actually sends.
/// </summary>
internal static class MonsterLootChannelChecks
{
    public const string CheckName = "Monster drop channel";

    public static Task RunAsync()
    {
        CheckMigrationOwnsTheTables();
        CheckCaptureProvenTables();
        CheckRollIsDeterministicAndCapped();
        CheckGroundKeys();
        CheckCapturedPickupDescriptor();
        CheckCapturedCorpseClickReply();
        CheckBindOnPickupRule();
        CheckAreaKeyedTables();
        return Task.CompletedTask;
    }

    /// <summary>
    /// A loot rule decides whether its drop binds: <c>NULL</c> follows the item
    /// template, and the roll carries the rule's choice to the pickup so the item
    /// is written with the flag the GM chose.
    /// </summary>
    private static void CheckBindOnPickupRule()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static migration => migration.Id ==
                "20260927_213_monster_loot_bind_on_pickup");
        Check.True(
            migration.Sql.Contains(
                "ADD COLUMN IF NOT EXISTS bound_on_pickup boolean NULL",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "NULL follows the item template BindType",
                StringComparison.Ordinal),
            "the bind flag is a nullable per-rule column");

        // A rule that says nothing keeps the template's own behaviour, so the
        // default record carries no flag at all.
        var untouched = new MonsterLootRule(
            "A_normal_stub_001",
            0,
            4529u,
            2_500,
            1,
            1);
        Check.True(
            untouched.BoundOnPickup is null,
            "a rule without a choice follows the item template");

        var forcedTradeable = untouched with { BoundOnPickup = false };
        var forcedBound = untouched with { BoundOnPickup = true };
        Check.True(
            forcedTradeable.BoundOnPickup == false &&
            forcedBound.BoundOnPickup == true,
            "a rule can force either answer");
    }

    /// <summary>
    /// The corpse click that opens the client's loot window and tells it which
    /// drop to move. Without this reply the installed client never sends a
    /// pickup at all, so the captured bytes are pinned exactly - including the
    /// echoed drop index, which is what the two captured clicks differ in.
    /// </summary>
    private static void CheckCapturedCorpseClickReply()
    {
        // Captured S2C 10050 at 01:37:27.113 answering the 01:37:26.770 click on
        // corpse 10492 with drop index 1: player 981, corpse 10492, index 1.
        // The 10056 at 01:37:27.118 then named record 1's item (Raw Gem 12040)
        // and its ground key E36F1A70:00065B74.
        var secondDrop = Godswar.Server.Packets.PacketBuilder.MonsterLootSourceOpen(
            981u,
            10492u,
            1);
        Check.True(
            Convert.ToHexString(secondDrop) ==
                "10004227D5030000FC28000001000000",
            "the corpse-click reply matches the captured opcode-10050 frame");

        // Captured S2C 10050 at 01:37:27.883 answering the 01:37:27.540 click
        // with drop index 0: the same corpse answered with the index echoed.
        // The 10056 at 01:37:27.888 then named record 0's item (4224) and its
        // ground key E36F18E2:00065B74.
        var firstDrop = Godswar.Server.Packets.PacketBuilder.MonsterLootSourceOpen(
            981u,
            10492u,
            0);
        Check.True(
            Convert.ToHexString(firstDrop) ==
                "10004227D5030000FC28000000000000",
            "the corpse-click reply echoes the clicked drop index");

        // Every drop source in the server - the database loot tables and the
        // Medusa instance rules - numbers its 10029 records from zero in list
        // order, so the reply has to echo any index of a corpse window, not
        // just the two captured ones.
        for (var index = 0; index < 32; index++)
        {
            var reply = Godswar.Server.Packets.PacketBuilder.MonsterLootSourceOpen(
                981u,
                10492u,
                index);
            Check.True(
                reply.Length == 16 &&
                BinaryPrimitives.ReadUInt16LittleEndian(reply) == 16 &&
                BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(2)) ==
                    Godswar.Server.Protocol.Opcodes.MoveItem &&
                BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(4)) == 981u &&
                BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(8)) == 10492u &&
                BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(12)) ==
                    (uint)index,
                $"the corpse-click reply echoes drop index {index}");
        }

        Check.True(
            Throws(() =>
                Godswar.Server.Packets.PacketBuilder.MonsterLootSourceOpen(
                    981u,
                    10492u,
                    32)),
            "a drop index outside the 10029 record cap is rejected");
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }

    /// <summary>
    /// The loot tables are database-owned, exactly like the Medusa ones: one
    /// migration owns the schema and the captured seed.
    /// </summary>
    private static void CheckMigrationOwnsTheTables()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static migration => migration.Id ==
                "20260915_147_monster_loot_policy");
        foreach (var fragment in new[]
                 {
                     "CREATE TABLE IF NOT EXISTS public.monster_loot_tables",
                     "CREATE TABLE IF NOT EXISTS public.monster_loot_rules",
                     "REFERENCES public.item_templates(id)",
                     "REFERENCES public.monster_loot_tables(template_key)",
                     "ON DELETE CASCADE",
                     "chance_basis_points BETWEEN 1 AND 10000",
                     "('A_normal_stub_001', 0, 4529, 2500, 1, 1)",
                     "('A_normal_stub_002', 5, 12040, 2500, 1, 1)",
                     "('A_normal_deer_001', 0, 4001, 2500, 1, 1)"
                 })
        {
            Check.True(
                migration.Sql.Contains(fragment, StringComparison.Ordinal),
                $"monster loot migration contains {fragment}");
        }
    }

    /// <summary>
    /// AREA LOOT (the admin a29): a table keyed <c>template@map</c> replaces the
    /// plain template table on that map only; every other map, and every
    /// template without an area table, keeps the plain table.
    /// </summary>
    private static void CheckAreaKeyedTables()
    {
        static MonsterLootRule Rule(string key, int index, uint item) =>
            new(key, index, item, ChanceBasisPoints: 10_000,
                MinimumQuantity: 1, MaximumQuantity: 1);
        var content = new MonsterLootContentSnapshot(
            [
                new("A_normal_snake_008", MaximumDrops: 1),
                new("A_normal_snake_008@15", MaximumDrops: 1),
                new("A_normal_wolf_004@5", MaximumDrops: 1)
            ],
            [
                Rule("A_normal_snake_008", 0, 4001),
                Rule("A_normal_snake_008@15", 0, 4002),
                Rule("A_normal_wolf_004@5", 0, 4003)
            ]);
        Check.Equal(
            "A_normal_snake_008@15",
            content.ResolveTableKey("A_normal_snake_008", 15),
            "an area table wins on its own map");
        Check.Equal(
            "A_normal_snake_008",
            content.ResolveTableKey("A_normal_snake_008", 13),
            "other maps fall back to the plain template table");
        Check.Equal(
            "A_normal_wolf_004@5",
            content.ResolveTableKey("A_normal_wolf_004", 5),
            "an area table works without a plain table");
        Check.True(
            content.ResolveTableKey("A_normal_wolf_004", 11) is null &&
            content.ResolveTableKey("A_normal_unknown_001", 15) is null &&
            content.ResolveTableKey(null, 15) is null &&
            content.ResolveTableKey(" ", 15) is null,
            "no table for the map or the template means no drop");
        var deathEventId = new Guid("0b7f6c3e-2f1a-4d55-8a0e-5c1d2e3f4a5b");
        Check.True(
            content.RollLoot(content.ResolveTableKey("A_normal_snake_008", 15)!, deathEventId)
                .Select(static drop => drop.ItemId).SequenceEqual([4002u]) &&
            content.RollLoot(content.ResolveTableKey("A_normal_snake_008", 13)!, deathEventId)
                .Select(static drop => drop.ItemId).SequenceEqual([4001u]),
            "the resolved key rolls that area's own rules");
        Check.Equal(
            "A_normal_stub_002",
            MonsterLootContentCatalog.Current.ResolveTableKey("A_normal_stub_002", 1),
            "the capture-proven tables resolve exactly as before");
    }

    private static void CheckCaptureProvenTables()
    {
        var content = MonsterLootContentCatalog.Current;
        Check.Equal(
            3,
            content.Tables.Count,
            "three capture-proven monsters own a loot table");

        Check.True(
            content.TryGetTable("A_normal_stub_002", out var stub) &&
            stub.MaximumDrops == 2,
            "the Athens stub caps one kill at the captured two items");

        Check.True(
            content.Loot
                .Where(static rule => rule.TemplateKey == "A_normal_stub_002")
                .Select(static rule => rule.ItemId)
                .Order()
                .SequenceEqual([4003u, 4150u, 4224u, 4529u, 12030u, 12040u]),
            "the Athens stub keeps its four-kill item set");

        Check.True(
            content.Loot
                .Where(static rule => rule.TemplateKey == "A_normal_stub_001")
                .Select(static rule => rule.ItemId)
                .SequenceEqual([4529u]) &&
            content.Loot
                .Where(static rule => rule.TemplateKey == "A_normal_deer_001")
                .Select(static rule => rule.ItemId)
                .SequenceEqual([4001u]),
            "the Sparta stub and the Athens deer keep their single capture-proven drop");

        Check.True(
            !content.TryGetTable("A_normal_unknown_001", out _) &&
            !content.TryGetTable(null, out _) &&
            content.Loot.All(static rule => rule.ChanceBasisPoints == 2_500),
            "uncaptured monsters drop nothing and seeded chances stay placeholders");
    }

    private static void CheckRollIsDeterministicAndCapped()
    {
        var content = MonsterLootContentCatalog.Current;
        var deathEventId = new Guid("6f0f1c2a-5d3b-4a1e-9c44-70b2c9a31f5d");
        var first = content.RollLoot("A_normal_stub_002", deathEventId);
        var replay = content.RollLoot("A_normal_stub_002", deathEventId);
        Check.True(
            first.SequenceEqual(replay),
            "loot rolling replays identically for one death event");

        var rules = new HashSet<uint>(
            [12030u, 4529u, 4150u, 4003u, 4224u, 12040u]);
        var overCap = false;
        var dropped = 0;
        for (var index = 0; index < 64; index++)
        {
            var guid = new Guid(index, 0, 0, new byte[8]);
            var rolled = content.RollLoot("A_normal_stub_002", guid);
            overCap |= rolled.Count > 2;
            dropped += rolled.Count;
            foreach (var drop in rolled)
            {
                overCap |= !rules.Contains(drop.ItemId) || drop.Quantity != 1;
            }
        }

        Check.True(
            !overCap,
            "rolls never exceed the captured drop cap or item set");
        Check.True(dropped > 0, "rolls do produce ground items");

        Check.True(
            content.RollLoot("A_normal_stub_002", Guid.Empty).Count == 0 &&
            content.RollLoot("A_normal_unknown_001", deathEventId).Count == 0,
            "empty identities and unknown monsters roll nothing");
    }

    private static void CheckGroundKeys()
    {
        var deathEventId = new Guid("6f0f1c2a-5d3b-4a1e-9c44-70b2c9a31f5d");
        var first = MonsterLootGroundKey.Resolve(deathEventId, 0);
        var second = MonsterLootGroundKey.Resolve(deathEventId, 1);
        Check.True(
            first.High != second.High && first.Low == second.Low,
            "ground keys identify the item per drop and the kill per event");

        var otherKill = MonsterLootGroundKey.Resolve(
            new Guid("6f0f1c2a-5d3b-4a1e-9c44-70b2c9a31f5e"),
            0);
        Check.True(
            otherKill.Low != first.Low,
            "ground keys separate two kills of the same drop");

        Check.True(
            MonsterLootGroundKey.Resolve(deathEventId, 0) == first,
            "ground keys are deterministic");
    }

    private static void CheckCapturedPickupDescriptor()
    {
        // Captured C2S 10056 at 01:37:27.118 on 2026-09-15: Raw Gem 12040 into
        // bag page 0 index 0.
        var rawGem = Convert.FromHexString(
            "280048274823E21F010000000000000000000000082F00006C44A639" +
            "000000001CFD1A009BE47F00");
        Check.True(
            GameClientHandler.TryReadGroundLootPickup(
                rawGem.AsSpan(4),
                out var rawGemSlot,
                out var rawGemItem,
                out _,
                out _) &&
            rawGemSlot == 0 &&
            rawGemItem == 12040u,
            "captured ground pickup names item 12040 in bag slot 0");

        // Captured C2S 10056 at 01:37:27.888: Level 4 Emerald Pieces 4224 into
        // bag page 0 index 13.
        var emerald = Convert.FromHexString(
            "28004827E02FE21F01000000000000000D000000801000006C44A639" +
            "000000001CFD1A009BE47F00");
        Check.True(
            GameClientHandler.TryReadGroundLootPickup(
                emerald.AsSpan(4),
                out var emeraldSlot,
                out var emeraldItem,
                out _,
                out _) &&
            emeraldSlot == 13 &&
            emeraldItem == 4224u,
            "captured ground pickup names item 4224 in bag slot 13");

        // A thirty-two byte frame is not the captured descriptor at all.
        Check.True(
            !GameClientHandler.TryReadGroundLootPickup(
                rawGem.AsSpan(4, 32),
                out _,
                out _,
                out _,
                out _),
            "the pickup parser requires the exact 40-byte descriptor");

        var outOfRange = rawGem.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            outOfRange.AsSpan(16, 4),
            24);
        Check.True(
            !GameClientHandler.TryReadGroundLootPickup(
                outOfRange.AsSpan(4),
                out _,
                out _,
                out _,
                out _),
            "the pickup parser rejects a slot index outside the bag page");
    }
}
