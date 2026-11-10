using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.State;

internal readonly record struct CapitalShopOffer(
    CompactItemEntry Item,
    int UnitPrice,
    CapitalNpcShopCurrency Currency)
{
    public bool IsValid =>
        !Item.IsEmpty &&
        Item.Quality > 0 &&
        Item.Grade > 0 &&
        Item.Bound is >= 0 and <= 1 &&
        Item.Stack == 1 &&
        UnitPrice > 0 &&
        Enum.IsDefined(Currency);
}

internal enum CapitalShopPurchaseStatus
{
    Purchased,
    CharacterNotFound,
    InsufficientCurrency,
    InsufficientCapacity,
    UnsupportedItem
}

internal sealed record CapitalShopPurchaseResult(
    CapitalShopPurchaseStatus Status,
    GameCharacter? Character,
    int CurrencyBalance)
{
    public bool Purchased => Status == CapitalShopPurchaseStatus.Purchased;
}

internal enum CapitalShopSaleStatus
{
    Sold,
    CharacterNotFound,
    EmptySlot,
    UnsupportedItem
}

/// <summary>
/// Outcome of selling one whole addressed kit bag stack to a shop. The client's
/// sell request carries no quantity, so a sale always covers the entire stack.
/// </summary>
internal sealed record CapitalShopSaleResult(
    CapitalShopSaleStatus Status,
    GameCharacter? Character,
    int ItemId,
    int Quantity,
    int UnitPrice,
    int Earned,
    int SilverBalance)
{
    public bool Sold => Status == CapitalShopSaleStatus.Sold;

    public static CapitalShopSaleResult Rejected(CapitalShopSaleStatus status) =>
        new(status, Character: null, 0, 0, 0, 0, 0);
}
