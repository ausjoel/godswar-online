using Godswar.Server.Application.World.Content;
using System.Runtime.CompilerServices;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Installs the synthetic monster loot content the protocol checks run against,
/// mirroring <see cref="MedusaRewardPolicyTestFixture"/>. The rows are the
/// capture-proven drops the migration seeds, so a shape regression in either
/// place fails the suite.
/// </summary>
internal static class MonsterLootContentTestFixture
{
    [ModuleInitializer]
    public static void Install()
    {
        MonsterLootContentCatalog.Install(Create());
    }

    public static MonsterLootContentSnapshot Create() => new(
        [
            new("A_normal_stub_001", MaximumDrops: 1),
            new("A_normal_stub_002", MaximumDrops: 2),
            new("A_normal_deer_001", MaximumDrops: 1)
        ],
        [
            Rule("A_normal_stub_001", 0, 4529),
            Rule("A_normal_stub_002", 0, 12030),
            Rule("A_normal_stub_002", 1, 4529),
            Rule("A_normal_stub_002", 2, 4150),
            Rule("A_normal_stub_002", 3, 4003),
            Rule("A_normal_stub_002", 4, 4224),
            Rule("A_normal_stub_002", 5, 12040),
            Rule("A_normal_deer_001", 0, 4001)
        ]);

    private static MonsterLootRule Rule(
        string templateKey,
        int lootIndex,
        uint itemId) =>
        new(
            templateKey,
            lootIndex,
            itemId,
            ChanceBasisPoints: 2_500,
            MinimumQuantity: 1,
            MaximumQuantity: 1);
}
