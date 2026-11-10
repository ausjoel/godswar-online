using Godswar.Server.Infrastructure.Guilds;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// Answers the guild registrar's create request.
    /// </summary>
    /// <remarks>
    /// The request is the client's own guild record (see
    /// <see cref="GuildRegistrarProtocol"/>). The server's part is the part the
    /// client cannot do itself: it checks the level the client's own refusal text
    /// names (<c>ERROR_035B</c>, "you need level 30"), spends the Guild Stone the
    /// other one names (<c>ERROR_035E</c>), refuses a duplicate name or a second
    /// membership, and writes the guild and its lord.
    ///
    /// What the client gets back is still split in two. The outcome line is sent
    /// with the server-note opcode, which is the one piece of feedback whose format
    /// is known; the guild window's own answer
    /// (<c>MSG_CONSORTIA_CREATE_RESPONSE</c> plus the guild's base info) is not,
    /// because the captured reference attempt was refused by the client before it
    /// ever reached a server. Until that answer is established, the request is
    /// echoed back as a probe so the client's reaction can be read.
    /// </remarks>
    private async Task HandleGuildCreateRequestAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            _account is null ||
            !GuildRegistrarProtocol.TryReadCreateRequest(
                packet,
                out var creatorId,
                out var name))
        {
            Console.WriteLine(
                $"[guild] create request ignored len={packet.Length}");
            return;
        }

        Console.WriteLine(
            $"[guild] create request character={_character.Name} " +
            $"level={_character.Level} creator={creatorId} name='{name}'");

        var outcome = _guilds is null
            ? GuildCreateOutcomeIo.Unavailable
            : _character.Level < GuildRegistrarProtocol.MinimumLevel
                ? GuildCreateOutcomeIo.LevelTooLow
                : GuildCreateOutcomeIo.From(
                    await _guilds.TryCreateAsync(
                        _account.Id,
                        _character.Id,
                        name,
                        DateTimeOffset.UtcNow,
                        cancellationToken));

        Console.WriteLine(
            $"[guild] create outcome character={_character.Name} " +
            $"name='{name}' outcome={outcome.Outcome} guild={outcome.GuildId}");

        await _session.SendAsync(
            PacketBuilder.ServerNote(outcome.Message),
            cancellationToken,
            "GuildCreateOutcome");

        if (outcome.Outcome == GuildCreateOutcome.Created)
        {
            if (outcome.ConsumedKitBagSlot is { } consumedSlot)
            {
                await ProjectConsumedGuildStoneAsync(
                    consumedSlot,
                    cancellationToken);
            }

            await SendGuildWindowAsync(
                cancellationToken,
                createdResponse: true,
                creatorObjectId: creatorId);
            // The founder's object now carries a guild too, and the players
            // standing around them were told otherwise when they became visible.
            await RefreshCharacterGuildPresenceAsync(
                _character.Id,
                cancellationToken);
        }

        // Probe: the drawn answer's shape is still being established.
        await _session.SendAsync(
            GuildRegistrarProtocol.CreateProbeResponse(packet.Payload),
            cancellationToken,
            "GuildCreateProbe");
    }

    /// <summary>
    /// Pushes the character's guild in the shape the client's guild window
    /// renders.
    /// </summary>
    /// <remarks>
    /// <c>MSG_CONSORTIA_BASE_INFO</c> is also what sets the client's own guild
    /// state, so a member whose client never saw one shows nothing on the guild
    /// hotkey. The founder additionally gets
    /// <c>MSG_CONSORTIA_CREATE_RESPONSE</c>, the answer its create conversation
    /// expects; that answer is resolved against the requester's own object id, so
    /// the id the request carried is passed through and the base info goes first.
    /// </remarks>
    private async Task SendGuildWindowAsync(
        CancellationToken cancellationToken,
        bool createdResponse = false,
        uint creatorObjectId = 0)
    {
        if (_guilds is null || _character is null)
        {
            return;
        }

        var guild = await _guilds.TryReadGuildAsync(
            _character.Id,
            cancellationToken);
        if (guild is null)
        {
            return;
        }

        // The altar bonus is derived from the guild's own altar levels, so it is
        // re-read whenever the window that shows them is built; the client's
        // status is repainted when it moved.
        var panelChanged = await RefreshGuildPanelStatusAsync(
            _character.Id,
            cancellationToken);
        var altarStatusSent = await PushAltarBonusAsync(cancellationToken);

        await _session.SendAsync(
            PacketBuilder.GuildBaseInfo(guild),
            cancellationToken,
            "GuildBaseInfo");
        if (createdResponse)
        {
            await _session.SendAsync(
                PacketBuilder.GuildCreateResponse(guild, creatorObjectId),
                cancellationToken,
                "GuildCreateResponse");
        }
        var members = guild.Members
            .Select(member => member with
            {
                Online = _registry.IsCharacterOnline(member.CharacterId)
            })
            .ToArray();
        await _guilds.UpdateOnlineCountAsync(
            guild.GuildId,
            members.Count(static member => member.Online),
            DateTimeOffset.UtcNow,
            cancellationToken);
        await _session.SendAsync(
            PacketBuilder.GuildMemberList(members, _character.Id),
            cancellationToken,
            "GuildMemberList");
        // Opening the window is also when the panel's guild rows are re-read: the
        // duty and contribution can have moved since the last status frame, and
        // the altar's own repaint above may already have carried them.
        if (panelChanged && !altarStatusSent)
        {
            await _session.SendAsync(
                BuildLocalPlayerStatusUpdate(),
                cancellationToken,
                "GuildPanelStatus");
        }
        Console.WriteLine(
            $"[guild] window pushed character={_character.Name} " +
            $"guild='{guild.Name}' level={guild.Level} " +
            $"members={guild.Members.Count} " +
            $"online={members.Count(static member => member.Online)} " +
            $"roster={FormatRoster(members)}");
    }

    /// <summary>
    /// Tells the client the spent Guild Stone is gone.
    /// </summary>
    /// <remarks>
    /// The stone leaves the bag inside the create transaction, so the client
    /// keeps drawing it until the slot is evicted: the slot delete plus the
    /// authoritative bag refresh are what make the deduction visible.
    /// </remarks>
    private async Task ProjectConsumedGuildStoneAsync(
        short kitBagSlot,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        _character.KitBag = KitBagSlots.ClearSlot(
            _character.KitBag,
            kitBagSlot);
        await _session.SendAsync(
            PacketBuilder.StorageItemKitBagDelete(kitBagSlot),
            cancellationToken,
            "GuildStoneKitBagDeleteAck");
        await SendKitBagRefreshAsync(cancellationToken);
        Console.WriteLine(
            $"[guild] guild stone consumed character={_character.Name} " +
            $"slot={kitBagSlot}");
    }

    /// <summary>
    /// The outcome of a create attempt plus the line the player is told.
    /// </summary>
    private readonly record struct GuildCreateOutcomeIo(
        GuildCreateOutcome Outcome,
        long GuildId,
        string Message,
        short? ConsumedKitBagSlot = null)
    {
        /// <summary>The store is not wired up in this profile.</summary>
        public static readonly GuildCreateOutcomeIo Unavailable = new(
            GuildCreateOutcome.InvalidName,
            0,
            "Guild creation is unavailable on this server.");

        public static readonly GuildCreateOutcomeIo LevelTooLow = new(
            GuildCreateOutcome.InvalidName,
            0,
            $"You need to be level {GuildRegistrarProtocol.MinimumLevel} " +
            "to found a guild.");

        public static GuildCreateOutcomeIo From(GuildCreateResult result) =>
            new(
                result.Outcome,
                result.GuildId,
                result.Outcome switch
                {
                    GuildCreateOutcome.Created =>
                        $"Guild {result.Name} is founded.",
                    GuildCreateOutcome.AlreadyInGuild =>
                        "You already belong to a guild.",
                    GuildCreateOutcome.NameTaken =>
                        "Another guild already uses that name.",
                    GuildCreateOutcome.MissingGuildStone =>
                        "You need a Guild Stone to found a guild.",
                    _ => "That guild name cannot be used."
                },
                result.ConsumedKitBagSlot);
    }
}
