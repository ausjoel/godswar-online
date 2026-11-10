namespace Godswar.Server.Game;

/// <summary>
/// Temporary file-backed trace used to locate the Medusa revival defect.
/// Remove once the fix is verified.
/// </summary>
internal static class ReviveTrace
{
    private static readonly object Sync = new();

    internal static void Log(string message)
    {
        try
        {
            lock (Sync)
            {
                File.AppendAllText(
                    "/tmp/revive-trace.log",
                    $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Tracing must never disturb the game loop.
        }
    }
}
