using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.Characters;
using Godswar.Server.Infrastructure.Items;

namespace Godswar.Server.Game;

internal enum SummonItemKind
{
    SealBox,
    BossScroll
}

/// <summary>One monster a summon item can release: a name to look for among the map's own templates, then a
/// global template (client Monster/Monster.ini, MonsterCon500, loaded on every map) as the fallback.</summary>
internal sealed record SummonMonsterChoice(string Name, string? GlobalTemplateKey, int PillCount);

internal sealed record SummonItemDefinition(
    int ItemId,
    SummonItemKind Kind,
    IReadOnlyList<SummonMonsterChoice> Choices,
    int MinimumLevel,
    byte? Camp,
    IReadOnlySet<short>? Maps,
    string MapText);

/// <summary>
/// ROUND3 runtime spawns (the admin F-31 "Build both", 2026-11-09): seal boxes 14034-14037 and boss scrolls
/// 14060-14065. Item texts (client en_us EquipDescription.dat):
/// <list type="bullet">
/// <item>14035 Centaur / 14036 Ogre / 14037 Persian Soldier Seal Box: "release the sealed X beside you. Killing it
/// drops 99 / 198 / 297 EXP Pills" (Exp Pill = 4174, stack 99, so the drop is 1 / 2 / 3 stacks of 99).</item>
/// <item>14034 Sealing Stone: "a box holding all kinds of sealed monsters; kill the weak monster inside for a lot of
/// EXP. Level 20 and above" (all four boxes carry PlayLv 20,200).</item>
/// <item>14060-14062 Ladon / Cyclops / Medusa Scroll (Camp 1): "Use in the Suburbs of Athens, Marathon or
/// Parnitha"; 14063-14065 (Camp 0): "Use in the Suburbs of Sparta, Peloponnesus or Nemea".</item>
/// </list>
/// The client lists no Ogre in any loaded Monster.ini and Persian Soldier only on Thermopylae (8), so the
/// stand-ins below are GENERATED. Stats, lifetimes and the scroll drop are GENERATED too (no source).
/// </summary>
internal static class SummonItemPolicy
{
    public const int SealingStone = 14034;
    public const int CentaurSealBox = 14035;
    public const int OgreSealBox = 14036;
    public const int PersianSoldierSealBox = 14037;
    public const uint ExperiencePill = ExperiencePillItemContentBaseline.ItemId;
    public const int ExperiencePillStack = 99;

    /// <summary>GENERATED lifetimes: how long an untouched spawn stays before it despawns.</summary>
    public static readonly TimeSpan SealBoxLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan BossScrollLifetime = TimeSpan.FromMinutes(30);

    /// <summary>GENERATED: Exp Pills a scroll boss drops to its killer (the scroll text names no reward).</summary>
    public const int BossScrollPillCount = 30;

    /// <summary>GENERATED: how far beside the player the monster appears.</summary>
    public const float SealBoxOffset = 2.5f;
    public const float BossScrollOffset = 6f;

    private static readonly SummonMonsterChoice Centaur =
        new("Centaur", "A_normal_Trialcentaurwl_001", 99); // global "Centaur (Physical)"
    private static readonly SummonMonsterChoice Ogre =
        new("Ogre", "A_normal_Trialantai_001", 198); // GENERATED stand-in: global "Antaeus" (giant model)
    private static readonly SummonMonsterChoice PersianSoldier =
        new("Persian Soldier", "A_normal_Trialnvwang_001", 297); // GENERATED stand-in: global "Hippolyte"

    private static readonly IReadOnlySet<short> AthensScrollMaps = new HashSet<short> { 2, 3, 11, 12 };
    private static readonly IReadOnlySet<short> SpartaScrollMaps = new HashSet<short> { 4, 5, 13, 14 };
    private const string AthensScrollMapText = "the Suburbs of Athens, Marathon or Parnitha";
    private const string SpartaScrollMapText = "the Suburbs of Sparta, Peloponnesus or Nemea";

    public static IReadOnlyDictionary<int, SummonItemDefinition> Items { get; } =
        new Dictionary<int, SummonItemDefinition>
        {
            // GENERATED: the Sealing Stone releases one of the three sealed monsters at random and pays the
            // smallest box drop (99), so it is never better than a Centaur Seal Box.
            [SealingStone] = new(SealingStone, SummonItemKind.SealBox,
                [Centaur with { PillCount = 99 }, Ogre with { PillCount = 99 }, PersianSoldier with { PillCount = 99 }],
                20, null, null, ""),
            [CentaurSealBox] = new(CentaurSealBox, SummonItemKind.SealBox, [Centaur], 20, null, null, ""),
            [OgreSealBox] = new(OgreSealBox, SummonItemKind.SealBox, [Ogre], 20, null, null, ""),
            [PersianSoldierSealBox] = new(PersianSoldierSealBox, SummonItemKind.SealBox, [PersianSoldier], 20,
                null, null, ""),
            [14060] = Scroll(14060, "Ladon", "A_boss_baitoulong_001", FactionPortalSkillPolicy.AthensCamp),
            [14061] = Scroll(14061, "Cyclops", "A_boss_Cyclopkuke_001", FactionPortalSkillPolicy.AthensCamp),
            [14062] = Scroll(14062, "Medusa", "A_boss_medusamo_001", FactionPortalSkillPolicy.AthensCamp),
            [14063] = Scroll(14063, "Ladon", "A_boss_baitoulong_001", FactionPortalSkillPolicy.SpartaCamp),
            [14064] = Scroll(14064, "Cyclops", "A_boss_Cyclopkuke_001", FactionPortalSkillPolicy.SpartaCamp),
            [14065] = Scroll(14065, "Medusa", "A_boss_medusamo_001", FactionPortalSkillPolicy.SpartaCamp),
        };

    private static SummonItemDefinition Scroll(int itemId, string name, string templateKey, byte camp) =>
        new(itemId, SummonItemKind.BossScroll, [new SummonMonsterChoice(name, templateKey, BossScrollPillCount)],
            1, camp,
            camp == FactionPortalSkillPolicy.AthensCamp ? AthensScrollMaps : SpartaScrollMaps,
            camp == FactionPortalSkillPolicy.AthensCamp ? AthensScrollMapText : SpartaScrollMapText);

    public static bool TryGet(int itemId, out SummonItemDefinition definition) =>
        Items.TryGetValue(itemId, out definition!);

    /// <summary>Splits a drop into stacks the bag can hold: never more than 99 in one line.</summary>
    public static IReadOnlyList<WorldRuntimeSpawnDrop> SplitStacks(uint itemId, int total,
        int stack = ExperiencePillStack)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stack);
        var drops = new List<WorldRuntimeSpawnDrop>();
        for (var left = total; left > 0; left -= stack)
        {
            drops.Add(new WorldRuntimeSpawnDrop(itemId, Math.Min(stack, left)));
        }
        return drops;
    }

    /// <summary>
    /// The template a choice releases on <paramref name="mapId"/>: a non-pet, non-boss template of this map whose
    /// display name is the monster's name, then one that contains it (normal rank first), then the global
    /// fallback. Null when the client could not draw any of them here.
    /// </summary>
    public static GameplayMonsterTemplateDefinition? ResolveTemplate(
        SummonMonsterChoice choice,
        IEnumerable<GameplayMonsterTemplateDefinition> templates,
        short mapId,
        SummonItemKind kind)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(templates);
        var all = templates as IReadOnlyList<GameplayMonsterTemplateDefinition> ?? templates.ToArray();
        if (kind == SummonItemKind.SealBox)
        {
            var local = all.Where(template => template.SourceMapId == mapId && !template.IsPet && !template.IsBoss)
                .ToArray();
            var match = local
                .Where(template => string.Equals(template.DisplayName, choice.Name, StringComparison.OrdinalIgnoreCase))
                .Concat(local.Where(template => template.DisplayName.Contains(choice.Name, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(template => template.IsElite)
                    .ThenBy(template => template.TemplateKey, StringComparer.Ordinal))
                .FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
        }

        return choice.GlobalTemplateKey is null
            ? null
            : all.FirstOrDefault(template => template.SourceMapId is null &&
                string.Equals(template.TemplateKey, choice.GlobalTemplateKey, StringComparison.Ordinal));
    }

    /// <summary>GENERATED stats: tier = the user's level; a "weak" seal monster, a tough scroll boss.</summary>
    public static (uint Level, uint MaximumHealth) Stats(SummonItemKind kind, int playerLevel)
    {
        var level = (uint)Math.Clamp(playerLevel, 1, 200);
        return kind == SummonItemKind.SealBox
            ? (level, 20u * level + 100u)
            : (level, 3_000u * level + 20_000u);
    }
}

/// <summary>
/// ROUND3 runtime spawns: the 10020 appearance packet of a runtime spawn. With a published spawn of the same
/// template on the map, its whole packet is reused (the un-reversed appearance tail after offset 44 included);
/// otherwise the 108-byte Atlantis layout is written. Byte 6 must carry the map id or the client hides the mob.
/// </summary>
internal static class WorldRuntimeSpawnPackets
{
    private const int MinimumPacketLength = 108;
    private const ushort AppearanceOpcode = 10020;

    /// <param name="camp">ROUND3 summons: 0 Sparta / 1 Athens for an allied spawn, written to byte 5 on both
    /// paths (a reused published packet would otherwise keep its own monster's camp); null keeps the packet's.</param>
    public static byte[] Build(byte[]? publishedTemplatePacket, short mapId, uint objectId, string templateKey,
        uint level, uint maximumHealth, float x, float y, float z, float facing, byte? camp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateKey);
        byte[] packet;
        if (publishedTemplatePacket is { Length: >= MinimumPacketLength })
        {
            packet = publishedTemplatePacket.ToArray();
        }
        else
        {
            packet = new byte[MinimumPacketLength];
            var templateBytes = Encoding.ASCII.GetBytes(templateKey);
            if (templateBytes.Length >= packet.Length - 44)
            {
                throw new InvalidDataException("Runtime spawn template does not fit its appearance packet.");
            }
            BinaryPrimitives.WriteUInt16LittleEndian(packet, checked((ushort)packet.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), AppearanceOpcode);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 0x212); // monster, faction nobody
            templateBytes.CopyTo(packet.AsSpan(44));
        }

        if (camp is { } faction)
        {
            if (faction > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(camp), "An allied spawn camp is 0 (Sparta) or 1 (Athens).");
            }
            packet[5] = faction;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), checked((ushort)mapId));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), Math.Max(level, 1u));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20), Math.Max(maximumHealth, 1u));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(24), Math.Max(maximumHealth, 1u));
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(28), x);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(32), y);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(36), z);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(40), facing);
        return packet;
    }
}
