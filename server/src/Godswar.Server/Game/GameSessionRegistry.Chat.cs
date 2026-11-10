using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

/// <summary>
/// Who one chat message reaches, and the delivery itself.
/// </summary>
/// <remarks>
/// A chat channel is an audience, not a map: the operator's rule is that the
/// world, faction, guild and party channels reach their members wherever they are,
/// and only the stock "nearby" channel is confined to the sender's own world
/// instance. The recipients are therefore resolved here first (a set of session
/// identities) and the frame is written to each of them afterwards, so no channel
/// has to know how routing works and no routing has to know what a channel is.
/// <para>
/// Nothing here awaits while the registry gate is held: the session snapshot is
/// taken under the gate and the writes happen after it, which is the same split
/// the rest of this registry uses.
/// </para>
/// </remarks>
internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// One chat recipient: the session to write to and the object id its client
    /// knows the sender by.
    /// </summary>
    internal readonly record struct ChatRecipient(
        ClientSession Session,
        uint SenderObjectId);

    /// <summary>
    /// The sender's camp, for the faction channel.
    /// </summary>
    internal byte? GetOnlineCharacterCamp(ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            return _sessions.TryGetValue(session, out var context)
                ? context.Character.Camp
                : null;
        }
    }

    /// <summary>
    /// The sender's party members, for the party channel. The sender is included:
    /// a chat line of his own is one the client shows him from his own send, so
    /// the caller decides whether to write it back.
    /// </summary>
    internal IReadOnlyList<int> GetPartyChatCharacterIds(
        ClientSession session) =>
        GetPartyMembership(session)?.MemberCharacterIds ?? [];

    /// <summary>
    /// Every session a channel should reach.
    /// </summary>
    /// <remarks>
    /// The sender is included: the stock client does not print the line of a
    /// message it sent on a channel, so the channel has to write it back or the
    /// speaker never sees his own message. The native "nearby" line reaches him
    /// through the map broadcast it always used (<paramref name="nearby"/>), and
    /// every other channel reaches him through this list.
    /// <para>
    /// Each recipient carries the <em>sender's</em> object id, which is the identity
    /// the writing client matches the message against. It is resolved once, from the
    /// sender's own session state, so a recipient's row can never be used as the
    /// speaker's identity.
    /// </para>
    /// </remarks>
    internal ChatRecipient[] ResolveChatRecipients(
        ClientSession senderSession,
        byte? camp,
        IReadOnlyCollection<int>? characterIds,
        bool nearby = false)
    {
        ArgumentNullException.ThrowIfNull(senderSession);
        lock (_gate)
        {
            if (!_sessions.TryGetValue(senderSession, out var sender))
            {
                return [];
            }

            var senderObjectId = sender.ObjectId;
            var recipients = new List<ChatRecipient>(_sessions.Count);
            foreach (var context in _sessions.Values)
            {
                if (context.Session.IsDisconnected ||
                    context.CharacterId <= 0)
                {
                    continue;
                }

                if (ReferenceEquals(context.Session, senderSession))
                {
                    // Nearby already reached him through the map broadcast; every
                    // other channel writes his own line back to him here.
                    if (!nearby)
                    {
                        recipients.Add(new(senderSession, senderObjectId));
                    }

                    continue;
                }

                if (camp.HasValue && context.Character.Camp != camp.Value ||
                    characterIds is not null &&
                        !characterIds.Contains(context.CharacterId))
                {
                    continue;
                }

                recipients.Add(new(context.Session, senderObjectId));
            }

            return [.. recipients];
        }
    }

    /// <summary>
    /// Writes one already-built frame to a set of recipients.
    /// </summary>
    /// <remarks>
    /// Each recipient gets the frame built for his own view, because the speaker's
    /// object id is what his client matches the message against. One failing
    /// session never stops the message for the others.
    /// </remarks>
    internal async Task<int> DeliverChatAsync(
        IReadOnlyList<ChatRecipient> recipients,
        Func<ChatRecipient, byte[]> buildPacket,
        string label,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(buildPacket);
        var delivered = 0;
        foreach (var recipient in recipients)
        {
            try
            {
                await recipient.Session.SendAsync(
                    buildPacket(recipient),
                    cancellationToken,
                    label);
                delivered++;
            }
            catch (Exception error) when (
                error is not OperationCanceledException ||
                !cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine(
                    "[chat] delivery failed label=" + label + ": " +
                    error.Message);
            }
        }

        return delivered;
    }

    /// <summary>
    /// The online session and character name of one named character in the sender's
    /// realm, for the private channel. Both come from the run's own state rather than
    /// from the text the client wrote, because both are used to build the frame the
    /// two clients read.
    /// </summary>
    internal bool TryFindOnlineChatTarget(
        Domain.World.Instances.RealmId realmId,
        string name,
        ClientSession senderSession,
        out ClientSession target,
        out string characterName)
    {
        lock (_gate)
        {
            target = null!;
            characterName = string.Empty;
            if (!TryFindOnlinePartyMemberLocked(name, realmId, out var found) ||
                ReferenceEquals(found.Session, senderSession))
            {
                return false;
            }

            target = found.Session;
            characterName = found.CharacterName;
            return true;
        }
    }
}
