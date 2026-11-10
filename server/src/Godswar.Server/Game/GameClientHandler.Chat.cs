using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

/// <summary>
/// The chat channels: which one a <c>Talk</c> (10035) frame asked for, who that
/// channel reaches, and the write to each of them.
/// </summary>
/// <remarks>
/// The channel is read from the client's own frame (see
/// <see cref="ChatChannelProtocol"/>), so the chat window's channel selector needs
/// no server-side command and no client change:
/// <list type="bullet">
/// <item>world, faction, guild and party reach their members on whatever map they
/// are, which is the rule the operator set;</item>
/// <item>nearby keeps the behaviour that shipped before channels existed (the
/// sender's own world instance, sender included);</item>
/// <item>a channel word this build does not recognise is treated as nearby, so an
/// unknown channel can never swallow a message or send it further than the
/// sender's map;</item>
/// <item>a private message is addressed by name and delivered to that character
/// alone, wherever he is.</item>
/// </list>
/// The speaker is always the session that sent the frame: the object id the client
/// put in its own frame is never trusted.
/// </remarks>
internal sealed partial class GameClientHandler
{
    private async Task HandleChatAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        // ROUND2: /mute (character_base.mutetime) drops every channel.
        if (await RejectMutedChatAsync(cancellationToken))
        {
            return;
        }

        if (_character is null ||
            !ChatChannelProtocol.TryRead(
                packet.Payload,
                out var channelWord,
                out var text))
        {
            // Not a chat frame this build understands: keep the behaviour that
            // shipped before channels existed rather than dropping the line.
            await BroadcastToCurrentMapAsync(packet, cancellationToken);
            return;
        }

        var audienceWord = channelWord;
        // W1 Chat Bubbles (4110/4111): the speaker's bubble style rides in the top byte of the channel word, where the
        // 2008 server's MSG_TALK kept ucChatBubbleSytle. The audience is resolved from the client's own word.
        var bubble = await CurrentChatBubbleStyleAsync(cancellationToken);
        if (bubble > 0)
        {
            channelWord = (channelWord & 0x00FF_FFFFu) | ((uint)bubble << 24);
            if (packet.Length >= 16)
            {
                var copy = packet.Buffer.ToArray();
                copy[15] = bubble;
                packet = new GamePacket(copy);
            }
        }

        switch (ChatChannelProtocol.ResolveAudience(audienceWord))
        {
            case ChatAudience.World:
                await DeliverChannelChatAsync(
                    ChatAudience.World,
                    channelWord,
                    text,
                    cancellationToken);
                break;
            case ChatAudience.Faction:
                await DeliverChannelChatAsync(
                    ChatAudience.Faction,
                    channelWord,
                    text,
                    cancellationToken);
                break;
            case ChatAudience.Guild:
                await DeliverChannelChatAsync(
                    ChatAudience.Guild,
                    channelWord,
                    text,
                    cancellationToken);
                break;
            case ChatAudience.Party:
                await DeliverChannelChatAsync(
                    ChatAudience.Party,
                    channelWord,
                    text,
                    cancellationToken);
                break;
            case ChatAudience.Private:
                await DeliverPrivateChatAsync(
                    channelWord,
                    text,
                    cancellationToken);
                break;
            default:
                // The native "nearby" line: unchanged, including its own echo to
                // the sender and the client's own frame bytes.
                await BroadcastToCurrentMapAsync(packet, cancellationToken);
                break;
        }
    }

    /// <summary>
    /// One of the four channels that reach a set of characters wherever they are.
    /// </summary>
    private async Task DeliverChannelChatAsync(
        ChatAudience audience,
        uint channelWord,
        string text,
        CancellationToken cancellationToken)
    {
        var character = _character!;
        byte? camp = null;
        IReadOnlyCollection<int>? characterIds = null;
        switch (audience)
        {
            case ChatAudience.Faction:
                camp = _registry.GetOnlineCharacterCamp(_session) ??
                    character.Camp;
                break;
            case ChatAudience.Party:
                var partyMembers = _registry.GetPartyChatCharacterIds(_session);
                if (partyMembers.Count < 2)
                {
                    await _session.SendAsync(
                        PacketBuilder.ServerNote(
                            "You are not in a party."),
                        cancellationToken,
                        "ChatNoParty");
                    return;
                }

                characterIds = partyMembers;
                break;
            case ChatAudience.Guild:
                var guildMembers =
                    await TryResolveGuildMemberIdsAsync(cancellationToken);
                if (guildMembers is null || guildMembers.Count == 0)
                {
                    await _session.SendAsync(
                        PacketBuilder.ServerNote(
                            "You are not in a guild."),
                        cancellationToken,
                        "ChatNoGuild");
                    return;
                }

                characterIds = guildMembers;
                break;
            default:
                break;
        }

        var recipients = _registry.ResolveChatRecipients(
            _session,
            camp,
            characterIds);
        if (recipients.Length == 0)
        {
            return;
        }

        var delivered = await _registry.DeliverChatAsync(
            recipients,
            _ => PacketBuilder.ChatMessage(
                CurrentPlayerObjectId,
                channelWord,
                text),
            "Chat" + audience,
            cancellationToken);
        Console.WriteLine(
            "[chat] channel=" + audience +
            $" character={character.Name} recipients={recipients.Length} " +
            $"delivered={delivered} text='{text}'");
    }

    /// <summary>
    /// The private channel: the client names the recipient inside the text it sends
    /// (<c>&lt;recipient&gt;&lt;speaker&gt;:&lt;message&gt;</c>). That name is
    /// resolved to an online session and the frame is rebuilt the way the stock
    /// client reads an incoming private message.
    /// </summary>
    /// <remarks>
    /// The recipient's client renders a private message as
    /// "&lt;speaker&gt;对你说:&lt;message&gt;", and it takes the message from a fixed
    /// offset in the text: the first <see cref="PrivateBodyPrefixLength"/> UTF-16
    /// characters are the line's own header and are not displayed. The captured
    /// frame carries exactly that - <c>12e1test:nihao</c>, a ten-character header,
    /// then the message - and the operator confirmed the rendered line is
    /// <c>test对你说:nihao</c>. Writing only <c>&lt;speaker&gt;:&lt;message&gt;</c>
    /// therefore lost the message: those twelve characters were all header, and the
    /// recipient saw "test对你说:" with nothing after it.
    /// <para>
    /// The header is therefore built to the same width: the recipient's name, the
    /// speaker's name, and one pad character for every character the two names fall
    /// short of ten. A name pair at or past the width needs no padding, which is
    /// what the second captured frame shows (<c>12v312test:</c> is already twelve).
    /// </para>
    /// </remarks>
    private async Task DeliverPrivateChatAsync(
        uint channelWord,
        string text,
        CancellationToken cancellationToken)
    {
        var character = _character!;
        if (!ChatChannelProtocol.TryReadPrivateRecipient(
                text,
                character.Name,
                out var recipientName,
                out var message) ||
            message.Length == 0)
        {
            Console.WriteLine(
                "[chat] private refused character=" + character.Name +
                $" text='{text}'");
            return;
        }

        if (!_registry.TryFindOnlineChatTarget(
                character.RealmId,
                recipientName,
                _session,
                out var target,
                out var targetName))
        {
            await _session.SendAsync(
                PacketBuilder.ServerNote(
                    $"{recipientName} is not online."),
                cancellationToken,
                "ChatPrivateOffline");
            Console.WriteLine(
                "[chat] private target offline character=" + character.Name +
                $" recipient={recipientName}");
            return;
        }

        // Blocked looks the same as offline, so the sender cannot probe a blacklist.
        if (await IsBlockedFromPrivateChatAsync(targetName, cancellationToken))
        {
            await _session.SendAsync(
                PacketBuilder.ServerNote(
                    $"{recipientName} is not online."),
                cancellationToken,
                "ChatPrivateOffline");
            Console.WriteLine(
                "[chat] private blocked character=" + character.Name +
                $" recipient={recipientName}");
            return;
        }

        // One copy per side, and nothing else. The recipient reads
        // "12e1对test说:hello" and the sender reads "test对12e1说:hello": each side's
        // copy names the other party in the slot its client renders (characters
        // 4..8), which is the shape the captured client frames carry.
        await target.SendAsync(
            PacketBuilder.ChatMessage(
                CurrentPlayerObjectId,
                channelWord,
                ChatChannelProtocol.BuildPrivateBody(
                    targetName,
                    character.Name,
                    message)),
            cancellationToken,
            "ChatPrivate");
        await _session.SendAsync(
            PacketBuilder.ChatMessage(
                CurrentPlayerObjectId,
                channelWord,
                ChatChannelProtocol.BuildPrivateBody(
                    character.Name,
                    targetName,
                    message)),
            cancellationToken,
            "ChatPrivateEcho");
        Console.WriteLine(
            "[chat] private character=" + character.Name +
            $" recipient={targetName} text='{message}'");
    }

    /// <summary>
    /// The character ids of the sender's guild, or null when he has none. The guild
    /// is read from its own store; who of those members is reachable is decided by
    /// the registry, which is the only place that knows who is online.
    /// </summary>
    private async Task<IReadOnlyCollection<int>?> TryResolveGuildMemberIdsAsync(
        CancellationToken cancellationToken)
    {
        if (_guilds is null || _character is null)
        {
            return null;
        }

        var guild = await _guilds.TryReadGuildAsync(
            _character.Id,
            cancellationToken);
        return guild?.Members.Select(static member => member.CharacterId)
            .ToArray();
    }
}
