using Godswar.Server.Application.Progression;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Progression;

internal sealed partial class PostgresWeekendExperienceClaimStore : IWeekendExperienceClaimStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresWeekendExperienceClaimStore(NpgsqlDataSource dataSource) =>
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
}
