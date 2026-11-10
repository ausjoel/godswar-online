namespace Godswar.Server.Domain.Characters;

internal static class FactionPortalSkillPolicy
{
    public const byte SpartaCamp = 0;

    public const byte AthensCamp = 1;

    public const uint AthensCapitalPortalSkillId = 3060;

    public const uint AthensSuburbPortalSkillId = 3061;

    public const uint SpartaCapitalPortalSkillId = 3062;

    public const uint SpartaSuburbPortalSkillId = 3063;

    public static uint ResolveCapitalPortalSkillId(byte camp) =>
        camp switch
        {
            SpartaCamp => SpartaCapitalPortalSkillId,
            AthensCamp => AthensCapitalPortalSkillId,
            _ => throw new ArgumentOutOfRangeException(
                nameof(camp),
                camp,
                "Faction portal skills require a recognized camp.")
        };

    public static uint ResolveSuburbPortalSkillId(byte camp) =>
        camp switch
        {
            SpartaCamp => SpartaSuburbPortalSkillId,
            AthensCamp => AthensSuburbPortalSkillId,
            _ => throw new ArgumentOutOfRangeException(
                nameof(camp),
                camp,
                "Faction portal skills require a recognized camp.")
        };
}
