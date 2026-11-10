using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// What one skill-book grant did.
    /// </summary>
    /// <remarks>
    /// The failures are kept apart because the client script words them
    /// differently: <c>JN300</c> is "you don't have enough Gold to throw into the
    /// wishing pool" while <c>JN400</c> is the full-bag text.
    /// </remarks>
    internal enum WishingPoolGrantOutcome
    {
        Added,
        InsufficientGold,
        InsufficientCapacity,
        Failed
    }

    /// <summary>
    /// Hands one skill book to the character through the Wishing Pool's own grant
    /// path.
    /// </summary>
    /// <remarks>
    /// The class comes from the button the player clicked and the level from the
    /// wish's own draw, so the book matches what was asked for rather than the
    /// character's own class.
    /// <para>
    /// This deliberately does not use the developer item grant: that executor only
    /// accepts materials and GM items from its allowlist, which skill books are not
    /// part of, and the developer channel is scheduled for removal.
    /// </para>
    /// <para>
    /// A non-zero <paramref name="goldCost"/> is charged inside the same store
    /// transaction that inserts the book, against the locked character row, so the
    /// charge and the book can never disagree.
    /// </para>
    /// </remarks>
    private async Task<(
        WishingPoolGrantOutcome Outcome,
        int ItemId,
        string DisplayName,
        int SkillLevel)> TryGrantWishingPoolSkillBookAsync(
            byte characterClass,
            int goldCost,
            CancellationToken cancellationToken)
    {
        if (_account is null ||
            _character is null ||
            _gameplayCatalogs is null)
        {
            return (WishingPoolGrantOutcome.Failed, 0, string.Empty, 0);
        }

        var skillLevel = WishingPoolCatalog.DrawSkillLevel();
        var candidates = WishingPoolCatalog.Resolve(
            _gameplayCatalogs.Content.SkillBooks,
            characterClass,
            skillLevel);
        if (candidates.Count == 0)
        {
            Console.WriteLine(
                $"[wishing-pool] no skill book for class={characterClass} " +
                $"level={skillLevel}");
            return (WishingPoolGrantOutcome.Failed, 0, string.Empty, skillLevel);
        }

        var book = candidates[Random.Shared.Next(candidates.Count)];
        var grant = await _store.AddWishingPoolSkillBookAsync(
            _account.Id,
            _character.Id,
            checked((uint)book.ItemId),
            goldCost,
            cancellationToken);
        if (!grant.Added)
        {
            Console.WriteLine(
                $"[wishing-pool] grant rejected item={book.ItemId} " +
                $"status={grant.Status} goldCost={goldCost} " +
                $"class={characterClass} level={skillLevel}");
            return (
                grant.Status switch
                {
                    KitBagItemGrantStatus.InsufficientGold =>
                        WishingPoolGrantOutcome.InsufficientGold,
                    KitBagItemGrantStatus.InsufficientCapacity =>
                        WishingPoolGrantOutcome.InsufficientCapacity,
                    _ => WishingPoolGrantOutcome.Failed
                },
                book.ItemId,
                book.DisplayName,
                skillLevel);
        }

        var walletChanged = goldCost > 0;
        InstallUpdatedCharacter(grant.Character!);
        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);
        if (walletChanged)
        {
            // A silent balance change leaves the client painting the old amount.
            // This is the same 10166 status refresh the NPC shop sends after a
            // sale moves the wallet.
            await _session.SendAsync(
                BuildLocalPlayerStatusUpdate(),
                cancellationToken,
                "WishingPoolWalletStatus");
        }
        await SendKitBagRefreshAsync(cancellationToken);
        Console.WriteLine(
            $"[wishing-pool] granted item={book.ItemId} " +
            $"name={book.DisplayName} class={characterClass} " +
            $"level={skillLevel} goldCost={goldCost} " +
            $"goldAfter={grant.Character!.Gold}");
        return (
            WishingPoolGrantOutcome.Added,
            book.ItemId,
            book.DisplayName,
            skillLevel);
    }
}
