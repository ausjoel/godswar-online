namespace Godswar.Server.Application.Items;

internal static class HolySuitProgressionPolicy
{
    public const short MaximumSuitType = 8;
    public const short MaximumLevel = 10;

    public static bool TryReadCode(int code, out short suitType, out short suitLevel)
    {
        suitType = 0;
        suitLevel = 0;
        if (code == 0) return true;
        if (code < 0 || code > MaximumSuitType * 100 + MaximumLevel) return false;
        var type = code / 100;
        var level = code % 100;
        if (type is < 1 or > MaximumSuitType || level is < 1 or > MaximumLevel) return false;
        suitType = (short)type;
        suitLevel = (short)level;
        return true;
    }

    public static bool IsMaximum(int code) => code == MaximumSuitType * 100 + MaximumLevel;

    public static int GetBonusPercent(int code)
    {
        if (!TryReadCode(code, out var type, out var level) || type == 0) return 0;
        if (type < 8) return (type - 1) * 10 + level;
        return level switch
        {
            1 => 71, 2 => 73, 3 => 75, 4 => 77, 5 => 79,
            6 => 82, 7 => 84, 8 => 86, 9 => 88, 10 => 90,
            _ => 0
        };
    }

    public static int GetEffectPoints(int code) => GetBonusPercent(code);
}
