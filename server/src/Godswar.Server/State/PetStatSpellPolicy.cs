namespace Godswar.Server.State;

/// <summary>
/// W0 F-93 (the admin): pet stat spells 10500-10508 (client ItemBaseAttribute Values = stat, minimum, maximum in
/// hundredths; EquipDescription: "increase your pet's agility anywhere from 0.01 to 0.20 point").
/// Stat 0 = rank; 1..6 = the six Basic Savvy stats (stat_code 1..6, the client's first Savvy column).
/// </summary>
internal static class PetStatSpellPolicy
{
    internal readonly record struct Spell(uint ItemId, short StatCode, int MinimumHundredths, int MaximumHundredths);

    private static readonly IReadOnlyDictionary<uint, Spell> Spells = new Dictionary<uint, Spell>
    {
        [10500] = new(10500, 0, 1, 20),   // Rank Spell
        [10501] = new(10501, 1, 1, 20),   // Agility Spell
        [10502] = new(10502, 2, 1, 20),   // Strength Spell
        [10503] = new(10503, 3, 1, 20),   // Accuracy Spell
        [10504] = new(10504, 4, 1, 20),   // Technique Spell
        [10505] = new(10505, 5, 1, 20),   // Wisdom Spell
        [10506] = new(10506, 6, 1, 20),   // Luck Spell
        [10507] = new(10507, 0, 10, 10),  // Rank Spell I (+0.1)
        [10508] = new(10508, 0, 100, 100) // Rank Spell II (+1)
    };

    public static bool TryResolve(uint itemId, out Spell spell) => Spells.TryGetValue(itemId, out spell);

    public static bool IsSpell(uint itemId) => Spells.ContainsKey(itemId);

    public static decimal RollGain(Spell spell) =>
        Random.Shared.Next(spell.MinimumHundredths, spell.MaximumHundredths + 1) / 100m;
}
