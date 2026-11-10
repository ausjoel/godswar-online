using System.Buffers.Binary;
using Godswar.Server.Application.Characters;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    public static byte[] LocalizedError(int errorCode)    {
        if (errorCode <= 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(errorCode),
                "Localized error codes must not overlap repetition states.");
        }

        var packet = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.RepetitionQueueState);
        // 10222 (_MSG_REPETITION_CODE, Origin 0x4BE935): +8 values 0..3 are
        // queue results; larger values are formatted as ERROR_%0.4x and
        // appended to the message log. Do NOT use 10216: its receiver
        // (0x4BEB5C) opens the 60-second instance Enter window ("Labyrinth of
        // Minos") unconditionally and overwrites the pending scene id.
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(8),
            errorCode);
        return packet;
    }

    public static byte[] RepetitionNotice(
        int repetitionId,
        int invitationId)
    {
        if (repetitionId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(repetitionId),
                "Repetition notices require positive identities.");
        }
        if (invitationId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(invitationId),
                "Repetition notices require positive identities.");
        }

        var packet = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.RepetitionNotice);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(4),
            repetitionId);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(8),
            invitationId);
        return packet;
    }

    public static byte[] RepetitionInvitation(
        int repetitionId,
        int invitationId,
        string inviterName)
    {
        if (repetitionId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(repetitionId),
                "Repetition invitations require positive identities.");
        }
        if (invitationId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(invitationId),
                "Repetition invitations require positive identities.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(inviterName);

        const int headerAndIdsBytes = 12;
        var packet = new byte[
            headerAndIdsBytes + CharacterSnapshotLimits.CharacterNameLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.RepetitionInvitation);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(4),
            repetitionId);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(8),
            invitationId);
        PacketText.WriteFixedAscii(
            packet.AsSpan(
                headerAndIdsBytes,
                CharacterSnapshotLimits.CharacterNameLength),
            inviterName);
        return packet;
    }

    public static byte[] RepetitionCountdown(int seconds)
    {
        if (seconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }

        var packet = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.RepetitionReset);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(4),
            seconds);
        return packet;
    }

    public static byte[] RepetitionReset() => RepetitionCountdown(0);

    public static byte[] RepetitionPanelCompletion()
    {
        var packet = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 6);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.RepetitionPanelAction);
        packet[4] = 3;
        return packet;
    }

    public static byte[] RepetitionCompletionState(
        int repetitionId,
        bool completed)
    {
        if (repetitionId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(repetitionId));
        }

        var packet = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.RepetitionCompletionState);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(4),
            repetitionId);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(8),
            completed ? 1 : 0);
        return packet;
    }

    public static byte[] RepetitionInstanceMembers(
        IReadOnlyList<RepetitionInstanceMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        const int memberBytes = 44;
        var packet = new byte[checked(8 + members.Count * memberBytes)];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.RepetitionInstanceMembers);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(4),
            members.Count);

        for (var index = 0; index < members.Count; index++)
        {
            var member = members[index];
            if (member.CharacterId <= 0 || member.Level <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(members));
            }

            var offset = 8 + index * memberBytes;
            BinaryPrimitives.WriteInt32LittleEndian(
                packet.AsSpan(offset),
                member.CharacterId);
            PacketText.WriteFixedAscii(
                packet.AsSpan(
                    offset + sizeof(int),
                    CharacterSnapshotLimits.CharacterNameLength),
                member.Name);
            BinaryPrimitives.WriteInt32LittleEndian(
                packet.AsSpan(offset + 36),
                member.Level);
            // +40 decides whether the client draws the row at all, in-game
            // confirmed: 0 is offline, 1 draws the row (「在线」, and the operator
            // reads 「在线(队长)」 for the run's leader), 2 draws 「等待中」, and any
            // other value makes the client skip the row entirely. Only 0, 1 and 2
            // are ever written; 「在线」 and 「在线(队长)」 deliberately share 1.
            packet[offset + 40] = (byte)member.State;
            packet[offset + 41] = member.Profession;
            // +42 and +43 are left untouched. They were tried for the 「(队长)」
            // suffix and provably do not decide it (the suffix did not move), and
            // the stock capture only ever shows zero, so nothing is written there.
            // The suffix question itself is SUSPENDED: it needs client
            // disassembly or an original-server frame as evidence, not another
            // guess from this side.
        }

        return packet;
    }

    public static byte[] RepetitionSync(
        ushort repetitionId,
        ushort repetitionIndex,
        ushort groupIndex,
        ushort state,
        ushort entryLimit)
    {
        if (repetitionId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(repetitionId),
                "Repetition synchronization requires positive identities.");
        }

        var packet = new byte[14];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.RepetitionSync);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(4),
            repetitionId);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(6),
            repetitionIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(8),
            groupIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(10),
            state);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(12),
            entryLimit);
        return packet;
    }

    public static byte[] RepetitionFightInfo(
        int remainingSeconds,
        int teamScore)
    {
        if (remainingSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(remainingSeconds));
        }
        if (teamScore < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(teamScore));
        }

        var packet = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.RepetitionFightInfo);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(4),
            remainingSeconds);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(8),
            1);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(12),
            1);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(16),
            teamScore);
        return packet;
    }

    public static byte[] RepetitionReward(int hardPoints)
    {
        if (hardPoints <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hardPoints));
        }

        // The external Medusa completion capture carries its documented
        // HardPoint award in the native SimplePoint/Honor field at +92. The
        // client exposes that shared balance as Honor in Character Details.
        var packet = new byte[104];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.RepetitionReward);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(4),
            17);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(92),
            hardPoints);
        return packet;
    }
}

/// <summary>
/// The instance roster's per-member state byte at <c>+40</c>.
/// </summary>
/// <remarks>
/// The stock client renders this single byte as one piece of text (「在线(队长)」
/// 「等待中」…), and it also decides whether the client draws the row at all.
/// <para>
/// In-game confirmed, and the only values ever written: <c>0</c> is 「离线」,
/// <c>1</c> draws the row (「在线」, read by the operator as 「在线(队长)」 for the
/// run's leader), <c>2</c> is 「等待中」. Every other value - <c>3</c>, <c>4</c>
/// and <c>5</c> were all tried on a live client - makes the client skip the row
/// entirely, which is why 「在线」 and 「在线(队长)」 deliberately share <c>1</c>.
/// </para>
/// <para>
/// Which byte carries the 「(队长)」 suffix is NOT settled and is SUSPENDED:
/// <c>+42</c> and <c>+43</c> were both tried and provably do not move the
/// suffix, so the answer needs client disassembly or an original-server frame
/// rather than another guess. Nothing on this side changes until that evidence
/// exists.
/// </para>
/// The value is decided once, by the one roster implementation every instance
/// publishes from (<c>GameSessionRegistry.SnapshotInstanceRoster</c>); the packet
/// builder only writes it.
/// </remarks>
internal enum RepetitionMemberState : byte
{
    Offline = 0,
    // The leader and every other member inside share the one value that draws
    // the row; the distinction between them is not this byte's to make.
    OnlineLeader = 1,
    Waiting = 2,
    Online = OnlineLeader
}

/// <summary>
/// One row of the native instance roster (opcode 10218).
/// </summary>
/// <remarks>
/// <paramref name="State"/> is the single state byte the client reads at
/// <c>+40</c>; see <see cref="RepetitionMemberState"/>.
/// </remarks>
internal readonly record struct RepetitionInstanceMember(
    int CharacterId,
    string Name,
    int Level,
    RepetitionMemberState State,
    byte Profession);
