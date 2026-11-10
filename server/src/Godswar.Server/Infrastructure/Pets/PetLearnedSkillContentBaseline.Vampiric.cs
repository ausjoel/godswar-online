namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PetLearnedSkillContentBaseline
{
    public const string Source = "installed-en-us-pet-skill-plus-vampiric-v1";
    // SHA256 of UTF-8: "pet-learned-skill-extension-v1\ninstalled-source-sha256:"
    // + InstalledSourceSha256 + "\n" + LF-normalized VampiricData + "\n".
    public const string SourceSha256 =
        "76B688436DFD14A2E071B2CA0DD450F48A4509D24538A515D92182B04B7364D9";
    public const string ExpectedRevision =
        "4B57F8562B679692E2C1517D26290FE9D05D1CF079492CB3E785284F56E7260A";

    // Effect 34 is the native healing presentation. Family 428 deliberately
    // uses fractions of committed damage, distinct from native flat healing.
    private const string VampiricData =
        """
        428|1|34|34|1|1|0,0,0,0,0,0|6400|0|0.06
        428|2|34|34|1|1|0,0,64,0,0,0|6401|0|0.08
        428|3|34|34|1|1|0,0,192,0,0,0|6402|0|0.11
        428|4|34|34|1|1|0,0,235,0,0,0|6403|0|0.14
        428|5|34|34|1|1|0,0,270,0,0,0|6404|0|0.17
        428|6|34|34|1|1|0,0,305,0,0,0|6405|0|0.20
        """;
}
