using System.Security.Cryptography;
using System.Text;

namespace Godswar.Server.Application.Coordination;

internal static partial class RuntimeContentFingerprint
{
    public static string Create(
        string worldRevision,
        string itemRevision,
        string petRevision,
        string petOwnerMergeRevision,
        string petLearnedSkillRevision,
        string holySpiritBalanceRevision,
        string factionCrierBalanceRevision,
        string realmCalendarCatalogRevision,
        string onlineAwardBalanceRevision,
        string warehouseExpansionPolicyRevision,
        string monsterRewardPolicyRevision,
        string monsterCombatBalanceRevision)
    {
        ValidateRevision(monsterCombatBalanceRevision, nameof(monsterCombatBalanceRevision));
        var previous = Create(worldRevision, itemRevision, petRevision,
            petOwnerMergeRevision, petLearnedSkillRevision, holySpiritBalanceRevision,
            factionCrierBalanceRevision, realmCalendarCatalogRevision, onlineAwardBalanceRevision,
            warehouseExpansionPolicyRevision, monsterRewardPolicyRevision);
        var canonical = $"runtime-content-v11\nbase:{previous}\nmonster-combat-balance:{monsterCombatBalanceRevision}\n";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
