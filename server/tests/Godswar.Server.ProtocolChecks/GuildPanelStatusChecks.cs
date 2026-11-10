using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// The character panel's two guild rows: <c>S2C 10166</c> carries the
/// character's duty as one byte at wire offset 76 and the guild contribution it
/// still holds as a dword at wire offset 80.
/// </summary>
/// <remarks>
/// The slots are the client's own: the attribute writer <c>0x4A8A00</c> fills
/// <c>GameData+0x2A0</c> (one byte) for attribute id <c>0x0A</c> and
/// <c>GameData+0x2A4</c> for id <c>0x0B</c>, and the PersonalInfoUI update reads
/// the first into its <c>DutyText</c> control (<c>movzx edx, byte ptr</c>,
/// formatted <c>"G%d"</c>) and the second into <c>ContributeText</c>. The status
/// block starts at wire 8 and lands on <c>GameData+0x25C</c>, so the wire
/// offsets are 76 and 80; <c>GameData+0x2A8</c> (wire 84) is written by the
/// attribute writer for id <c>0x0C</c> but never read by the panel, so the
/// captured template's value there is left alone.
/// </remarks>
internal static class GuildPanelStatusChecks
{
    public const string CheckName =
        "Guild panel duty and contribution projection (10166 wire 76/80)";

    private const uint RemoteObjectId = 0x7135_B24E;
    private const int DutyOffset = 76;
    private const int ContributionOffset = 80;
    private const int UnreadTemplateOffset = 84;
    private const int CharacterId = 4_242;

    public static Task RunAsync()
    {
        var character = CreateCharacter();

        // Nothing was read for this character yet: both rows are blank.
        GuildPanelStatusCache.Clear(CharacterId);
        var blank = PacketBuilder.PlayerStatusUpdate(
            character,
            movementSpeedMultiplier: 1f);
        Check.Equal(0, blank[DutyOffset], "uncached duty at wire 76");
        Check.Equal(
            0,
            ReadInt32(blank, ContributionOffset),
            "uncached contribution at wire 80");

        // Distinct values, so the two slots cannot be confused with each other.
        Check.True(
            GuildPanelStatusCache.Set(
                CharacterId,
                new GuildPanelStatus(Duty: 4, Contribution: 123_456)),
            "the first write of a status is a change");
        Check.True(
            !GuildPanelStatusCache.Set(
                CharacterId,
                new GuildPanelStatus(Duty: 4, Contribution: 123_456)),
            "rewriting the same status is not a change");

        var local = PacketBuilder.PlayerStatusUpdate(
            character,
            movementSpeedMultiplier: 1f);
        Check.Equal(4, local[DutyOffset], "local duty byte at wire 76");
        Check.Equal(
            123_456,
            ReadInt32(local, ContributionOffset),
            "local contribution at wire 80");
        Check.Equal(
            40,
            ReadInt32(local, UnreadTemplateOffset),
            "wire 84 keeps the captured template's 40: the panel never reads it");
        Check.Equal(
            236,
            local.Length,
            "the guild rows do not change the status packet length");

        // A remote status carries the duty as well: TargetInfoWnd reads the same
        // object byte (GameData+0x2A0) on the target's own status block, and the
        // remote frame otherwise would put the template's zero back over it. The
        // contribution stays private to the character's own panel.
        var remote = PacketBuilder.PlayerStatusUpdate(character, RemoteObjectId);
        Check.Equal(4, remote[DutyOffset], "remote duty follows the character's status");
        Check.Equal(
            0,
            ReadInt32(remote, ContributionOffset),
            "remote contribution stays the template's zero");
        Check.Equal(
            40,
            ReadInt32(remote, UnreadTemplateOffset),
            "remote wire 84 stays the captured template's 40");

        GuildPanelStatusCache.Clear(CharacterId);
        return Task.CompletedTask;
    }

    private static int ReadInt32(byte[] packet, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(offset, 4));

    private static GameCharacter CreateCharacter() =>
        new()
        {
            Id = CharacterId,
            AccountId = 4_243,
            Name = "GuildPanelHero",
            Camp = GameDefaults.AthensCamp,
            Profession = 0,
            Level = 40,
            CurrentMap = 3,
            CalculatedStats = new CharacterStats()
        };
}
