using System.Reflection;
using Godswar.Server.Infrastructure.Items;

namespace Godswar.Server.ProtocolChecks;

internal static class PetItemsV3UpgradePolicyChecks
{
    public const string CheckName =
        "Exact pets-v3 through capture-tool publication lineage";

    public static Task RunAsync()
    {
        const BindingFlags flags =
            BindingFlags.NonPublic | BindingFlags.Static;
        var type = typeof(PostgresItemTemplateBaselinePublisher);
        Check.Equal(
            "BCF91FCD7A9E3C5EA93B774143B5D2F9B714B147E40EBF0B85C639CF0DD63057",
            type.GetField("OfficialPetItemsV3Revision", flags)
                ?.GetRawConstantValue() as string ?? string.Empty,
            "only the exact sealed pets-v3 SHA is an upgrade predecessor");
        Check.Equal(
            "items-v9+holy-v3+element-v1+sockets-v1+holy-stones-v2+" +
            "zephyr-v1+mount-speed-v3+pets-v3",
            type.GetField("OfficialPetItemsV3Source", flags)
                ?.GetRawConstantValue() as string ?? string.Empty,
            "pets-v3 predecessor also pins its exact source label");
        var publicationSource = type.GetField("PublicationSource", flags)
            ?.GetRawConstantValue() as string ?? string.Empty;
        // The label must name the lineage the realms have actually sealed: the
        // publisher accepts a stored release only when its source is this value
        // or a strict prefix of it. A shorter label (the former
        // "pets-v5 ... client-catalog-v1" form) matched neither test and
        // blocked startup against a deployed database.
        Check.Equal(
            "items-v9+pets-v7+nameplates-v1+warehouse-v1+opal-v1+holy-v5+" +
            "holy-stones-v3+sockets-v2+ascension-v1+wonderland-v1+exp-pill-v1",
            publicationSource,
            "the item publication label names the deployed provenance lineage");
        Check.True(publicationSource.Length is > 0 and <= 128,
            "current item publication provenance fits the durable source column");
        return Task.CompletedTask;
    }
}
