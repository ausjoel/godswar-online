using System.Buffers.Binary;
using Godswar.Server.Infrastructure.Guilds;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

/// <summary>
/// The guild window's application actions: the officers' "refuse applications"
/// switch, a guildless character asking to join from the List tab, and an
/// officer's answer to that application.
/// </summary>
/// <remarks>
/// <para>
/// All three are the window's own messages: ticking <c>RejectRequest</c> sends
/// <c>10155</c> with the new state as a lone dword, the List tab's Apply button
/// sends <c>10146</c> naming the selected guild (its body leads with the entry
/// key, which is the guild id), and answering an application sends <c>10145</c>
/// whose <c>body+4</c> is the applicant's name - measured 2026-10-02 in the
/// running client: the officer's accept produced
/// <c>10145 len=104 body+0=0 body+4="12e1"</c>.
/// </para>
/// <para>
/// The feedback the client prints is its own: every refusal below is sent as the
/// client's text <em>key</em> (<c>ERROR_03D6</c>/<c>ERROR_03D7</c>/
/// <c>ERROR_03D8</c>/<c>ERROR_03D9</c>/<c>ERROR_036B</c>/<c>ERROR_0368</c>), which
/// its own table carries in the player's language, instead of an English line
/// this server would have had to invent.
/// </para>
/// </remarks>
internal sealed partial class GameClientHandler
{
    /// <summary>
    /// The duty that may answer an application: <c>Consortia_Job.ini</c> numbers 5
    /// as 副会长 and 6 as 会长.
    /// </summary>
    private const byte ApplicationOfficerDuty = 5;

    /// <summary>The 32-byte applicant-name field the client's requests carry.</summary>
    private const int GuildApplicantNameLength = 32;

    // The guild window's feedback is the client's own text. These are the exact
    // encoded bytes of the entries the client ships in
    // Localization/zh_cn/Text/ErrorMessage.dat (UTF-16 there; GBK on the wire,
    // because the note frame is decoded with the machine's ANSI code page, 936
    // here). They are sent on the personal channel, which the client's own Lua
    // paints in the lower-right log, rather than as an English line this server
    // would have invented. Extract again with tools/ scripts if the client's
    // table changes.
    private static readonly byte[] GuildRefusesApplicationsText =
        Convert.FromHexString("B8C3B9ABBBE1BEDCBEF8C9EAC7EBBCD3C8EB");
    private static readonly byte[] NoGuildOfficerOnlineText =
        Convert.FromHexString("B9ABBBE1BBE1B3A4B8B1BBE1B3A4B6BCB2BBD4DACFDF20CEDEB7A8BCD3C8EB");
    private static readonly byte[] GuildApplicationSentText =
        Convert.FromHexString("C7EBC4CDD0C4B5C8B4FDB9ABBBE1D1FBC7EBB5C4CFFBCFA2");
    private static readonly byte[] NoSuchGuildText =
        Convert.FromHexString("B8C3B9ABBBE1B2BBB4E6D4DA");
    private static readonly byte[] GuildFullText =
        Convert.FromHexString("B9ABBBE1C2FAD4B1");
    private static readonly byte[] ApplicantHasGuildText =
        Convert.FromHexString("B6D4B7BDD2D1D3D0B9ABBBE1");
    private static readonly byte[] AlreadyInGuildText =
        Convert.FromHexString("D2D1D3D0B9ABBBE1");
    private static readonly byte[] NotPermittedText =
        Convert.FromHexString("C0EDCAC2D2D4CFC2D6B0CEBBC3BBD3D0CFE0D3A6C8A8CFDE");
    private static readonly byte[] NotInGuildText =
        Convert.FromHexString("B6D4B2BBC6F0A3ACC4E3C3BBD3D0BCD3C8EBC8CEBACEB9ABBBE1");
    private static readonly byte[] ApplicantUnavailableText =
        Convert.FromHexString("CDE6BCD2B2BBD4DACFDF");

    /// <summary>
    /// Prints one of the client's own texts on its personal (lower-right) log
    /// channel.
    /// </summary>
    private Task SendGuildTextAsync(
        byte[] text,
        string label,
        CancellationToken cancellationToken) =>
        _session.SendAsync(
            PacketBuilder.PersonalNotice(text),
            cancellationToken,
            label);

    private async Task HandleGuildRefuseApplicationsRequestAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        if (packet.Payload.Length < 4)
        {
            Console.WriteLine(
                $"[guild] refuse request ignored len={packet.Length} " +
                $"{packet.ToHexPreview()}");
            return;
        }

        var refuse = BinaryPrimitives.ReadUInt32LittleEndian(
            packet.Payload[..4]) != 0;
        Console.WriteLine(
            $"[guild] refuse request character={_character.Name} refuse={refuse}");

        if (_guilds is null)
        {
            await RefuseGuildActionAsync(
                "Guilds are unavailable on this server.",
                "GuildRefuseUnavailable",
                cancellationToken);
            return;
        }

        var outcome = await _guilds.SetRefuseApplicationsAsync(
            _character.Id,
            refuse,
            DateTimeOffset.UtcNow,
            cancellationToken);
        switch (outcome)
        {
            case GuildRefuseOutcome.NotAMember:
                await SendGuildTextAsync(
                    NotInGuildText,
                    "GuildRefuseNotAMember",
                    cancellationToken);
                return;
            case GuildRefuseOutcome.NotPermitted:
                await SendGuildTextAsync(
                    NotPermittedText,
                    "GuildRefuseNotPermitted",
                    cancellationToken);
                return;
            default:
                // The switch is broadcast with the list itself: every player's
                // Join cell follows the entry byte, so nothing else has to be
                // pushed for the guild's own window.
                Console.WriteLine(
                    $"[guild] refuse applications set " +
                    $"character={_character.Name} refuse={refuse}");
                return;
        }
    }

    private async Task HandleGuildApplyRequestAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        if (packet.Payload.Length < 4)
        {
            Console.WriteLine(
                $"[guild] apply request ignored len={packet.Length} " +
                $"{packet.ToHexPreview()}");
            return;
        }

        var guildId = BinaryPrimitives.ReadUInt32LittleEndian(
            packet.Payload[..4]);
        Console.WriteLine(
            $"[guild] apply request character={_character.Name} " +
            $"guild={guildId} payload={packet.Payload.Length}");

        if (_guilds is null)
        {
            await RefuseGuildActionAsync(
                "Guilds are unavailable on this server.",
                "GuildApplyUnavailable",
                cancellationToken);
            return;
        }

        if (await _guilds.IsCharacterInGuildAsync(
                _character.Id,
                cancellationToken))
        {
            // One guild per character (Consortia.dat HadConsortia).
            await SendGuildTextAsync(
                AlreadyInGuildText,
                "GuildApplyAlreadyInGuild",
                cancellationToken);
            return;
        }

        var guild = await _guilds.TryReadApplicationContextAsync(
            guildId,
            cancellationToken);
        if (guild is null)
        {
            await SendGuildTextAsync(
                NoSuchGuildText,
                "GuildApplyNoSuchGuild",
                cancellationToken);
            return;
        }

        if (guild.RefusesApplications)
        {
            // The list already draws this guild's Join cell as 拒绝 ("ConReject")
            // from the entry's +0x65 byte; this is what answers a player who
            // clicked it anyway, in the client's own words.
            await SendGuildTextAsync(
                GuildRefusesApplicationsText,
                "GuildApplyRefused",
                cancellationToken);
            return;
        }

        if (guild.MemberCount >= guild.MemberLimit)
        {
            await SendGuildTextAsync(
                GuildFullText,
                "GuildApplyFull",
                cancellationToken);
            return;
        }

        var delivered = 0;
        foreach (var officer in guild.Officers)
        {
            if (officer.Duty < ApplicationOfficerDuty)
            {
                continue;
            }

            if (!_registry.TryGetCharacterSession(
                    officer.CharacterId,
                    out var session))
            {
                continue;
            }

            await session.SendAsync(
                PacketBuilder.GuildJoinApplicationNotice(_character.Name),
                cancellationToken,
                "GuildJoinApplication");
            delivered++;
        }

        if (delivered == 0)
        {
            // Asked for explicitly: with no officer online there is nobody to
            // answer, so the applicant is told in the client's own words
            // (ERROR_03D8 公会会长副会长都不在线 无法加入).
            await SendGuildTextAsync(
                NoGuildOfficerOnlineText,
                "GuildApplyNoOfficerOnline",
                cancellationToken);
            Console.WriteLine(
                $"[guild] apply no officer online character={_character.Name} " +
                $"guild='{guild.Name}' officers={guild.Officers.Count}");
            return;
        }

        await SendGuildTextAsync(
            GuildApplicationSentText,
            "GuildApplySent",
            cancellationToken);
        Console.WriteLine(
            $"[guild] apply delivered character={_character.Name} " +
            $"guild='{guild.Name}' officers={guild.Officers.Count} " +
            $"delivered={delivered}");
    }

    /// <summary>
    /// An officer's answer to an application: the client sends the applicant's
    /// name, and the server seats them in the guild.
    /// </summary>
    /// <remarks>
    /// The request's leading dword was zero in every measured accept
    /// (<c>10145 len=104 body+0=0</c>); any other value is refused rather than
    /// guessed at, so a reject path that uses a different value cannot silently
    /// add the applicant. The applicant's own window then comes alive through the
    /// roster push, because the guild state on the client is set by
    /// <c>10138</c>.
    /// </remarks>
    private async Task HandleGuildApplicationAnswerAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        if (packet.Payload.Length < 4 + GuildApplicantNameLength)
        {
            Console.WriteLine(
                $"[guild] application answer ignored len={packet.Length} " +
                $"{packet.ToHexPreview()}");
            DumpUnknownPacketWords(packet);
            return;
        }

        var kind = BinaryPrimitives.ReadUInt32LittleEndian(
            packet.Payload[..4]);
        var applicantName = PacketText.ReadFixedAscii(
            packet.Payload,
            4,
            GuildApplicantNameLength);
        Console.WriteLine(
            $"[guild] application answer character={_character.Name} " +
            $"kind={kind} applicant='{applicantName}'");

        if (kind != 0)
        {
            Console.WriteLine(
                $"[guild] application answer ignored kind={kind} " +
                "(only the measured accept value 0 is handled)");
            return;
        }

        if (_guilds is null)
        {
            await RefuseGuildActionAsync(
                "Guilds are unavailable on this server.",
                "GuildAnswerUnavailable",
                cancellationToken);
            return;
        }

        var guild = await _guilds.TryReadGuildAsync(
            _character.Id,
            cancellationToken);
        if (guild is null)
        {
            await SendGuildTextAsync(
                NotInGuildText,
                "GuildAnswerNotAMember",
                cancellationToken);
            return;
        }

        var actor = guild.Members.FirstOrDefault(
            member => member.CharacterId == _character.Id);
        if (actor is null || actor.Duty < ApplicationOfficerDuty)
        {
            await SendGuildTextAsync(
                NotPermittedText,
                "GuildAnswerNotPermitted",
                cancellationToken);
            return;
        }

        var result = await _guilds.TryAddMemberAsync(
            guild.GuildId,
            applicantName,
            DateTimeOffset.UtcNow,
            cancellationToken);
        switch (result.Outcome)
        {
            case GuildJoinOutcome.ApplicantNotFound:
                await SendGuildTextAsync(
                    ApplicantUnavailableText,
                    "GuildAnswerApplicantMissing",
                    cancellationToken);
                return;
            case GuildJoinOutcome.ApplicantAlreadyInGuild:
                await SendGuildTextAsync(
                    ApplicantHasGuildText,
                    "GuildAnswerApplicantHasGuild",
                    cancellationToken);
                return;
            case GuildJoinOutcome.GuildMissing:
                await SendGuildTextAsync(
                    NoSuchGuildText,
                    "GuildAnswerGuildMissing",
                    cancellationToken);
                return;
            case GuildJoinOutcome.GuildFull:
                await SendGuildTextAsync(
                    GuildFullText,
                    "GuildAnswerGuildFull",
                    cancellationToken);
                return;
        }

        // The new member's own panel, roster and nameplate, and every existing
        // member's roster, are repainted from the guild as it now stands.
        await RefreshCharacterGuildPresenceAsync(
            result.CharacterId,
            cancellationToken);
        var members = guild.Members
            .Select(member => member.CharacterId)
            .Append(result.CharacterId);
        foreach (var memberId in members)
        {
            await PushGuildWindowToMemberAsync(memberId, cancellationToken);
        }

        Console.WriteLine(
            $"[guild] application accepted character={_character.Name} " +
            $"guild='{guild.Name}' applicant='{applicantName}' " +
            $"id={result.CharacterId}");
    }
}
