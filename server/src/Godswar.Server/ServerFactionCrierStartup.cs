using Godswar.Server.Application.FactionCrier;
using Godswar.Server.Infrastructure.FactionCrier;

namespace Godswar.Server;

internal static class ServerFactionCrierStartup
{
    public static Task<FactionCrierBalanceSnapshot> LoadBalanceAsync(
        ServerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return PostgresFactionCrierBalanceSnapshotReader.LoadAsync(
            options.Storage.PostgresConnectionString,
            cancellationToken);
    }
}
