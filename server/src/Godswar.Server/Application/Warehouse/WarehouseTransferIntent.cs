namespace Godswar.Server.Application.Warehouse;

/// <summary>Validated warehouse transfer intent, independent of its transport.</summary>
internal readonly record struct WarehouseTransferIntent(
    WarehouseTransferOperation Operation,
    int WarehouseSlot,
    int KitBagSlot,
    int DestinationWarehouseSlot,
    int Money,
    WarehouseStorageType StorageType);
