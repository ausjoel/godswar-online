using Godswar.Server.Application.WorldInstances;
using Godswar.Server.State;

namespace Godswar.Server.Game;

// Pindus Holy Water (IGG acid=123): the faction's Quartermaster hands out one Holy Water at a time; a Fortune Altar
// takes it for 30 points (own faction's altar) or 100 (the hostile one), 40 / 120 when the other side leads by more
// than 30%. Points go to the player (non-kill ranking) and to the faction. Both NPCs are the client's own WarField
// actors driven by NpcFunWar.lua (function 3): the Quartermaster's page is 160 "Claim Holy Water", the altar's 150
// "Turn in Holy Water"; replies 1951/1952/1953 and 1700/1701/2701/2702/1702 are the client's own lines (Index 2).
// The NPCs are not in the published map 38 roster: pindus-npcs-generated.sql places them through gm_npc_spawns at
// GENERATED positions. Gate: battlefield.scoring.
// Each bottle is issued for one round (battlefield_participants.holy_issued / holy_turned_in): a hand-in scores only
// against a bottle issued in the current round, so bottles kept from earlier rounds (bag or warehouse) cannot be dumped.
// NOT built: the EXP and the +3,000 HP / +300 MP effect a hand-in also gives (the guide gives no amount), the
// Priest's Holy Assistance / Battlefield Buff (361/362), and removing Holy Water outside the battlefield.
internal sealed partial class GameClientHandler
{
    private const int HolyWaterClaimEntry = 160;
    private const int HolyWaterTurnInEntry = 150;
    private const int NotTheTime = 473;          // "Now is not the specified time!"
    private const int HolyWaterBagFull = 1951;
    private const int HolyWaterObtained = 1952;
    private const int HolyWaterAlreadyHeld = 1953;
    private const int HolyWaterNone = 1702;

    /// <summary>The Quartermaster's faction (0 Sparta, 1 Athens) or null.</summary>
    private static byte? PindusQuartermasterFaction(string npcKey) => npcKey switch
    {
        "WarField_002" => 0,
        "WarField_007" => 1,
        _ => null
    };

    /// <summary>The faction whose Fortune Altar this is, or null.</summary>
    private static byte? PindusAltarFaction(string npcKey) => npcKey switch
    {
        "WarField_005" or "WarField_016" or "WarField_017" or "WarField_018" or "WarField_019" or "WarField_020" => 0,
        "WarField_010" or "WarField_011" or "WarField_012" or "WarField_013" or "WarField_014" or "WarField_015" => 1,
        _ => null
    };

    private async Task<bool> TryHandlePindusSupplyActionAsync(
        NpcSpawnDefinition npc, int dialogIndex, int[] path, CancellationToken cancellationToken)
    {
        if (dialogIndex != BattlefieldAwarderReplies.FunctionNumber || path.Length != 1 ||
            npc.MapId != BattlefieldRewardPolicy.PindusMapId || _character is not { } character ||
            BattlefieldStore is not { } store)
        {
            return false;
        }

        int reply;
        if (path[0] == HolyWaterClaimEntry && PindusQuartermasterFaction(npc.NpcKey) is { } supplier)
        {
            reply = await ClaimHolyWaterAsync(store, character, supplier, cancellationToken);
        }
        else if (path[0] == HolyWaterTurnInEntry && PindusAltarFaction(npc.NpcKey) is { } altar)
        {
            reply = await TurnInHolyWaterAsync(store, character, altar, cancellationToken);
        }
        else
        {
            return false;
        }

        await SendScriptedNpcDialogueReplyAsync(
            npc, PindusSupplyDialogue(npc.NpcKey), dialogIndex, path[0], [reply], cancellationToken);
        Console.WriteLine(
            $"[battlefield] holy water character={character.Name} npc={npc.NpcKey} entry={path[0]} reply={reply}");
        return true;
    }

    private async Task<int> ClaimHolyWaterAsync(
        IBattlefieldRoundStore store, GameCharacter character, byte supplierFaction, CancellationToken cancellationToken)
    {
        // Only the player's own faction's Quartermaster serves them (IGG: "the Priest of their own faction").
        if (!BattlefieldScoringEnabled || character.Camp != supplierFaction ||
            !BattlefieldRewardPolicy.PindusLevelEligible(character.Level) || // BATTLEFIELD2: band
            BattlefieldRewardPolicy.CurrentRound(BattlefieldRewardPolicy.Pindus, _realmCalendar, DateTimeOffset.UtcNow)
                is not { } round)
        {
            return NotTheTime;
        }

        // The bottle is issued for this round (holy_issued); only bottles issued in the round can be handed in, so
        // stockpiling across rounds or in the warehouse earns nothing.
        var change = await store.GrantHolyWaterAsync(BattlefieldRewardPolicy.Pindus, round.Start, round.End,
            character.Id, character.Camp, BattlefieldRewardPolicy.HolyWaterItemId, cancellationToken);
        switch (change.Outcome)
        {
            case HolyWaterOutcome.AlreadyHolding:
                return HolyWaterAlreadyHeld;
            case HolyWaterOutcome.RoundClosed:
                return NotTheTime;
            case HolyWaterOutcome.Done when change.Character is { } refreshed:
                InstallUpdatedCharacter(refreshed);
                await SendKitBagRefreshAsync(cancellationToken);
                return HolyWaterObtained;
            default:
                return HolyWaterBagFull;
        }
    }

    private async Task<int> TurnInHolyWaterAsync(
        IBattlefieldRoundStore store, GameCharacter character, byte altarFaction, CancellationToken cancellationToken)
    {
        if (!BattlefieldScoringEnabled || character.Camp > 1 ||
            BattlefieldRewardPolicy.CurrentRound(BattlefieldRewardPolicy.Pindus, _realmCalendar, DateTimeOffset.UtcNow)
                is not { } round)
        {
            return NotTheTime;
        }

        // One store transaction: the bottle leaves the bag and the points are recorded together, or neither happens.
        var change = await store.TurnInHolyWaterAsync(BattlefieldRewardPolicy.Pindus, round.Start, round.End,
            character.Id, character.Camp, hostileAltar: altarFaction != character.Camp,
            BattlefieldRewardPolicy.HolyWaterItemId, cancellationToken);
        if (change.Outcome == HolyWaterOutcome.RoundClosed)
        {
            return NotTheTime;
        }
        if (change.Outcome != HolyWaterOutcome.Done || change.Character is not { } refreshed)
        {
            // None in the bag, or only a bottle that was not issued in this round (it scores nothing).
            return HolyWaterNone;
        }
        InstallUpdatedCharacter(refreshed);
        await SendKitBagRefreshAsync(cancellationToken);

        // The client's own lines: 1701 (+30), 1700 (+100), 2701 (+40), 2702 (+120).
        return change.Points switch
        {
            30 => 1701,
            100 => 1700,
            40 => 2701,
            _ => 2702
        };
    }

    /// <summary>The Quartermaster's page: 160 "Claim Holy Water" (Index 1 draws it with the NF_HPMP_1001 text).</summary>
    private static readonly ScriptedNpcDialogue PindusQuartermasterDialogue = new(
        FunctionNumber: 3,
        OpeningMenu: [HolyWaterClaimEntry],
        Steps: new Dictionary<int, int[]>());

    /// <summary>A Fortune Altar's page: 150 "Turn in Holy Water".</summary>
    private static readonly ScriptedNpcDialogue PindusFortuneAltarDialogue = new(
        FunctionNumber: 3,
        OpeningMenu: [HolyWaterTurnInEntry],
        Steps: new Dictionary<int, int[]>());

    private static ScriptedNpcDialogue PindusSupplyDialogue(string npcKey) =>
        PindusQuartermasterFaction(npcKey) is not null ? PindusQuartermasterDialogue : PindusFortuneAltarDialogue;
}
