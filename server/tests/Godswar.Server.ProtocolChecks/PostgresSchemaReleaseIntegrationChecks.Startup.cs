using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.Infrastructure.WorldContent;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresSchemaReleaseIntegrationChecks
{
    private static async Task InitializeReleaseAsync(
        string connectionString)
    {
        await PostgresSchemaStartup.InitializeAsync(connectionString);
        var migratedViews = await ReadPublicViewDefinitionsAsync(connectionString);
        await PostgresRelationalContentBaselineBootstrapper.EnsureAsync(
            connectionString);
        await AssertPublicViewDefinitionsAsync(connectionString, migratedViews);
        _ = await PostgresItemTemplateContentBootstrapper.LoadAsync(
            connectionString);
        _ = await PostgresWorldContentBootstrapper.LoadAsync(
            connectionString);
    }
}
