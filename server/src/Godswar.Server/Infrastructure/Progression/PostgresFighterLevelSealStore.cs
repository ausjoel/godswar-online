using Godswar.Server.Application.Progression;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Progression;

internal sealed partial class PostgresFighterLevelSealStore : IFighterLevelSealStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresFighterLevelSealStore(NpgsqlDataSource dataSource) =>
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
}
