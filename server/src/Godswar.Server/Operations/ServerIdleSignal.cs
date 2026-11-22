namespace Godswar.Server.Operations;

/// <summary>
/// W5A: a process-wide "nobody is connected" hint for background polls that
/// may slow down while the server is empty.
/// </summary>
/// <remarks>
/// It is only a hint. Until the host binds a probe it reports "not idle", so
/// tools and checks that build workers without a listener keep their normal
/// timing. A probe that throws is treated as "not idle" for the same reason.
/// </remarks>
internal static class ServerIdleSignal
{
    private static Func<bool>? _noClientsConnected;

    public static void Bind(Func<bool> noClientsConnected)
    {
        ArgumentNullException.ThrowIfNull(noClientsConnected);
        Volatile.Write(ref _noClientsConnected, noClientsConnected);
    }

    public static void Unbind() =>
        Volatile.Write(ref _noClientsConnected, null);

    public static bool NoClientsConnected
    {
        get
        {
            var probe = Volatile.Read(ref _noClientsConnected);
            if (probe is null)
            {
                return false;
            }

            try
            {
                return probe();
            }
            catch
            {
                return false;
            }
        }
    }
}
