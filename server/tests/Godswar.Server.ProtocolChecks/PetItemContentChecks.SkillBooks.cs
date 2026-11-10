namespace Godswar.Server.ProtocolChecks;

internal static partial class PetItemContentChecks
{
    private static ExpectedItem[] CreateExpectedPetSkillBookItems() =>
    [
        .. Family(10464, ["Pet Skill:Wild Bump I", "Pet Skill:Wild Bump II", "Pet Skill:Wild Bump III", "Pet Skill:Wild Bump IV", "Pet Skill:Wild Bump V", "Pet Skill:Wild Bump VI"], [3900, 3904, 3908, 3912, 3916, 3920]),
        .. Family(10510, ["Pet Skill: Wild Strength I", "Pet Skill:Wild Strength  II", "Pet Skill:Wild Strength  III", "Pet Skill:Wild Strength  IV", "Pet Skill:Wild Strength  V", "Pet Skill:Wild Strength  VI"], [4500, 4503, 4507, 4511, 4515, 4519]),
        .. Family(10530, ["Pet Skill: Focus  I", "Pet Skill:Focus  II", "Pet Skill:Focus  III", "Pet Skill:Focus  IV", "Pet Skill:Focus  V", "Pet Skill:Focus  VI"], [4600, 4604, 4608, 4612, 4616, 4620]),
        .. Family(10590, ["Pet Skill: Violent Strength I", "Pet Skill:Violent Strength II", "Pet Skill:Violent Strength III", "Pet Skill:Violent Strength IV", "Pet Skill:Violent Strength V", "Pet Skill:Violent Strength VI"], [5200, 5204, 5208, 5212, 5216, 5220]),
        .. Family(10700, ["Pet Skill: Resolute Physique I", "Pet Skill: Resolute Physique II", "Pet Skill: Resolute Physique III", "Pet Skill: Resolute Physique IV", "Pet Skill: Resolute Physique V", "Pet Skill: Resolute Physique VI"], [5600, 5604, 5608, 5612, 5616, 5620]),
        Book(10745, "Pet Skill: Spiky Armor VI", itemType: "3", petSkill: "6020")
    ];

    /// <summary>
    /// The six authored Vampiric tiers sit above every stock pet-item
    /// identity, so they close the reviewed baseline.
    /// </summary>
    private static ExpectedItem[] CreateExpectedVampiricBookItems() =>
    [
        Book(16400, "Pet Skill: Vampiric I", itemType: "4", petSkill: "6400"),
        Book(16401, "Pet Skill: Vampiric II", itemType: "3", petSkill: "6401"),
        Book(16402, "Pet Skill: Vampiric III", itemType: "3", petSkill: "6402"),
        Book(16403, "Pet Skill: Vampiric IV", itemType: "3", petSkill: "6403"),
        Book(16404, "Pet Skill: Vampiric V", itemType: "3", petSkill: "6404"),
        Book(16405, "Pet Skill: Vampiric VI", itemType: "3", petSkill: "6405")
    ];

    private static ExpectedItem Book(
        int itemId,
        string displayName,
        string itemType,
        string petSkill) =>
        E(
            itemId,
            $"Pet{itemId}",
            displayName,
            "216,972",
            "99",
            use: "1",
            itemType: itemType,
            petSkill: petSkill);

    private static IEnumerable<ExpectedItem> Family(
        int firstItemId,
        IReadOnlyList<string> displayNames,
        IReadOnlyList<int> petSkillIds)
    {
        for (var index = 0; index < displayNames.Count; index++)
        {
            var itemId = firstItemId + index;
            yield return E(
                itemId,
                $"Pet{itemId}",
                displayNames[index],
                "216,972",
                "99",
                use: "1",
                itemType: index == 0 ? "4" : "3",
                petSkill: petSkillIds[index].ToString());
        }
    }
}
