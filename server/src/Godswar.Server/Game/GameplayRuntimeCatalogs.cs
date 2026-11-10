using Godswar.Server.Application.Rewards;
using Godswar.Server.Application.World;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// Immutable gameplay views derived once from the process-pinned PostgreSQL
/// world-content snapshot. Runtime systems receive this instance through
/// composition and never consult generated seed declarations directly.
/// </summary>
internal sealed record GameplayRuntimeCatalogs(
    GameplayContentCatalog Content,
    MonsterRewardPolicySnapshot MonsterRewards,
    MapTraversalCatalog MapTraversal,
    WorldBossCatalog WorldBosses,
    SkillCombatCatalog SkillCombat,
    MonsterCombatRangeCatalog MonsterCombatRanges,
    MonsterCombatProfileCatalog MonsterCombatProfiles,
    PvpWorldAuthorityCatalog PvpWorldAuthority)
{
    public static GameplayRuntimeCatalogs Empty { get; } = new(
        GameplayContentCatalog.Empty,
        MonsterRewardPolicySnapshot.Default,
        MapTraversalCatalog.Empty,
        WorldBossCatalog.Empty,
        SkillCombatCatalog.Empty,
        MonsterCombatRangeCatalog.Empty,
        MonsterCombatProfileCatalog.Empty,
        PvpWorldAuthorityCatalog.Empty);

    public static GameplayRuntimeCatalogs Create(
        GameplayContentCatalog content,
        MonsterRewardPolicySnapshot? monsterRewards = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var rewardPolicy = monsterRewards ??
            MonsterRewardPolicySnapshot.Default;
        rewardPolicy.Validate();
        if (content == GameplayContentCatalog.Empty ||
            content.Maps.Count == 0 &&
            content.AddressPoints.Count == 0 &&
            content.Links.Count == 0 &&
            content.MonsterTemplates.Count == 0 &&
            content.WorldBosses.Count == 0 &&
            content.PendingWorldBossAreas.Count == 0 &&
            content.SkillCombatDefinitions.Count == 0)
        {
            return Empty with { MonsterRewards = rewardPolicy };
        }

        return new GameplayRuntimeCatalogs(
            content,
            rewardPolicy,
            MapTraversalCatalog.Create(content),
            WorldBossCatalog.Create(content),
            SkillCombatCatalog.Create(content),
            MonsterCombatRangeCatalog.Create(content),
            MonsterCombatProfileCatalog.Create(content),
            PvpWorldAuthorityCatalog.Create(content));
    }
}
