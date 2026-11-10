using Godswar.Server.Application.Pets;
using Godswar.Server.Application.FactionCrier;
using Godswar.Server.Application.OnlineAwards;
using Godswar.Server.Infrastructure.OnlineAwards;
using Godswar.Server.State;

namespace Godswar.Server;

internal static class ServerDailyNpcStartup
{
    public static async Task<ServerDailyNpcBalances> LoadBalancesAsync(
        ServerOptions options,
        GameplayItemContent itemContent,
        IPetContentCatalog petContent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(itemContent);
        var faction = ServerFactionCrierStartup.LoadBalanceAsync(
            options,
            cancellationToken);
        var online = PostgresOnlineAwardBalanceSnapshotReader.LoadAsync(
            options.Storage.PostgresConnectionString,
            itemContent.Templates,
            petContent,
            cancellationToken);
        await Task.WhenAll(faction, online);
        return new(await faction, await online);
    }
}

internal sealed record ServerDailyNpcBalances(
    FactionCrierBalanceSnapshot FactionCrier,
    OnlineAwardBalanceSnapshot OnlineAward);
