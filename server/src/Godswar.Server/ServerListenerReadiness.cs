using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure.Udp;
using Godswar.Server.Operations;

namespace Godswar.Server;

internal static class ServerListenerReadiness
{
    public static async Task WaitAsync(
        IReadOnlyList<TcpEndpointServer> endpoints,
        ManagementHttpServer? management,
        SecureUdpRuntime? secureUdp,
        ServerReadinessMonitor readiness,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(10);
        await Task.WhenAll(endpoints.Select(server =>
            server.WaitUntilStartedAsync(cancellationToken)))
            .WaitAsync(timeout, cancellationToken);
        if (management is not null)
        {
            await management.WaitUntilStartedAsync(cancellationToken)
                .WaitAsync(timeout, cancellationToken);
        }
        if (secureUdp is not null)
        {
            await secureUdp.WaitUntilReadyAsync(cancellationToken)
                .WaitAsync(timeout, cancellationToken);
        }
        await readiness.WaitUntilFirstRefreshAsync(cancellationToken)
            .WaitAsync(timeout, cancellationToken);
    }
}
