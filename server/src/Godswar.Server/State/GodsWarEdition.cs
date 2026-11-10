namespace Godswar.Server.State;

/// <summary>Which rule set the server runs: "reborn" (default, limits raised past the original game) or "classic" (original limits). Set with GODSWAR_EDITION.</summary>
public static class GodsWarEdition
{
    public static readonly string Name =
        string.Equals(Environment.GetEnvironmentVariable("GODSWAR_EDITION"), "classic", StringComparison.OrdinalIgnoreCase) ? "classic" : "reborn";

    public static bool IsClassic => Name == "classic";
}
