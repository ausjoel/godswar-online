namespace Godswar.Server.Application.World.Content;

/// <summary>
/// B22 B23-F (the admin d86, P1: "learnt from books like Flame Blast"): the class field books drop where the Mage's Flame
/// Blast books drop and exactly as often. Whenever a monster's table drops a Flame Blast book of rank N (items
/// 5427-5431) for a Warrior, Champion or Priest killer, the corpse also gets that killer's own class field book of rank
/// N (Earthsplitter / Storm of Spears / Holy Pyre). The Flame Blast book still drops (a Mage killer gets nothing extra),
/// so the Mage book supply is unchanged and every class has the same chance per kill. A code rule because most of these
/// tables already use all 32 loot slots (like the K1 chests). Switch drop.class_field_books (1 = on).
/// </summary>
internal static class ClassFieldBookLootRule
{
    public const string SwitchKey = "drop.class_field_books";
    public const uint FlameBlastBookFirst = 5427;
    public const uint FirstBookItemId = 6540;

    /// <summary>Book item for profession 0/1/2 at rank 1-5: 6540-6544 Warrior, 6545-6549 Champion, 6550-6554 Priest.</summary>
    public static uint BookItemId(byte profession, int rank) =>
        profession <= 2 && rank is >= 1 and <= 5
            ? FirstBookItemId + profession * 5u + (uint)(rank - 1)
            : 0;

    public static IReadOnlyList<RolledMonsterLoot> Add(
        IReadOnlyList<RolledMonsterLoot> drops, IReadOnlyList<RolledMonsterLoot> tableDrops, byte killerProfession,
        Godswar.Server.Application.Rates.ServerRatesSnapshot rates)
    {
        if (killerProfession > 2 || rates.Get(SwitchKey) < 1m)
        {
            return drops;
        }

        var result = drops;
        foreach (var drop in tableDrops)
        {
            if (drop.ItemId < FlameBlastBookFirst || drop.ItemId > FlameBlastBookFirst + 4)
            {
                continue;
            }

            var book = BookItemId(killerProfession, (int)(drop.ItemId - FlameBlastBookFirst) + 1);
            var used = result.Select(static value => value.LootIndex).ToHashSet();
            for (var index = CorpseLootLimit.LootIndexCount - 1; index >= 0; index--)
            {
                if (used.Contains(index))
                {
                    continue;
                }

                // Same rarity as the Flame Blast book it follows, so the corpse cap ranks them alike.
                result = [.. result, new RolledMonsterLoot(index, book, 1, null, ChanceBasisPoints: drop.ChanceBasisPoints)];
                break;
            }
        }

        return result;
    }
}
