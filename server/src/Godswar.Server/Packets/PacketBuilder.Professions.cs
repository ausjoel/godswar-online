using System.Buffers.Binary;

namespace Godswar.Server.Packets;

// PROF slice 1 (2026-11-09): the client's life-skill messages. The 2013 Origin.exe routes 10334-10341 through one
// jump table at 0x6cfe70 (index = opcode - 10334) whose handlers log the names below; layouts are read from those
// handlers (packet offset N = [packet + N]). 10339 is the 12-byte frame of the Origin login capture.
// Full table, addresses and confidence: Development/professions/README.md.
internal static partial class PacketBuilder
{
    public const ushort LearnLifeSkillOpcode = 10334;      // _MSG_LEARN_LIFESKILL       S2C 16
    public const ushort UnlearnLifeSkillOpcode = 10335;    // _MSG_UNLEARN_LIFESKILL     S2C 8
    public const ushort LearnBlueprintOpcode = 10336;      // _MSG_LEARN_BLUEPRINT       S2C 24
    public const ushort UnlearnBlueprintOpcode = 10337;    // _MSG_UNLEARN_BLUEPRINT     S2C 12
    public const ushort LifeSkillAttributeOpcode = 10338;  // _MSG_SYN_ATTR_INFO         S2C 20
    public const ushort SyncActivityPointsOpcode = 10339;  // _MSG_SYN_AP                S2C 12
    public const ushort MakeBlueprintOpcode = 10340;       // _MSG_MAKE_BLUEPRINT        C2S 16 / S2C 16 error
    public const ushort MakeBlueprintReturnOpcode = 10341; // _MSG_MAKE_BLUEPRINT_RETURN S2C 96

    public const int MakeBlueprintRequestLength = 16;
    public const int MakeBlueprintReturnLength = 96;

    /// <summary>10338 attribute kinds (handler 0x6d0290 jump table): 0 level, 1 exp, 4 proficiency, 5 cooldown.</summary>
    public const byte LifeSkillLevelAttribute = 0;
    public const byte LifeSkillExperienceAttribute = 1;
    public const byte BlueprintProficiencyAttribute = 4;
    public const byte BlueprintCooldownAttribute = 5;

    /// <summary>ERROR_05xx keys of ErrorMessage.dat that the 10340 error frame prints as ERROR_%04x.</summary>
    public const int CraftErrorMaterials = 0x500;
    public const int CraftErrorBagFull = 0x501;
    public const int CraftErrorActivityPoints = 0x502;
    public const int CraftErrorCooldown = 0x503;
    public const int CraftErrorLocation = 0x504;
    public const int CraftErrorKnown = 0x509;
    public const int CraftErrorLevelOrClass = 0x50A;

    private static byte[] LifeSkillFrame(ushort opcode, int length)
    {
        var packet = new byte[length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, checked((ushort)length));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), opcode);
        return packet;
    }

    /// <summary>10334: +4 profession (SkillID), +8 level, +12 exp. The client looks the SkillID up in Lifing_Skill.</summary>
    public static byte[] LearnLifeSkill(byte profession, int level, int experience)
    {
        var packet = LifeSkillFrame(LearnLifeSkillOpcode, 16);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), profession);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), level);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12), experience);
        return packet;
    }

    /// <summary>10335: +4 profession. Removes the profession and its recipe list from the window.</summary>
    public static byte[] UnlearnLifeSkill(byte profession)
    {
        var packet = LifeSkillFrame(UnlearnLifeSkillOpcode, 8);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), profession);
        return packet;
    }

    /// <summary>
    /// 10336: +4 profession, +8 blueprint, +12 proficiency, +16 cooldown seconds (the client adds 50 when it is
    /// not 0), +20 u8 "newly learned" (passed to the window refresh; 0 at login, 1 when learned in play).
    /// </summary>
    public static byte[] LearnBlueprint(byte profession, int blueprintId, int proficiency, int cooldownSeconds, bool fresh)
    {
        var packet = LifeSkillFrame(LearnBlueprintOpcode, 24);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), profession);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), blueprintId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12), proficiency);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(16), cooldownSeconds);
        packet[20] = fresh ? (byte)1 : (byte)0;
        return packet;
    }

    /// <summary>10337: +4 profession, +8 blueprint.</summary>
    public static byte[] UnlearnBlueprint(byte profession, int blueprintId)
    {
        var packet = LifeSkillFrame(UnlearnBlueprintOpcode, 12);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), profession);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), blueprintId);
        return packet;
    }

    /// <summary>10338: +4 profession, +8 blueprint (kinds 4/5), +12 u8 kind, +16 value.</summary>
    public static byte[] LifeSkillAttribute(byte profession, int blueprintId, byte kind, int value)
    {
        var packet = LifeSkillFrame(LifeSkillAttributeOpcode, 20);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), profession);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), blueprintId);
        packet[12] = kind;
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(16), value);
        return packet;
    }

    /// <summary>10339: +4 current AP, +8 maximum AP (12 bytes, as in the Origin login capture).</summary>
    public static byte[] SyncActivityPoints(int ap, int maxAp)
    {
        var packet = LifeSkillFrame(SyncActivityPointsOpcode, 12);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), ap);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), maxAp);
        return packet;
    }

    /// <summary>
    /// 10340 S2C: the refusal. The handler (0x6d7900) stops the batch and prints ERROR_%04x of +12; +4/+8 echo
    /// the request.
    /// </summary>
    public static byte[] MakeBlueprintError(byte profession, int blueprintId, int errorCode)
    {
        var packet = LifeSkillFrame(MakeBlueprintOpcode, MakeBlueprintRequestLength);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), profession);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), blueprintId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12), errorCode);
        return packet;
    }

    /// <summary>
    /// 10341: +4 profession, +8 blueprint, +12 result (0 MAKE_LIFESKILL_MSG1 failed, 1 MSG2 successful, 2 MSG3),
    /// +16 profession exp gained (printed with MSG6 "Exp"), +24 the 72-byte item record of the product (read
    /// only when the result is not 0). The handler starts the next cast of the batch, so every request gets one.
    /// </summary>
    public static byte[] MakeBlueprintReturn(byte profession, int blueprintId, int result, int experience,
        uint productId, int productCount, bool bound)
    {
        var packet = LifeSkillFrame(MakeBlueprintReturnOpcode, MakeBlueprintReturnLength);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), profession);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), blueprintId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12), result);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(16), experience);
        if (result != 0 && productId != 0)
        {
            // The kit-bag item record shape (PacketBuilder.ItemSerialization): id, five empty attributes,
            // quality/grade 1, binding, stack.
            var record = packet.AsSpan(24, 72);
            BinaryPrimitives.WriteUInt32LittleEndian(record, productId);
            for (var offset = 4; offset <= 20; offset += 4)
            {
                BinaryPrimitives.WriteInt32LittleEndian(record.Slice(offset, 4), -1);
            }

            record[24] = 1;
            record[25] = 1;
            record[26] = bound ? (byte)1 : (byte)0;
            record[27] = (byte)Math.Clamp(productCount, 1, 99);
        }

        return packet;
    }
}
