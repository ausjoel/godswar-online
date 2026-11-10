using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    // Function numbers from NpcFun.lua: NPC_FLAG_SYS_DENATURATION and NPC_FLAG_SYS_MIGRATION.
    private const int GenderChangeFunction = 105;
    private const int FactionChangeFunction = 112;

    // Page-one buttons: 105 is the only surgeon button; the faction changer draws 105
    // (red, "to Sparta") or 106 (blue, "to Athens") and the client text shows the
    // price as "x Gold", so the price is the server's to set.
    private const int ChangeButtonRed = 105;
    private const int ChangeButtonBlue = 106;

    // Page-two lines, as numbered in NpcDenaturation.lua / NpcFunMigration.lua.
    private const int GenderNotEnoughGold = 101;
    private const int GenderFashionWorn = 103;
    private const int FactionNotEnoughGold = 107;
    private const int FactionCoolingDown = 108;
    private const int FactionInGuild = 109;

    // The client's own text says 13820 Gold for the gender change. Faction has no number in the
    // client; the admin set it to the same 13820 Gold (decision r01, 2026-11-03). All three are overridable.
    private static readonly int GenderChangePrice = ReadIdentityEnv("GODSWAR_GENDER_CHANGE_GOLD", 13820);
    private static readonly int FactionChangePrice = ReadIdentityEnv("GODSWAR_FACTION_CHANGE_GOLD", 13820);
    private static readonly TimeSpan FactionChangeCooldown =
        TimeSpan.FromDays(ReadIdentityEnv("GODSWAR_FACTION_CHANGE_COOLDOWN_DAYS", 30));

    private bool _identityChangeCommitted;

    private static int ReadIdentityEnv(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value >= 0
            ? value
            : fallback;

    /// <summary>
    /// The plastic surgeon and the faction changer act on the click instead of
    /// answering a menu. Returns true when the click was theirs.
    /// </summary>
    private async Task<bool> TryHandleIdentityChangeAsync(
        NpcSpawnDefinition npc,
        ScriptedNpcDialogue dialogue,
        int dialogIndex,
        int selection,
        CancellationToken cancellationToken)
    {
        var function = dialogue.FunctionNumber;
        if (function is not (GenderChangeFunction or FactionChangeFunction) ||
            _character is null ||
            _account is null)
        {
            return false;
        }

        if (selection < 0)
        {
            // The first page. The faction changer offers the side the character is not on:
            // the heading, then the red button to go to Sparta or the blue one to go to Athens.
            if (function == FactionChangeFunction)
            {
                await SendScriptedNpcDialogueReplyAsync(
                    npc,
                    dialogue,
                    dialogIndex,
                    selection,
                    [104, _character.Camp == GameDefaults.SpartaCamp ? ChangeButtonBlue : ChangeButtonRed],
                    cancellationToken);
                return true;
            }

            if (function == GenderChangeFunction)
            {
                await SendScriptedNpcDialogueReplyAsync(
                    npc, dialogue, dialogIndex, selection, [104, 105], cancellationToken);
                return true;
            }
        }

        var isConfirm = function == GenderChangeFunction
            ? selection == 105
            : selection is ChangeButtonRed or ChangeButtonBlue;
        if (!isConfirm)
        {
            return false;
        }

        if (_identityChangeCommitted)
        {
            // The change is already in; the connection is being closed.
            return true;
        }

        var store = CharacterIdentityControls.Store;
        if (store is null || !TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            Console.WriteLine($"[identity] unavailable npc={npc.InteractionId} function={function}");
            return true;
        }

        var subject = new CommandSubject(_account.Id, _character.Id);
        CharacterIdentityChangeResult result;
        try
        {
            result = function == GenderChangeFunction
                ? await store.ChangeGenderAsync(
                    subject, ownership, _character.Gender, GenderChangePrice, cancellationToken)
                : await store.ChangeFactionAsync(
                    subject, ownership, _character.Camp, FactionChangePrice,
                    FactionChangeCooldown, cancellationToken);
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return true;
        }

        Console.WriteLine(
            $"[identity] function={function} character={_character.Name} status={result.Status} " +
            $"gold={result.Stone} remaining={result.RemainingSeconds}");
        if (result.Status != CharacterIdentityChangeStatus.Done)
        {
            int[]? reply = (function, result.Status) switch
            {
                (GenderChangeFunction, CharacterIdentityChangeStatus.NotEnoughGold) => [GenderNotEnoughGold],
                (GenderChangeFunction, CharacterIdentityChangeStatus.FashionWorn) => [GenderFashionWorn],
                (FactionChangeFunction, CharacterIdentityChangeStatus.NotEnoughGold) => [FactionNotEnoughGold],
                (FactionChangeFunction, CharacterIdentityChangeStatus.InGuild) => [FactionInGuild],
                // The countdown line: seconds * 100 + 2, shown as minutes and seconds.
                (FactionChangeFunction, CharacterIdentityChangeStatus.CoolingDown) =>
                    [checked(result.RemainingSeconds * 100 + 2)],
                _ => null
            };
            if (reply is not null)
            {
                await SendScriptedNpcDialogueReplyAsync(
                    npc, dialogue, dialogIndex, selection, reply, cancellationToken);
            }

            return true;
        }

        // The row is committed. Bring the live character in line, so the logout
        // save writes matching values, then drop the connection: the client's
        // own heading promises it, and it re-reads the character at the next login.
        _identityChangeCommitted = true;
        _character.Gender = result.Gender;
        _character.Camp = result.Camp;
        _character.Hair = result.Hair;
        _character.Gold = result.Stone;
        // FINALISE2 (F-83, the admin: "Faction change should TP to other faction main city"): with the hot rate
        // npc.faction_change_teleport = 1 the player is moved to the new capital's start point at once (a full map
        // change, which re-sends the character to the client) instead of being logged out. Any refusal falls back
        // to the old logout, and the next login lands in the new capital as before.
        if (function == FactionChangeFunction &&
            Godswar.Server.Application.Finalise2.Finalise2Rates.On(
                Godswar.Server.Application.Finalise2.Finalise2RateKeys.FactionChangeTeleport))
        {
            _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
            // F-83 follow-up: a character already standing on the new capital's map (the cross-map transition
            // refuses source == target) is re-placed on that map instead of being logged out.
            var outcome = result.Map == _character.CurrentMap
                ? await TryBeginSameMapSceneTransitionAsync(
                    result.PositionX, result.PositionZ, "faction-change", continuationGuard: null, cancellationToken)
                : await TryBeginMapTransitionAsync(
                    result.Map, result.PositionX, result.PositionZ, "faction-change", cancellationToken);
            Console.WriteLine($"[identity] faction change teleport character={_character.Name} " +
                $"camp={result.Camp} map={result.Map} outcome={outcome}");
            if (outcome == SceneTransitionOutcome.CommittedAwaitingReadiness)
            {
                _identityChangeCommitted = false; // the session goes on; another change is refused by the cooldown
                return true;
            }
        }

        // F-83 follow-up (the admin: "Surely we can get gender to work - maybe this just does a teleport to the same
        // location in order to refresh avatars"): with the hot rate npc.gender_change_teleport = 1 the committed
        // character is reloaded and re-sent (new gender and hair in the player detail), then re-placed on the
        // spot with a same-map scene change. Everyone around gets a remove and then the full re-announce, whose
        // equipment visual carries the gender, so they redraw the new model too. Any failure falls back to the
        // old logout below.
        if (function == GenderChangeFunction &&
            Godswar.Server.Application.Finalise2.Finalise2Rates.On(
                Godswar.Server.Application.Finalise2.Finalise2RateKeys.GenderChangeTeleport))
        {
            _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
            var refreshed = await TryRefreshClassChangeInPlaceAsync(cancellationToken);
            var outcome = refreshed && _character is not null
                ? await TryBeginSameMapSceneTransitionAsync(
                    _character.PositionX, _character.PositionZ, "gender-change",
                    continuationGuard: null, cancellationToken)
                : SceneTransitionOutcome.RejectedWithoutRelocation;
            Console.WriteLine($"[identity] gender change re-place character={_character?.Name} " +
                $"gender={result.Gender} hair={result.Hair} refreshed={refreshed} outcome={outcome}");
            if (outcome == SceneTransitionOutcome.CommittedAwaitingReadiness)
            {
                _identityChangeCommitted = false;
                return true;
            }

            if (_character is null)
            {
                _session.Disconnect();
                return true;
            }
        }

        if (function == FactionChangeFunction)
        {
            _character.CurrentMap = result.Map;
            _character.PositionX = result.PositionX;
            _character.PositionZ = result.PositionZ;
            _character.PositionRevision = result.PositionRevision;
        }

        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        _session.Disconnect();
        return true;
    }
}
