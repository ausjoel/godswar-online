using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class IntonedCombatSkillHandlerChecks
{
    public const string VampiricDamageCheckName =
        "Vampiric percentage healing follows each actual AOE target damage";

    public static async Task RunVampiricDamageAsync()
    {
        Check.True(GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(580, out var combat),
            "Vampiric percentage fixture uses the published mage area skill");
        Check.True(GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(574, out var flameBlast),
            "Vampiric also uses the distinct published Flame Blast ground skill");
        foreach (var mode in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
        {
            await CheckLifeAbsorptionAreaAsync(mode, combat, 100, 0, null, 600);
            await CheckLifeAbsorptionAreaAsync(mode, combat, 100, 7, null, 2_000);
            await CheckLifeAbsorptionAreaAsync(mode, combat, 410, 7, null, 2_000);
            await CheckLifeAbsorptionAreaAsync(mode, combat, 500, 7, null, 2_000);
            await CheckLifeAbsorptionAreaAsync(mode, flameBlast, 100, 7, null, 2_000);
        }
    }
}
