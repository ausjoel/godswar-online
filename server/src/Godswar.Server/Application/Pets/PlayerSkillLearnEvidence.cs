namespace Godswar.Server.Application.Pets;

internal sealed record PlayerSkillLearnEvidence(
    long ItemInstanceId,
    int ItemTemplateId,
    int KitBagSlot,
    int SkillId,
    short SkillLevel,
    int? PreviousSkillId,
    string BaseName,
    short Profession,
    int CharacterLevel,
    string ItemContentRevision,
    string GameplayContentRevision)
{
    public bool IsValid =>
        ItemInstanceId > 0 &&
        ItemTemplateId > 0 &&
        KitBagSlot is >= 0 and <= 95 &&
        SkillId >= 0 &&
        SkillLevel is >= 1 and <= byte.MaxValue &&
        PreviousSkillId is null or >= 0 &&
        !string.IsNullOrWhiteSpace(BaseName) &&
        BaseName.Length <= 128 &&
        !BaseName.Any(char.IsControl) &&
        Profession >= 0 &&
        CharacterLevel is >= 1 and <= 200 &&
        IsSha256(ItemContentRevision) &&
        IsSha256(GameplayContentRevision);

    private static bool IsSha256(string value) =>
        value is { Length: 64 } &&
        value.All(static character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');
}
