using Godswar.Server.Application.Guilds;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class CachedVitalsProjectionChecks
{
    public const string CheckName = "Stat refresh caches live health and mana without compounding altar bonuses";
    public static Task RunAsync()
    {
        const int id = 875031;
        var character = new GameCharacter { Id = id, AccountId = 44, MaxHp = 6000, MaxMp = 297,
            CurrentHp = 5000, CurrentMp = 250 };
        var baseline = new CharacterStats { CharacterId = id, AccountId = 44, MaxHp = 1500,
            MaxMp = 177, CurrentHp = 1, CurrentMp = 1, PhysicalAttack = 100 };
        GuildAltarBonusCache.Set(id, new GuildAltarAttributeBonus(MaxHp: 4500, MaxMp: 120, PhysicalAttack: 30));
        try
        {
            for (var i = 0; i < 5; i++)
            {
                CharacterCalculatedStatsProjectionApplier.Apply(character, character.CalculatedStats ?? baseline,
                    CharacterHealthProjectionMode.PreserveAbsolute);
                Check.Equal(6000, character.MaxHp, "altar HP applied once across repeated refreshes");
                Check.Equal(297, character.MaxMp, "altar MP applied once across repeated refreshes");
                Check.Equal(1500, character.CalculatedStats!.MaxHp, "cached HP ceiling remains base-only");
                Check.Equal(177, character.CalculatedStats.MaxMp, "cached MP ceiling remains base-only");
                Check.Equal(5000, character.CalculatedStats.CurrentHp, "cache contains live HP above base ceiling");
                Check.Equal(250, character.CalculatedStats.CurrentMp, "cache contains live MP above base ceiling");
                var displayed = CharacterStats.FromCharacter(character);
                Check.Equal(130, displayed.PhysicalAttack, "combat attributes receive altar exactly once");
                Check.Equal(5000, displayed.CurrentHp, "stats view keeps live HP rather than stale persisted HP");
                Check.Equal(250, displayed.CurrentMp, "stats view keeps live MP rather than stale persisted MP");
            }
            var cached = character.CalculatedStats;
            CharacterCalculatedStatsProjectionApplier.Apply(character, cached!, CharacterHealthProjectionMode.PreserveAbsolute);
            Check.True(ReferenceEquals(cached, character.CalculatedStats), "unchanged reusable base retains identity");
            character.CurrentHp = 0; character.CurrentMp = 0;
            CharacterCalculatedStatsProjectionApplier.Apply(character, cached!, CharacterHealthProjectionMode.PreservePercentage);
            Check.Equal(0, character.CurrentHp, "refresh never resurrects");
            Check.Equal(0, character.CalculatedStats!.CurrentHp, "dead HP replaces stale cached HP");
            Check.Equal(0, character.CalculatedStats.CurrentMp, "spent mana replaces stale cached mana");
        }
        finally { GuildAltarBonusCache.Clear(id); }
        return Task.CompletedTask;
    }
}
