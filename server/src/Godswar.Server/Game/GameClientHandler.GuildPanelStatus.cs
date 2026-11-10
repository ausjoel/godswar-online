using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// Keeps the character panel's two guild rows in step with the character's own
/// <c>guild_duty</c> and <c>consortia_contribute</c>.
/// </summary>
/// <remarks>
/// The panel reads both out of the GameData block <c>S2C 10166</c> carries (wire
/// offsets 80 and 84), and the packet builder fills them from
/// <see cref="GuildPanelStatusCache"/>. Because the cache is keyed by character
/// id, every other 10166 - login, combat, forging, a mount change - carries the
/// pair without knowing the guild exists. What this file adds is the refresh
/// itself: the two numbers are re-read at the moments they can move (entering the
/// world, opening or being pushed the guild window, a duty change, a donation, an
/// offering, leaving the guild).
/// </remarks>
internal sealed partial class GameClientHandler
{
    /// <summary>
    /// One roster line per member with the state the client is being told, so a
    /// wrong online flag can be read off the log instead of guessed from the
    /// window.
    /// </summary>
    private static string FormatRoster(
        IReadOnlyList<Godswar.Server.Application.Guilds.GuildMemberSnapshot>
            members) =>
        string.Join(
            ',',
            members.Select(static member =>
                $"{member.Name}:{(member.Online ? "on" : "off")}"));

    /// <summary>
    /// Re-reads one character's guild panel status into the cache.
    /// </summary>
    /// <returns>Whether it differs from what was cached.</returns>
    private async Task<bool> RefreshGuildPanelStatusAsync(
        int characterId,
        CancellationToken cancellationToken)
    {
        if (_guilds is null)
        {
            Console.WriteLine(
                $"[guild] panel status unavailable character={characterId} " +
                "reason=no-guild-store");
            return false;
        }

        var status = await _guilds.TryReadPanelStatusAsync(
            characterId,
            cancellationToken);
        if (status is not { } current)
        {
            // No such character row: leave what is cached alone rather than
            // blanking a panel over an id that resolved to nothing.
            Console.WriteLine(
                $"[guild] panel status read character={characterId} row=missing " +
                $"kept={GuildPanelStatusCache.For(characterId)}");
            return false;
        }

        var changed = GuildPanelStatusCache.Set(characterId, current);
        Console.WriteLine(
            $"[guild] panel status character={characterId} " +
            $"duty={current.Duty} contribution={current.Contribution} " +
            $"changed={changed}");
        return changed;
    }

    /// <summary>
    /// Re-reads a character's guild panel status and, when it moved, tells that
    /// character's own client to repaint its status block.
    /// </summary>
    /// <remarks>
    /// A member whose duty was changed by somebody else is not this handler's own
    /// character, so the refresh is addressed to their session through the
    /// registry - the same call the pet and owner-stat refreshes use. An offline
    /// member is only refreshed in the cache: their next status frame, starting
    /// with the login one, carries it.
    /// </remarks>
    private async Task PushGuildPanelStatusAsync(
        int characterId,
        CancellationToken cancellationToken)
    {
        if (!await RefreshGuildPanelStatusAsync(characterId, cancellationToken))
        {
            return;
        }

        // Read back from the cache rather than from the row: the frame the client
        // is about to receive is built from exactly this pair (wire 80/84), so the
        // log and the packet cannot disagree.
        var status = GuildPanelStatusCache.For(characterId);
        if (characterId == _character?.Id)
        {
            await _session.SendAsync(
                BuildLocalPlayerStatusUpdate(),
                cancellationToken,
                "GuildPanelStatus");
            Console.WriteLine(
                $"[guild] panel status pushed character={characterId} " +
                $"target=self duty={status.Duty} " +
                $"contribution={status.Contribution}");
            return;
        }

        if (!_registry.TryGetCharacterSession(characterId, out var session))
        {
            Console.WriteLine(
                $"[guild] panel status cached character={characterId} " +
                $"target=offline duty={status.Duty} " +
                $"contribution={status.Contribution}");
            return;
        }

        var snapshot = await _registry.SendStatusSnapshotToSelfAsync(
            session,
            DateTimeOffset.UtcNow,
            cancellationToken,
            "GuildPanelStatus");
        if (snapshot is null)
        {
            Console.WriteLine(
                $"[guild] panel status not published character={characterId}");
            return;
        }

        Console.WriteLine(
            $"[guild] panel status pushed character={characterId} " +
            $"target=member duty={status.Duty} " +
            $"contribution={status.Contribution}");
    }

    /// <summary>
    /// Repaints every place a guild change to one character is visible: the
    /// character's own panel and status block, and the frame that told their map
    /// who they are.
    /// </summary>
    /// <remarks>
    /// The guild name, the in-guild flag and the duty live on the player's own
    /// object (<c>10199</c>: body+4 guild name, +0x44 in-guild flag, +0x46 duty),
    /// which the other clients receive when the player becomes visible. A duty
    /// change, a removal or a leave has to repeat that frame for their map, or the
    /// nameplates and inspect windows keep drawing the guild as it was when the
    /// player last appeared - measured 2026-10-02: the promoted member's own
    /// client showed <c>567-内线</c> while the guild master's client still showed
    /// <c>567-成员</c>.
    /// </remarks>
    private async Task RefreshCharacterGuildPresenceAsync(
        int characterId,
        CancellationToken cancellationToken)
    {
        await PushGuildPanelStatusAsync(characterId, cancellationToken);

        if (!_registry.TryGetCharacterContext(characterId, out var context))
        {
            Console.WriteLine(
                $"[guild] presence character={characterId} target=offline");
            return;
        }

        var recipients = await _registry.BroadcastToMapAsync(
            context.MapId,
            PacketBuilder.PlayerTitleInfo(
                context.Character,
                context.ObjectId),
            cancellationToken,
            excludeSession: null,
            label: "GuildPresence");
        Console.WriteLine(
            $"[guild] presence character={context.Character.Name} " +
            $"object={context.ObjectId} map={context.MapId} " +
            $"recipients={recipients}");
    }
}
