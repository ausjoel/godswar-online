using Godswar.Server.Application.Progression;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Rewards;

internal sealed partial class PostgresMonsterRewardExtrasStore : IMonsterRewardExtrasStore
{
    private readonly NpgsqlDataSource _dataSource;
    private const short ItemLocationKitBag = 1;
    private const int KitBagProjectionSlots = 96;
    private readonly Func<int, CancellationToken, Task<GameCharacter?>> _readCharacter;

    public PostgresMonsterRewardExtrasStore(NpgsqlDataSource dataSource,
        Func<int, CancellationToken, Task<GameCharacter?>> readCharacter)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _readCharacter = readCharacter ?? throw new ArgumentNullException(nameof(readCharacter));
    }

    private Task<GameCharacter?> GetCharacterByIdAsync(int characterId, CancellationToken cancellationToken) =>
        _readCharacter(characterId, cancellationToken);
}
