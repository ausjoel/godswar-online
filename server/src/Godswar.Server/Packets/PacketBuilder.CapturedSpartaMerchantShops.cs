using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    // Exact opcode-10071 streams the reference server sent on 2026-09-28
    // (capture session c202c633, local 01:52-02:09). Only the
    // authoritative npc id, the live balance and the currency byte are
    // patched at egress; every item record, category, order and price
    // stays byte-identical to the capture.
    //
    // The currency byte is the one field this port decides rather than
    // copies: seven of these shelves were captured carrying 0x04. The
    // user's requirement is that every item on these shelves is bought
    // with silver, and the capture agrees on the price - every equipment
    // listing costs the item's own silver price in the client's
    // ItemBaseAttribute.xml (1000 at 58, 1001 at 129, 1002 at 188, ...).
    // Byte 9 is rewritten to the client's silver code (3) on load, and
    // every other captured byte is preserved.

    /// <summary>The client's own silver currency byte.</summary>
    private const byte SpartaMerchantSilverCurrencyByte = 3;

    /// <summary>The captured [Warrior] equipment merchant shelf.</summary>
    /// <remarks>
    /// <para>Sparta_028 and Sparta_096, who stream one identical shelf.</para>
    /// <para>
    /// 5 frames, 63 listings,
    /// categories [0, 0, 1, 2, 3], captured currency byte(s)
    /// ['0x4'].
    /// </para>
    /// </remarks>
    private const string SpartaWarriorEquipmentMerchantCatalogGzip =
        "H4sIAAAAAAACCrXWOUrEYBiH8S/LO0ZEEOy8gK2F67h2XkDwEFZ27jraWIlnEKy8gMu4jR7AG9i6r2ClxmGcAWH+MxZ5vhSBJOT3vYTkIds23b3T6ZyLO4Ly3t1EzqViCwJXuf7fFoVpmv9zXLvpFnALwr0D3KJw7wH3Q7gPgNsT1LuPgDsj3CfA3RXuM+BeC/cFcLvCevcVcKeE+wa4W8J9B9w94c6Znz7Mm58+LJifPiyanz5chbUeJ5XjJfPTi2Xz04sV89OLVfPTizXz04uC+enFuvnpxYb56cVm/Ps+B3F75dRnkm2duLzOpPhuotbsrurHFzCv6kcMzKv6YYCrevENPAfVixwwr+pFCsyretECzKt64QBX9SIB3FnhBoCrOhQCbrMOhdUO9WZ8P6zBf0wf4KoO9QOu6tAA4KoODQKu6tAQ4KoO5QFXdWgYcFWHRgBXdWgUcFWHJgBXdWgMcFWHxgG3WYeiaof227Ktk2vwP3QKuKpDB4CrOnQGuKpD54CrOnQIuKpDJcBVHToCXNWhC8BVHSoCrurQJeCqDh0DrurQCeCqDv0AAsV/7vgVAAA=";

    private static readonly Lazy<byte[]> SpartaWarriorEquipmentMerchantCatalog = new(
        () => ApplySpartaMerchantCurrency(InflateCatalog(
            SpartaWarriorEquipmentMerchantCatalogGzip,
            expectedLength: 5_624,
            expectedPacketCount: 5,
            expectedCapturedNpcId: 5_026)));

    /// <summary>The captured [Scholar] equipment merchant shelf.</summary>
    /// <remarks>
    /// <para>Sparta_029 and Sparta_107, who stream one identical shelf.</para>
    /// <para>
    /// 5 frames, 63 listings,
    /// categories [0, 0, 1, 2, 3], captured currency byte(s)
    /// ['0x4'].
    /// </para>
    /// </remarks>
    private const string SpartaScholarEquipmentMerchantCatalogGzip =
        "H4sIAAAAAAACCrXWOU7DQBiG4bHjZBxCIJCOC9BScwEugETLvoWGI9BRIUr2LQlLwtKyhJ0DcANuQWsik0hI+RKKvDOFpbHl52/sV7OZnBgu5o0xQc6rX00pZUwklueZ+Pl/K+FH0eifffOlMuCuCfcUcGvCPQPcb+GeA+6I1+peAG5BuBXALQu3Crhfwr0E3CG/1b0C3HHhXgPuhnBvALcq3NC66UPauulDj3XTh4x104dPv9njMN73Wje9yFo3veizbnrRb930Imfd9GLAuunFoHXTi7x104v14Pd79oJsfGsy3d2coD5nTPw3C4Cr+jEFuKofi4Cr+rEEuKoX04CrerEMuKoXM4CrelEAXNWLWcBVvVgB3FXhzgGu6tA84HbqkN/o0FbY3Zxkm3PMNuCqDu0ArurQLuCqDu0BrurQPuCqDh0ArurQIeCqDh0BrurQMeCqDpUAV3XoBHBVh4qA26lDiUaHbjPdzUm1OQ89A67q0B3gqg69AK7q0Cvgqg7dA67q0Bvgqg49AK7q0Dvgqg7VAFd16ANwVYceAVd16AlwVYd+ABubxlv4FQAA";

    private static readonly Lazy<byte[]> SpartaScholarEquipmentMerchantCatalog = new(
        () => ApplySpartaMerchantCurrency(InflateCatalog(
            SpartaScholarEquipmentMerchantCatalogGzip,
            expectedLength: 5_624,
            expectedPacketCount: 5,
            expectedCapturedNpcId: 5_027)));

    /// <summary>The captured skill merchant shelf.</summary>
    /// <remarks>
    /// <para>Sparta_034 and Sparta_099, who stream one identical shelf.</para>
    /// <para>
    /// 8 frames, 86 listings,
    /// categories [0, 0, 1, 1, 2, 2, 3, 3], captured currency byte(s)
    /// ['0x4'].
    /// </para>
    /// </remarks>
    private const string SpartaSkillMerchantCatalogGzip =
        "H4sIAAAAAAACCrXXz0tUURjG8Xvv+KNmbBR/YWY6lmCYoKKLXNRCaD+QYJCYUOBAgQO6sCBwKSh4oaBAIcEWtkikbbsWIq3mD3ChRC50YYgQKGg6zoXwfS4t5nvvYpjDDJ9zOJx7nvf1S4faV2ocxympcs8+nYd1jnMqHtd18r//70l4Z//9Zxx8vwu4G8K9D7jfXevejsjtjchtANyZq9btBNxx4Q4Abk64bYB7K2Hde4CbFm4f4K5XWPdBRO4NwK2qtG4X4K7VWrcfcA+qrdtEnAex3g03uI9L8+N2YB6/3s7TArjdTdbtBtwO4d4B3G83rZsC3NfN1vULueoWcrW6vrh5KkJyNQ64Kle36op3N4V7ArgZkX+/AFfl6n5E7i7g5jzr/gFcv9y6p4CbvmLdBHB+s6IOOCT2V7jbgKty9XdErgfsb07k9Q6w3p6kdfcA96dwp7zgPr443ElgX1S9EQPc8xr2snsE7Eu2wbplwHpVHVAJuKoOOAb2QeV1ObDevVR4HeAV6oCvNcXNcy2kDlgAXFUHvAVclX9fInKXAVfl6gzgqv56BXBVXi8Crurb3wFuUvTBq4A7LfJvFnBVv/oJcCdFTn0G3CWx3o/EeyH64DnAVX3we+K9EH1v1g3u45L8eIm4N6/beeYBV+XqB8BdE32wD7g/WsPzL1bIvze1xc2TDMm/Z4Cr8u8J4KqcmojInQJclX9pwF0UefICcPti1n0OuCr/hgH3KG7dlxG5k4Cr+spHgKty9RXgxoU7TrwXIv/GAFf1kYOA6wg35QX3cVl+/BSYR+Ys4I40WncIcFWuZgC3S7ijxPkQef2YuDdFXv8FjdOdZxAeAAA=";

    private static readonly Lazy<byte[]> SpartaSkillMerchantCatalog = new(
        () => ApplySpartaMerchantCurrency(InflateCatalog(
            SpartaSkillMerchantCatalogGzip,
            expectedLength: 7_696,
            expectedPacketCount: 8,
            expectedCapturedNpcId: 5_032)));

    /// <summary>The captured [Armor] merchant shelf.</summary>
    /// <remarks>
    /// <para>Sparta_037.</para>
    /// <para>
    /// 7 frames, 91 listings,
    /// categories [0, 0, 1, 2, 2, 3, 3], captured currency byte(s)
    /// ['0x4'].
    /// </para>
    /// </remarks>
    private const string SpartaArmorMerchantCatalogGzip =
        "H4sIAAAAAAACCrXXyU4UURjF8aruJmCT7wYFBRr2bF3zBMggiA0NMu5Z8Qa6YyHYgCgvwEYmmZQFDwHKIEPCKzCPJq021QmR07Doc+6ikqpK/e+qfvluMi9R9aXY87xIkf/36r2Pel4KLN/30u8fWvmhVKr61n3mo1FC9x3oDhK6K6D7kdA9A90xQve5f7c7ROj2ge4nQncCdD8Quvug+5nQjYXudpOEbhx0xwndftAdJnSHQHeE0J0E3RrTuPPCNO7Umsadt+GMx9H0fZ1pHKo3jUMNpnHopWkcajSNQ02mceiVaRxqNo1Dr03jUNw0Dg1Ebv4/P2LpR6v5ue1TkMWlNUIXufSD0EUu/SR0kUPrhC5yaIPQRQ5tErrIoS1CFzn0i9BFDm0TusihHUIXObRL6CKH9ghd5FAyOJeFgnPZQY5z16MsDh0SusihI0IXOXRM6CKHTghd5NApoYscOiN0kUPnhC5y6ILQRQ5dErrIoStCFzl0Tegih34Tuv/W/90p07gzbRp3ZkzjTuZcFgrOZbOmceiraRyaM41D86ZxaME0Di2axqEl0zj0zTQOfTeNQ8uE7n3zUDiYh+KFue0TzeJSC6GLXGoldJFLCUIXOdRG6CKH2gld5NAbQhc51EHoIoc6CV3kUBehixzqJnSRQz2ELnKol9BF81CR07jz2GnceeI07mTmoXAwDxU7jUMlTuPQU6dx6JnTOFTqNA6VOY1D5U7jUMxpHKpwGocqCV00D/0BvRIdxbgfAAA=";

    private static readonly Lazy<byte[]> SpartaArmorMerchantCatalog = new(
        () => ApplySpartaMerchantCurrency(InflateCatalog(
            SpartaArmorMerchantCatalogGzip,
            expectedLength: 8_120,
            expectedPacketCount: 7,
            expectedCapturedNpcId: 5_034)));

    /// <summary>The captured [Jewelry] merchant shelf.</summary>
    /// <remarks>
    /// <para>Sparta_038.</para>
    /// <para>
    /// 6 frames, 76 listings,
    /// categories [0, 1, 2, 2, 3, 3], captured currency byte(s)
    /// ['0x4'].
    /// </para>
    /// </remarks>
    private const string SpartaJewelryMerchantCatalogGzip =
        "H4sIAAAAAAACCrXWOVICURSF4W66TeD6BHFEnAdwxiFyA24AxVlzI3cAjmhguQkjtuBCzEyc58gUsaGrLDmUAee+gKruLr4X3b9u3s0kClHLstxGu/RrxcWyiuDYtuV9/+80BYrFhV/P/p8GCW4OuN0E9wq4QwT3C7jDBDdlV7s9BHcXuCME9xK4vQT3BrgJghsLVLt9BDcN3CTB3QNuP8E9B+4AwS0A99opd8h2Q96rbJ33hEv3LIL5yxFc1It9JfeA4KK5PlRyjwgumutjJfeE4KK5ziu5pwQXzfWZknvRUJ7rgBv2XiWD9d0TqbFfTBNctF+MElw01ymCi/aLGYKL9osxgot6MUtw0X4xTnBRL+YILtovJggu6sU8wUX7xSTBRb2YIrhov7gVne7ciU537kWnO1nH73HQe34QnQ49ik6HnkSnQ8+i06EX0enQq+h06E10OvQuOh36EJ0OfYpOh/x9yKnsQ+lQffcEa3RpieCiLi0TXNSlDMFFHVohuKhDqwQXdWiN4KIOrRNc1KENgos6tElwUYe2CC7q0DbBRR3aIbg/568bNjrdiRid7jQbne74+5BT2YeiRqdDLUanQ61Gp0NtRqdD7UanQx1Gp0OdRqdDMaPToS6j06E4wUX70DdQCEeWgBoAAA==";

    private static readonly Lazy<byte[]> SpartaJewelryMerchantCatalog = new(
        () => ApplySpartaMerchantCurrency(InflateCatalog(
            SpartaJewelryMerchantCatalogGzip,
            expectedLength: 6_784,
            expectedPacketCount: 6,
            expectedCapturedNpcId: 5_035)));

    /// <summary>The captured props merchant shelf.</summary>
    /// <remarks>
    /// <para>Sparta_036.</para>
    /// <para>
    /// 4 frames, 31 listings,
    /// categories [0, 1, 2, 3], captured currency byte(s)
    /// ['0x4'].
    /// </para>
    /// </remarks>
    private const string SpartaPropsMerchantCatalogGzip =
        "H4sIAAAAAAACCrvAFK6+UpiBgYGFgxFIMizgZ2D4jwUwMjKwMhABGJn+/0dWyAilF1LBXEks5i6igrkBWMzdRwVzubCYu58K5mpgMfcAFcz9RVq8JdMo3pIpibcKaHpmZGEHCxkp47SHkRh7ZIH2KPhh2mNMBXMXtDFimGtCDfcuZMcw14YK5r5gxgwHWyqYG3AY01w7Kpg74QIzhrn2VDB3QyE3hrkGLJB0x8TCAxZiEaDMHiagPQ8ms2DYY0YFcwXUMcPbnArmYkvPFlQwF1t6zuWn3NwPnkwY5iZTwdwL7JjhG08FcxmwlHcJNDLXT59yc98zYpobQSNzk2hkbokg5eZiK98KGCHlBTMLC1hojRhl9qgB7WHEEo8rqWAutnbMKhqZ22NOubn/sdRTAL1XW7DoCgAA";

    private static readonly Lazy<byte[]> SpartaPropsMerchantCatalog = new(
        () => ApplySpartaMerchantCurrency(InflateCatalog(
            SpartaPropsMerchantCatalogGzip,
            expectedLength: 2_792,
            expectedPacketCount: 4,
            expectedCapturedNpcId: 5_033)));

    /// <summary>The captured pet merchant shelf.</summary>
    /// <remarks>
    /// <para>Sparta_089.</para>
    /// <para>
    /// 8 frames, 87 listings,
    /// categories [0, 0, 1, 1, 1, 2, 2, 2], captured currency byte(s)
    /// ['0x3'].
    /// </para>
    /// </remarks>
    private const string SpartaPetMerchantCatalogGzip =
        "H4sIAAAAAAACCrXZvUvDQBjH8bvYjkqlCOIoSio4axWEgpvgJugk1Vs6in+Bkzj2T3AWh05ODuLkIE4iWtsa36rG+h5ftljSBIUTO+S+GQKXkM/zLL/nDlJMztq1tBCiIyWbd5GyhfD/uKQUwft2V5/l+3PWzzr6qNuAW0ro7gDU7yDUbxbqdwzqN2/A9aTuLhhwK0ndXTTgrqV1d9qAu9mpu//kTUF5U1DeFJQ3BeVNQXmL1e+SjOZxol3+FJQ/BeVPQflTcfJXDPc/Ge5/xzFz3tOsk3f1OmXIPYXcGuQ6kHsJuVeQW4fcG8i9g1wXcu8htwG5D5D7CLlPkPtrHgfrZ6jOqwF3JSs19w1yPch9h9wPyP2k+s3Ed9dXdbdriHF7IbcfcochdwRyc5A7BbkzkFsQ0TxuPZmH6kRz3wrP4ec2U6cCuWeQewu5J5B7DblVyL2A3BfI/YLcUWj/G4fcScjdgNwJyM1l8HkcrEtQnS3I3YbcXcjdg9wDyD2E3DLkOpBbh9wG5Bag89sy5O5D7hHkOpC7I6J53PoZ4UJ1PMj9BtsqtyRoHgAA";

    private static readonly Lazy<byte[]> SpartaPetMerchantCatalog = new(
        () => AppendPetMerchantSteed(ApplySpartaMerchantCurrency(InflateCatalog( // ECONOMY r11: + Greek Steed
            SpartaPetMerchantCatalogGzip,
            expectedLength: 7_784,
            expectedPacketCount: 8,
            expectedCapturedNpcId: 5_086))));

    /// <summary>
    /// Rewrites each captured frame's currency byte to silver.
    /// </summary>
    /// <remarks>
    /// Everything else - length, opcode, npc id, category, listing count,
    /// fresh flag, balance and every 88-byte record - stays as captured.
    /// </remarks>
    private static byte[] ApplySpartaMerchantCurrency(byte[] catalog)
    {
        var offset = 0;
        while (offset < catalog.Length)
        {
            var length = System.Buffers.Binary
                .BinaryPrimitives.ReadUInt16LittleEndian(
                    catalog.AsSpan(offset, sizeof(ushort)));
            if (length < ShopCatalogHeaderBytes ||
                offset + length > catalog.Length)
            {
                throw new InvalidDataException(
                    "Captured Sparta merchant catalogue has an " +
                    "invalid frame.");
            }

            catalog[offset + 9] = SpartaMerchantSilverCurrencyByte;
            offset += length;
        }

        return catalog;
    }

    private static byte[] GetCapturedSpartaMerchantCatalogSource(
        CapitalNpcServiceKind service) =>
        service switch
        {
            CapitalNpcServiceKind.SpartaWarriorEquipmentMerchant => SpartaWarriorEquipmentMerchantCatalog.Value,
            CapitalNpcServiceKind.SpartaScholarEquipmentMerchant => SpartaScholarEquipmentMerchantCatalog.Value,
            CapitalNpcServiceKind.SpartaSkillMerchant => SpartaSkillMerchantCatalog.Value,
            CapitalNpcServiceKind.SpartaArmorMerchant => SpartaArmorMerchantCatalog.Value,
            CapitalNpcServiceKind.SpartaJewelryMerchant => SpartaJewelryMerchantCatalog.Value,
            CapitalNpcServiceKind.SpartaPropsMerchant => SpartaPropsMerchantCatalog.Value,
            CapitalNpcServiceKind.SpartaPetMerchant => SpartaPetMerchantCatalog.Value,
            _ => throw new ArgumentOutOfRangeException(
                nameof(service),
                service,
                "The selected Sparta merchant is not a shop.")
        };
}
