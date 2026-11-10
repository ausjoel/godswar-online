namespace Godswar.Server.Game;

/// <summary>
/// File trace for the ground-loot channel. The runtime folds
/// <c>Console.WriteLine</c> diagnostics into counters, so the corpse window and
/// pickup decisions are written to a file the operator can read inside the
/// container. Diagnostics only: this never changes what is sent.
/// </summary>
internal static class MonsterLootTrace
{
    private const string Path = "/tmp/monster-loot.log";

    public static void Log(string message)
    {
        try
        {
            File.AppendAllText(
                Path,
                $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A diagnostic must never take the loot channel down.
        }
    }
}
