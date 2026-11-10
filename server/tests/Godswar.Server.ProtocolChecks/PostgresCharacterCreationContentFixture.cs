using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.WorldContent;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresCharacterCreationContentFixture
{
    // Character creation reads starter skills from the immutable gameplay
    // publication. Item/pet seeding alone does not prepare that startup input.
    public static async Task EnsureGameplayPublishedAsync(string connectionString)
    {
        await PostgresRelationalContentBaselineBootstrapper.EnsureAsync(connectionString);
        await PostgresGameplayContentPublisher.EnsurePublishedAsync(connectionString);
    }
}
