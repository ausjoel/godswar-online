using System.Collections.Frozen;

namespace Godswar.Server.Game;

internal sealed partial class MonsterCombatProfileCatalog
{
    private FrozenDictionary<(short Map, string Template), int> DatabaseCriticalResistances
        { get; init; } = FrozenDictionary<(short, string), int>.Empty;

    private FrozenDictionary<(short Map, uint Object, string Template), MonsterCombatProfile> AuthoredOverrides
        { get; init; } = FrozenDictionary<(short, uint, string), MonsterCombatProfile>.Empty;

    internal MonsterCombatProfileCatalog WithAuthoredOverrides(short map,
        IEnumerable<(uint ObjectId, string TemplateKey, MonsterCombatProfile Profile)> profiles) =>
        new(_exact, _fallback)
        {
            DatabaseCriticalResistances = DatabaseCriticalResistances,
            AuthoredOverrides = profiles.ToFrozenDictionary(
                item => (map, item.ObjectId, item.TemplateKey), item => item.Profile)
        };
}
