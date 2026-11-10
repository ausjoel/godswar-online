using System.Collections.Concurrent;

namespace Godswar.Server.Application.Operators;

// ROUND2: state and persistence hooks for the operator (GM) chat commands
// (GodsWar-2.51/Polish patch). Only accounts in the developer-command
// allowlist (DeveloperCommandOptions) can use them; the game layer checks that
// before anything here is touched.
//
// The "powers" are in memory only and belong to the operator's own character:
// they end when the server restarts. Mutes and bans are stored in the
// database (character_base.mutetime, accounts.status) so they survive both a
// relog and a restart.

/// <summary>A GM's own toggles. Default = no effect.</summary>
internal sealed record OperatorPowers(
    bool Invulnerable = false,
    float SpeedFactor = 1f,
    int AttackMultiplier = 1,
    int DefenseBonus = 0,
    bool BuffLock = false,
    bool PetCareLock = false)
{
    public static OperatorPowers None { get; } = new();

    public bool IsNone => this == None;
}

/// <summary>One online or offline character, resolved by name.</summary>
internal sealed record OperatorCharacterRef(
    int AccountId,
    string Username,
    short AccountStatus,
    int CharacterId,
    string CharacterName,
    int MuteUntilUnixSeconds);

/// <summary>One kit-bag row, as the operator sees it.</summary>
internal sealed record OperatorBagItem(
    long RowId,
    short Slot,
    int ItemId,
    short Quality,
    short Grade,
    short Bound,
    short Stack,
    int ItemExperience,
    long? SealedPetId);

internal enum OperatorItemOutcome
{
    Done = 1,
    CharacterNotFound,
    NoFreeSlot,
    SlotEmpty,
    InvalidValue,
    Refused
}

internal sealed record OperatorItemResult(
    OperatorItemOutcome Outcome,
    string Detail,
    int SlotsWritten = 0);

/// <summary>The fields <c>/itemset</c> can change on a bag item.</summary>
internal enum OperatorItemField
{
    Quality = 1,
    Grade,
    Bound,
    Stack,
    ItemExperience
}

internal interface IOperatorStore
{
    Task<bool> ReadCharacterGmAsync(int accountId, int characterId, CancellationToken cancellationToken) => Task.FromResult(false);

    Task<OperatorCharacterRef?> FindCharacterAsync(
        string characterName,
        CancellationToken cancellationToken);

    Task<short> ReadAccountStatusAsync(
        int accountId,
        CancellationToken cancellationToken);

    Task<bool> SetAccountStatusAsync(
        int accountId,
        short status,
        CancellationToken cancellationToken);

    Task<bool> SetMuteAsync(
        int characterId,
        int untilUnixSeconds,
        CancellationToken cancellationToken);

    Task<bool> MoveOfflineCharacterAsync(
        int characterId,
        byte mapId,
        float x,
        float z,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OperatorBagItem>> ReadBagAsync(
        int characterId,
        CancellationToken cancellationToken);

    Task<OperatorItemResult> GrantItemAsync(
        int characterId,
        uint itemId,
        int quantity,
        short stackCap,
        short bound,
        CancellationToken cancellationToken,
        short quality = 1,
        short grade = 1);

    Task<OperatorItemResult> RemoveBagItemAsync(
        int characterId,
        short slot,
        CancellationToken cancellationToken);

    /// <summary>
    /// PROF (q09): takes the materials out of the bag and adds the product, in one transaction. When anything is
    /// missing or there is no room, nothing changes. A failed odds roll passes productCount 0 (materials kept).
    /// </summary>
    Task<OperatorItemResult> CraftAsync(
        int characterId,
        IReadOnlyList<(uint ItemId, int Count)> materials,
        uint productId,
        int productCount,
        short productStackCap,
        short bound,
        CancellationToken cancellationToken) =>
        Task.FromResult(new OperatorItemResult(OperatorItemOutcome.Refused, "this store cannot craft"));

    Task<OperatorItemResult> MoveBagItemAsync(
        int characterId,
        short fromSlot,
        short toSlot,
        CancellationToken cancellationToken);

    /// <summary>Null fields are left unchanged.</summary>
    Task<OperatorItemResult> SetProgressionAsync(
        int characterId,
        short? level,
        long? experience,
        int? talentPoints,
        int? zodiacEnergy,
        CancellationToken cancellationToken);

    /// <summary>Null fields are left unchanged.</summary>
    Task<OperatorItemResult> SetWalletAsync(
        int characterId,
        int? silver,
        int? gold,
        int? bindGold,
        CancellationToken cancellationToken);

    // ROUND-QUESTRESET: complete the camp's main line through a level, or forget completed quests.
    // expected = the quest ids the caller previewed; a different plan is refused, never applied.
    Task<OperatorItemResult> ApplyQuestChainAsync(
        int characterId,
        Godswar.Server.Domain.World.Content.QuestChainOperation operation,
        int level,
        Godswar.Server.Domain.World.Content.QuestChainScope scope,
        IReadOnlyList<uint> expected,
        CancellationToken cancellationToken);

    Task<OperatorItemResult> SetBagItemFieldAsync(
        int characterId,
        short slot,
        OperatorItemField field,
        int value,
        CancellationToken cancellationToken);

    Task<OperatorItemResult> DuplicateBagItemAsync(
        int characterId,
        short slot,
        CancellationToken cancellationToken);

    Task LogAsync(
        int actorAccountId,
        string actorCharacter,
        string command,
        string target,
        string detail,
        CancellationToken cancellationToken);
}

internal static class OperatorControls
{
    private static readonly ConcurrentDictionary<int, OperatorPowers> PowersByCharacter = new();

    private static readonly ConcurrentDictionary<int, DateTimeOffset> MutedUntilByCharacter = new();

    /// <summary>Set at start-up by the PostgreSQL runtime; null = unavailable.</summary>
    public static IOperatorStore? Store { get; set; }

    public static bool UsesCharacterGmFlags { get; set; }

    public static void ClearPowers(int characterId) => PowersByCharacter.TryRemove(characterId, out _);

    public static OperatorPowers PowersOf(int characterId) =>
        PowersByCharacter.TryGetValue(characterId, out var powers)
            ? powers
            : OperatorPowers.None;

    public static OperatorPowers UpdatePowers(
        int characterId,
        Func<OperatorPowers, OperatorPowers> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var updated = PowersByCharacter.AddOrUpdate(
            characterId,
            _ => update(OperatorPowers.None),
            (_, current) => update(current));
        if (updated.IsNone)
        {
            PowersByCharacter.TryRemove(characterId, out _);
        }

        return updated;
    }

    public static bool IsInvulnerable(int characterId) =>
        PowersByCharacter.TryGetValue(characterId, out var powers) &&
        powers.Invulnerable;

    public static bool HasBuffLock(int characterId) =>
        PowersByCharacter.TryGetValue(characterId, out var powers) &&
        powers.BuffLock;

    public static bool HasPetCareLock(int characterId) =>
        PowersByCharacter.TryGetValue(characterId, out var powers) &&
        powers.PetCareLock;

    /// <summary>The in-memory copy of character_base.mutetime.</summary>
    public static void SetMutedUntil(int characterId, DateTimeOffset? until)
    {
        if (until is { } value && value > DateTimeOffset.UtcNow)
        {
            MutedUntilByCharacter[characterId] = value;
        }
        else
        {
            MutedUntilByCharacter.TryRemove(characterId, out _);
        }
    }

    public static bool TryGetMutedUntil(
        int characterId,
        DateTimeOffset now,
        out DateTimeOffset until)
    {
        if (MutedUntilByCharacter.TryGetValue(characterId, out until) &&
            until > now)
        {
            return true;
        }

        MutedUntilByCharacter.TryRemove(characterId, out _);
        return false;
    }

    /// <summary>accounts.status values: 0 = normal; anything else blocks login.</summary>
    public const short AccountStatusNormal = 0;

    public const short AccountStatusBanned = 1;

    /// <summary>
    /// True when the account may not log in. A missing store or a failed read
    /// lets the login through (the stock behaviour), and logs the failure.
    /// </summary>
    public static async Task<bool> IsAccountBlockedAsync(
        int accountId,
        CancellationToken cancellationToken)
    {
        var store = Store;
        if (store is null)
        {
            return false;
        }

        try
        {
            return await store.ReadAccountStatusAsync(
                accountId,
                cancellationToken) != AccountStatusNormal;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[operator] ban check failed account={accountId}; login allowed: " +
                error.Message);
            return false;
        }
    }
}
