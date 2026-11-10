using System.Globalization;
using Godswar.Server.Application.Progression;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal static class DeveloperProgressionChatCommand
{
    public const string LevelPrefix = "/level";
    public const string TalentPointsPrefix = "/talentpoints";
    public const string TalentAliasPrefix = "/talent";
    public const string ZodiacEnergyPrefix = "/zodiacenergy";
    public const string ZodiacEnergyAliasPrefix = "/zenergy";

    public const string Usage =
        "Usage: /level set <1-200>, /level add <signed-delta>, " +
        "/talentpoints add <amount>, or /zodiacenergy add <amount>.";

    private static readonly string[] Prefixes =
    [
        TalentPointsPrefix,
        ZodiacEnergyPrefix,
        ZodiacEnergyAliasPrefix,
        TalentAliasPrefix,
        LevelPrefix
    ];

    public static bool TryParse(
        string text,
        out DeveloperProgressionCommand? command,
        out string error)
    {
        command = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var offset = FindCommandOffset(text, out var prefix);
        if (offset < 0)
        {
            return false;
        }

        var tokens = text[offset..].Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        if (tokens.Length != 3 ||
            !tokens[0].Equals(prefix, StringComparison.OrdinalIgnoreCase))
        {
            error = Usage;
            return true;
        }

        if (!tokens[1].Equals("add", StringComparison.OrdinalIgnoreCase) &&
            !(prefix.Equals(LevelPrefix, StringComparison.OrdinalIgnoreCase) &&
              tokens[1].Equals("set", StringComparison.OrdinalIgnoreCase)))
        {
            error = Usage;
            return true;
        }

        if (!int.TryParse(
                tokens[2],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var value))
        {
            error = "Value must be a base-10 32-bit integer.";
            return true;
        }

        DeveloperProgressionOperation operation;
        if (prefix.Equals(LevelPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (tokens[1].Equals("set", StringComparison.OrdinalIgnoreCase))
            {
                operation = DeveloperProgressionOperation.SetFighterLevel;
                if (value is < 1 or > PlayerExperienceCatalog.MaximumLevel)
                {
                    error = $"Fighter level must be from 1 to " +
                        $"{PlayerExperienceCatalog.MaximumLevel}.";
                    return true;
                }
            }
            else
            {
                operation =
                    DeveloperProgressionOperation.AdjustFighterLevel;
                if (value is < -199 or > 199 or 0)
                {
                    error =
                        "Level delta must be from -199 to 199 and cannot be zero.";
                    return true;
                }
            }
        }
        else
        {
            operation = prefix.Equals(
                TalentPointsPrefix,
                StringComparison.OrdinalIgnoreCase) ||
                prefix.Equals(
                    TalentAliasPrefix,
                    StringComparison.OrdinalIgnoreCase)
                ? DeveloperProgressionOperation.AddTalentPoints
                : DeveloperProgressionOperation.AddZodiacEnergy;
            if (value <= 0)
            {
                error = "Amount must be a positive 32-bit integer.";
                return true;
            }
        }

        command = new DeveloperProgressionCommand(operation, value);
        return true;
    }

    // ROUND2: only at the start of the message (DeveloperCommandText);
    // "I am /level 5 now" is ordinary chat.
    private static int FindCommandOffset(
        string text,
        out string matchedPrefix) =>
        DeveloperCommandText.FindCommandOffset(
            text,
            Prefixes,
            out matchedPrefix);
}
