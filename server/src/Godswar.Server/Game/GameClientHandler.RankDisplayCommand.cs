using Godswar.Server.Infrastructure.Appearance;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// RANK-DISPLAY player command <c>/rank</c>: choose the highest armour / weapon rank effect shown on this
    /// character (cosmetic only; stats and the earned rank are unchanged). Any recognised line is consumed here and
    /// never reaches map chat. Needs the rank-display client stub to change what is drawn.
    /// </summary>
    private async Task<bool> HandlePlayerRankDisplayCommandAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (!TryReadTalkText(packet.Payload, out var text))
        {
            return false;
        }

        var offset = DeveloperCommandText.FindBodyOffset(text);
        if (offset < 0 ||
            !RankDisplayCommand.TryParse(text[offset..], out var target, out var cap, out var error))
        {
            return false;
        }

        if (_account is null || _character is null)
        {
            return true;
        }

        if (error is not null)
        {
            await SendDeveloperItemFeedbackAsync(packet, $"[rank] {error}", cancellationToken);
            return true;
        }

        if (!RankDisplayStore.Enabled)
        {
            await SendDeveloperItemFeedbackAsync(packet, "[rank] Choosing a rank effect is switched off on this server.", cancellationToken);
            return true;
        }

        var (armour, weapon) = RankDisplayStore.Get(_character.Id);
        if (target != RankDisplayTarget.Show)
        {
            if (target is RankDisplayTarget.Armour or RankDisplayTarget.Both) armour = cap;
            if (target is RankDisplayTarget.Weapon or RankDisplayTarget.Both) weapon = cap;
            try
            {
                await RankDisplayStore.SetAsync(_character.Id, armour, weapon, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[rank-display] save failed character={_character.Id}: {exception.Message}");
                await SendDeveloperItemFeedbackAsync(packet, "[rank] Could not save that right now; nothing changed.", cancellationToken);
                return true;
            }

            Console.WriteLine($"[rank-display] character={_character.Id} armour_cap={armour} weapon_cap={weapon}");
            var objectId = CurrentPlayerObjectId;
            var title = PacketBuilder.PlayerTitleInfo(_character, objectId);
            await _session.SendAsync(title, cancellationToken, "RankDisplaySelf");
            await _registry.BroadcastToMapAsync(_character.CurrentMap, title, cancellationToken, _session, "RankDisplayRefresh");
        }

        await SendDeveloperItemFeedbackAsync(
            packet,
            $"[rank] Armour: earned {_character.ArmorRank}, showing {Shown(armour, _character.ArmorRank)}. " +
            $"Weapon: earned {_character.WeaponRank}, showing {Shown(weapon, _character.WeaponRank)}. " +
            "Change with /rank armour <n>, /rank weapon <n> or /rank max.",
            cancellationToken);
        return true;
    }

    private static string Shown(byte cap, int earned) =>
        cap == RankDisplayStore.NoCap || cap >= earned
            ? $"{earned} (all)"
            : cap == 0 ? "none" : cap.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
