using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.Rewards;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Application.World;
using Godswar.Server.Application.World.Content;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.Infrastructure.WorldInstances;
using Godswar.Server.Infrastructure.Rewards;
using Godswar.Server.State;

namespace Godswar.Server;

internal sealed record ServerStartupContent(
    IWorldContentReader World,
    GameplayItemContent Items,
    PinnedPetContentCatalog Pets,
    PinnedPetOwnerMergeContentCatalog PetOwnerMerge,
    PinnedPetLearnedSkillContentCatalog LearnedSkills,
    HolySpiritBalanceSnapshot HolySpiritBalance,
    WarehouseExpansionPolicySnapshot WarehouseExpansionPolicy,
    MonsterRewardPolicySnapshot MonsterRewardPolicy,
    GameplayRuntimeCatalogs GameplayCatalogs);

internal static partial class ServerRuntimeContentComposition
{
    public static async Task<ServerStartupContent?> LoadStartupContentAsync(
        ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var items = await ServerItemContentComposition.LoadAsync(options);
        var pets = await ServerPetContentComposition.LoadAsync(
            options,
            items.Templates);
        var petOwnerMerge =
            await ServerPetOwnerMergeContentComposition.LoadAsync(options);
        var learnedSkills = await LoadLearnedSkillsAsync(options);
        var holySpiritBalance = await LoadHolySpiritBalanceAsync(options);
        var warehouseExpansionPolicy =
            await LoadWarehouseExpansionPolicyAsync(options, items);
        var medusaRewards =
            await PostgresMedusaRewardPolicySnapshotReader.LoadAsync(
                options.Storage.PostgresConnectionString);
        var medusaMonsters =
            await PostgresMedusaMonsterContentSnapshotReader.LoadAsync(
                options.Storage.PostgresConnectionString);
        var monsterRewardPolicy =
            await PostgresMonsterRewardPolicySnapshotReader.LoadAsync(
                options.Storage.PostgresConnectionString);
        var monsterLoot =
            await PostgresMonsterLootContentSnapshotReader.LoadAsync(
                options.Storage.PostgresConnectionString);
        var questRewards =
            await PostgresQuestRewardContentSnapshotReader.LoadAsync(
                options.Storage.PostgresConnectionString);
        MedusaRewardPolicyCatalog.Install(medusaRewards);
        MedusaMonsterContentCatalog.Install(medusaMonsters);
        MonsterLootContentCatalog.Install(monsterLoot);
        QuestRewardContentCatalog.Install(questRewards);
        var world = await ServerWorldContentComposition.TryLoadAsync(options);
        if (world is null)
        {
            return null;
        }

        RuntimeContentCompatibilityValidator.Validate(
            items.Templates,
            world.Gameplay);
        return new ServerStartupContent(
            world,
            items,
            pets,
            petOwnerMerge,
            learnedSkills,
            holySpiritBalance,
            warehouseExpansionPolicy,
            monsterRewardPolicy,
            GameplayRuntimeCatalogs.Create(
                world.Gameplay,
                monsterRewardPolicy));
    }
}
