using Godswar.Server.Application.Progression;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresCapitalShopPurchaseStore : ICapitalShopPurchaseStore
{
    private readonly NpgsqlDataSource _dataSource;
    private const short ItemLocationKitBag = 1;
    private const int KitBagProjectionSlots = 96;
    private readonly Func<int, CancellationToken, Task<GameCharacter?>> _readCharacter;

    public PostgresCapitalShopPurchaseStore(NpgsqlDataSource dataSource,
        Func<int, CancellationToken, Task<GameCharacter?>> readCharacter)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _readCharacter = readCharacter ?? throw new ArgumentNullException(nameof(readCharacter));
    }

    private Task<GameCharacter?> GetCharacterByIdAsync(int characterId, CancellationToken cancellationToken) =>
        _readCharacter(characterId, cancellationToken);
}
