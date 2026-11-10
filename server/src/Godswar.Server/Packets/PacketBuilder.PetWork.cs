using System.Buffers.Binary;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

// PETWORK (F-137): the pet work window's frames, read from GodsWar.exe (client 1.4.4 = the 2013 exe layout).
internal static partial class PacketBuilder
{
    /// <summary>
    /// S2C 10249 (the client's PetCodeReturn handler 0x69d450, the same opcode as the shed-expansion result):
    /// u8 code at +4 picks Message.dat PetCodeReturn&lt;code&gt;. 78 and 79 also change the client's pet state.
    /// </summary>
    public static byte[] PetWorkCodeReturn(PetWorkResultCode code)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown pet work result code.");
        }

        var packet = new byte[5];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 5);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), PetShedExpansionResultOpcode);
        packet[4] = (byte)code;
        return packet;
    }

    /// <summary>
    /// S2C 10266 (work finished, handler 0x69fa70, text PetWorkReturn) or 10294 (stopped early, handler 0x6a20d0,
    /// text PetStopWorkReturn): u32 EXP at +4, u16 Talent Points at +8. The client ADDS both to the player's
    /// own values, so this goes out before the absolute EXP/status refresh. 12 bytes = the padded struct.
    /// </summary>
    public static byte[] PetWorkReward(ushort opcode, long experience, int talentPoints)
    {
        if (opcode is not (Opcodes.PetWorkFinished or Opcodes.PetWorkStopped))
        {
            throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "Not a pet work reward frame.");
        }

        var packet = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 12);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)Math.Clamp(experience, 0, uint.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8), (ushort)Math.Clamp(talentPoints, 0, ushort.MaxValue));
        return packet;
    }

    /// <summary>
    /// S2C 10291, the answer to the window's 10290 query while the pet works (CPetWorkUI 0x5c93c0): u32 EXP +4,
    /// u16 Talent Points +8, u16 minutes left +0xA (text PetWorkRemainTime "%WorkRemainTime% minutes"), u16 cost
    /// +0xC (shown with "Gold"/"Silver"), u8 pay type +0xE (ticks the box), u8 hours +0xF (1/3/8/24 ticks the box).
    /// The cost field is only 16 bits: a Silver cost above 65,535 (level ~60+ for 24 hours) is shown clamped.
    /// </summary>
    public static byte[] PetWorkStatus(long experience, int talentPoints, int minutesLeft, int cost, byte pay,
        byte hours)
    {
        var packet = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), Opcodes.PetWorkStatus);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)Math.Clamp(experience, 0, uint.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8), (ushort)Math.Clamp(talentPoints, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(10), (ushort)Math.Clamp(minutesLeft, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(12), (ushort)Math.Clamp(cost, 0, ushort.MaxValue));
        packet[14] = pay;
        packet[15] = hours;
        return packet;
    }
}

/// <summary>PETWORK (F-137): Message.dat PetCodeReturn&lt;n&gt; texts the work window uses.</summary>
internal enum PetWorkResultCode : byte
{
    AlreadyAway = 75, // "The pet has already been sent away, this operation is not allowed."
    SummonFirst = 76, // "Please summon a pet to work for you."
    NotEnoughMoney = 77, // "Not enough money, work failed."
    Started = 78, // "Pay %paymoney% for a work commission." 0x6a0a10: removes the model, state 4, deducts locally
    Finished = 79, // "%PetName% has finished their work and returned to you." 0x6a0d00: state 2 (carried)
    NoWorkTalent = 82 // "The pet doesn't have the work talent."
}
