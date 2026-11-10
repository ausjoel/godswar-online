using System.Collections.Frozen;
using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

internal enum ProgressItemKind : byte
{
    Gold = 1,
    BindingGold = 2,
    Honor = 3,
    ExchangePoint = 4,
    TalentPoints = 5,
    GuildSilver = 6,
    GuildGold = 7,
    Firework = 8,
    StatusBuff = 9,
    Teleport = 10,
    Silver = 11,
    Title = 12,
    Sigil = 13
}

internal readonly record struct ProgressItemGrant(int SkillId, ProgressItemKind Kind, int Amount, int MaximumAmount = 0);

/// <summary>
/// Flat currency and progress rewards of bag items. The Skill link stays pinned to item content.
/// </summary>
/// <remarks>
/// Sources, reviewed against the Q20 client (Magic.ini, EquipDescription.dat):
/// <list type="bullet">
/// <item>B-Gold sacks, talent stones, Medal of Honor, Honor Badge, HardPoint items: Magic.ini Power2, same number in the item text.</item>
/// <item>Lucky Money: item text "2009 B-GOLD" (Power2 is 0).</item>
/// <item>Warrior's Soul: item text says 88, Power2 says 80. The text is what players were promised.</item>
/// <item>Super Skill Stone: Power2 100, item text only "numerous".</item>
/// <item>Gold bags and rank badges: 2.51 data has no number. Amounts are from the 2008 server's Skill.py
/// (AddStone1-4, AddGlory1-4). That older script is a different version of the game: its money bags paid less
/// than 2.51's, so these two groups may be lower than retail 2.51.</item>
/// </list>
/// <item>Silver Token 20,000 guild silver, Gold Token 100 guild gold: 2008 Skill.py GetConsortiaData (2.51 text has no number).
/// Guild Badge 4360 is absent: the same script gives 30 of "type 2", and this server's contribution is two silver/gold balances.</item>
/// <item>Fireworks: Magic.ini names the effect file; the server only consumes the item and shows the cast to the map.</item>
/// Vigor potions restore AP, which this server does not have.
/// </remarks>
internal static class ProgressItemPolicy
{
    private static readonly FrozenDictionary<uint, ProgressItemGrant> Grants =
        new Dictionary<uint, ProgressItemGrant>
        {
            [4160] = new(4610, ProgressItemKind.Gold, 10),
            [4161] = new(4611, ProgressItemKind.Gold, 20),
            [4162] = new(4612, ProgressItemKind.Gold, 50),
            [4163] = new(4613, ProgressItemKind.Gold, 100),
            [4164] = new(4614, ProgressItemKind.BindingGold, 2_009),
            [4194] = new(4624, ProgressItemKind.BindingGold, 500),
            [4195] = new(4625, ProgressItemKind.BindingGold, 1_000),
            [4196] = new(4626, ProgressItemKind.BindingGold, 3_000),
            [4197] = new(4627, ProgressItemKind.BindingGold, 5_000),
            [4198] = new(4628, ProgressItemKind.BindingGold, 10_000),
            [4199] = new(4629, ProgressItemKind.BindingGold, 50_000),
            [4180] = new(4630, ProgressItemKind.TalentPoints, 5),
            [4181] = new(4631, ProgressItemKind.TalentPoints, 10),
            [4182] = new(4632, ProgressItemKind.TalentPoints, 20),
            [4183] = new(4633, ProgressItemKind.TalentPoints, 100),
            [4190] = new(4640, ProgressItemKind.Honor, 20),
            [4191] = new(4641, ProgressItemKind.Honor, 40),
            [4192] = new(4642, ProgressItemKind.Honor, 100),
            [4193] = new(4643, ProgressItemKind.Honor, 200),
            [3902] = new(4746, ProgressItemKind.Honor, 100),
            [3943] = new(4750, ProgressItemKind.Honor, 88),
            [3944] = new(4751, ProgressItemKind.Honor, 888),
            [3957] = new(4757, ProgressItemKind.ExchangePoint, 100),
            [3981] = new(4758, ProgressItemKind.ExchangePoint, 888),
            [4340] = new(4660, ProgressItemKind.GuildSilver, 20_000),
            [4350] = new(4670, ProgressItemKind.GuildGold, 100),
            [3939] = new(4821, ProgressItemKind.Firework, 1),
            [3940] = new(4822, ProgressItemKind.Firework, 1),
            [3941] = new(4823, ProgressItemKind.Firework, 1),
            [3942] = new(4824, ProgressItemKind.Firework, 1),
            [3949] = new(4825, ProgressItemKind.Firework, 1),
            [3950] = new(4826, ProgressItemKind.Firework, 1),
            // Silver cards and boxes: item text. Super Money Bag: inferred, see Development/consumable-audit/ITEM-DECISIONS.md.
            [4154] = new(5614, ProgressItemKind.Silver, 1_000),
            [4155] = new(5615, ProgressItemKind.Silver, 10_000),
            [4156] = new(5616, ProgressItemKind.Silver, 30_000),
            [4157] = new(5617, ProgressItemKind.Silver, 50_000),
            [4153] = new(4603, ProgressItemKind.Silver, 1_000_000),
            // Anniversary Gold Sack: item text gives the range; even odds are inferred.
            [3989] = new(4617, ProgressItemKind.BindingGold, 1_000, 10_000),
            // Amounts the admin chose 2026-10-31; even odds.
            [3961] = new(5303, ProgressItemKind.BindingGold, 1_000, 5_000),
            [4165] = new(4616, ProgressItemKind.BindingGold, 5_000, 15_000),
            // Made up, the admin's decision 2026-10-31: the text gives only the most a packet holds; even odds up to it.
            [3935] = new(5128, ProgressItemKind.Gold, 1, 2_000),
            [3936] = new(5129, ProgressItemKind.Silver, 1, 20_000),
            // Guardian's Badge: Amount is the title id, Divine Guardian. The text says 30 days; permanent by the admin's decision.
            [4115] = new(5613, ProgressItemKind.Title, 5129)
        }.Concat(StatusItemPolicy.ProgressGrants).Concat(TeleportItemPolicy.ProgressGrants).Concat(SigilStonePolicy.ProgressGrants).ToFrozenDictionary();

    public static bool TryResolve(IItemTemplateCatalog templates, uint itemId, out ProgressItemGrant grant) =>
        Grants.TryGetValue(itemId, out grant) &&
        BagConsumableItemStats.TryResolveSkillId(templates, itemId, out var skill) && skill == grant.SkillId;

    public static string UnitName(ProgressItemKind kind) => kind switch
    {
        ProgressItemKind.Gold => "Gold",
        ProgressItemKind.BindingGold => "B-Gold",
        ProgressItemKind.Honor => "Honor",
        ProgressItemKind.ExchangePoint => "Hard Points",
        ProgressItemKind.TalentPoints => "Talent Points",
        ProgressItemKind.GuildSilver => "guild Silver",
        ProgressItemKind.GuildGold => "guild Gold",
        ProgressItemKind.Firework => "Firework",
        ProgressItemKind.StatusBuff => "status",
        ProgressItemKind.Teleport => "teleport",
        ProgressItemKind.Silver => "Silver",
        ProgressItemKind.Title => "title",
        ProgressItemKind.Sigil => "damage",
        _ => throw new InvalidDataException("Unknown progress item kind.")
    };
}
