using Godswar.Server.Application.Finalise2;

namespace Godswar.Server.Application.WorldInstances;

internal static class AtlantisPetSpawnPolicy
{
    public const uint MermaidObjectId = 42_245;
    public const uint SirenObjectId = 42_246;
    public const uint MermanEggItemId = 10_158;
    public const string MermaidTemplateKey = "B_normal_fish_famale_001";
    public const string SirenTemplateKey = "B_normal_fish_male_001";

    /// <summary>F-122 (the admin 2026-11-08): the Mermaid is "a monster with a lot of HP"; netting it is the easy way past
    /// it and gives a Morning Dew, not a pet. Hot switch <c>atlantis.mermaid_dew</c>, read when a run starts / a net lands.</summary>
    public static bool MermaidGivesDew => Finalise2Rates.On(Finalise2RateKeys.MermaidDew);

    public static uint MermaidDewItemId =>
        (uint)Math.Clamp(Finalise2Rates.Amount(Finalise2RateKeys.MermaidDewItem), 10_130, 10_134);

    public static bool IsMermaidDewItem(uint itemId) => itemId is >= 10_130 and <= 10_134;

    /// <summary>F-128 (the admin 2026-11-08): killing the Atlantis Mermaid never drops a Morning Dew; only the net pays.
    /// Applied to the kill's loot roll whatever the switch says, so a later loot-table edit cannot add one.</summary>
    public static bool BlocksMermaidKillDrop(int mapId, string? templateKey, uint itemId) =>
        mapId == 205 && templateKey == MermaidTemplateKey && IsMermaidDewItem(itemId);

    /// <summary>EXP Stone 4 (AddEXP4, stacks to 99).</summary>
    public const uint MermaidKillStoneItemId = 4_173;

    /// <summary>the admin 2026-11-11: "Mermaid should actually give an exp stone 4 as well". Killing the Atlantis Mermaid
    /// puts one EXP Stone 4 in the corpse (100%, quantity 1, outside the loot table and the drop rates). Hot switch
    /// <c>atlantis.mermaid_kill_exp_stone</c> (default 1).</summary>
    public static bool DropsMermaidKillStone(int mapId, string? templateKey) =>
        mapId == 205 && templateKey == MermaidTemplateKey && Finalise2Rates.On(Finalise2RateKeys.MermaidKillExpStone);

    public static bool Matches(uint objectId, string templateKey) => objectId switch
    {
        MermaidObjectId => templateKey == MermaidTemplateKey,
        SirenObjectId => templateKey == SirenTemplateKey,
        _ => false
    };

    public static IReadOnlyList<(uint ObjectId, AtlantisMonsterSpawnIdentity Placement)> Spawns { get; } =
        Array.AsReadOnly(new (uint, AtlantisMonsterSpawnIdentity)[]
        {
            (MermaidObjectId, new(MermaidTemplateKey, AtlantisMonsterRank.Normal, -59f, 0f, 88f)),
            (SirenObjectId, new(SirenTemplateKey, AtlantisMonsterRank.Normal, -54f, 0f, -7f))
        });
}
