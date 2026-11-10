using Godswar.Server.Application.Progression;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly ICapitalShopPurchaseStore _capitalShopPurchases;
    private readonly IMonsterRewardExtrasStore _monsterRewardExtras;
    private readonly IFighterLevelSealStore? _fighterLevelSeals;
    private readonly IWeekendExperienceClaimStore? _weekendExperienceClaims;
}
