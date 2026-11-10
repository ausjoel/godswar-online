using Godswar.Server.Application.North;

namespace Godswar.Server.Game;

/// <summary>
/// FINALISE2 F-19 + F-24 (the admin 2026-11-05). F-19: a Mycenae boss quest pays 8 Level 3 Crystals into the bag, written by
/// the server (the client's reward slot menu pays one item, never a stack). F-24: the one personal info line that says
/// what a hand-in really paid.
/// </summary>
internal sealed partial class GameClientHandler
{
    /// <summary>
    /// Returns the items granted (empty for every other quest), or null when the bag cannot take them: the caller then
    /// stops the hand-in before anything else is paid, and the quest stays carried.
    /// </summary>
    private async Task<IReadOnlyList<(uint ItemId, int Quantity)>?> TryGrantMycenaeBossQuestItemsAsync(
        uint questId,
        CancellationToken cancellationToken)
    {
        if (!MycenaeBossPolicy.QuestIds.Contains(questId) || _account is null || _character is null)
        {
            return [];
        }

        Godswar.Server.State.CapitalShopPurchaseResult result;
        try
        {
            // unitSilver 0 = a free grant; the store checks capacity and stacks.
            result = await _capitalShopPurchases.GrantGmMallItemAsync(
                _account.Id, _character.Id, Guid.NewGuid(), MycenaeBossPolicy.RewardItemId, 0,
                MycenaeBossPolicy.RewardQuantity, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"[quest] mycenae boss reward failed character={_character.Name} quest={questId} " +
                $"{ex.GetType().Name}: {ex.Message}");
            await SendGainLogAsync("Quest not handed in: the reward could not be given. Try again.", cancellationToken);
            return null;
        }

        if (!result.Purchased || result.Character is null)
        {
            Console.WriteLine(
                $"[quest] mycenae boss reward refused character={_character.Name} quest={questId} status={result.Status}");
            await SendGainLogAsync(
                $"Make room in your bag for {MycenaeBossPolicy.RewardQuantity} {GainItemName(MycenaeBossPolicy.RewardItemId)}, then hand in again.",
                cancellationToken);
            return null;
        }

        InstallCapitalShopProjection(result.Character);
        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        await SendKitBagRefreshAsync(cancellationToken);
        Console.WriteLine(
            $"[quest] mycenae boss reward character={_character.Name} quest={questId} " +
            $"item={MycenaeBossPolicy.RewardItemId} x{MycenaeBossPolicy.RewardQuantity}");
        return [(MycenaeBossPolicy.RewardItemId, MycenaeBossPolicy.RewardQuantity)];
    }

    /// <summary>F-24: "Quest reward: 1,190,000 EXP, 90 TP, 50,000 Silver" plus items and a reached level.</summary>
    private string QuestRewardLine(
        long experience,
        int talentPoints,
        int silver,
        int gold,
        IReadOnlyList<(uint ItemId, int Quantity)>? items,
        int reachedLevel)
    {
        var parts = new List<string>();
        if (experience > 0) parts.Add($"{Amount(experience)} EXP");
        if (talentPoints > 0) parts.Add($"{Amount(talentPoints)} TP");
        if (silver > 0) parts.Add($"{Amount(silver)} Silver");
        if (gold > 0) parts.Add($"{Amount(gold)} Gold");
        if (items is { Count: > 0 }) parts.Add(GainItemList(items));
        var text = "Quest reward: " + (parts.Count == 0 ? "nothing" : string.Join(", ", parts));
        return reachedLevel > 0 ? $"{text} (now level {reachedLevel})" : text;
    }
}
