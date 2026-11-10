using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// FINALISE-PETS (the admin 2026-11-05): a capturable pet takes exactly one damage per hit ("-1") and each hit
/// costs it a tenth of its health, so about ten hits bring it down whatever the attacker's level.
/// </summary>
/// <remarks>
/// <para>
/// The client draws a monster's health bar from the appearance packet's current/maximum words and then
/// subtracts the number each damage frame shows (<c>PacketBuilder.PhysicalDamage</c> carries no health
/// field). "Shows 1" and "loses 10%" therefore only agree when the pet has 10 health, which is already
/// the existing convention for the Medusa Baby Rock Elf (<c>MedusaIslandAmbientSpawnPolicy</c>,
/// MaximumHealth 10) and the Atlantis Merman/Siren (<c>PrepareAtlantisPetDefinitions</c>, health 10).
/// The farm dogs and the wild Flower Pixie / Minotaur babies are normalised to the same 10 here
/// (<see cref="NormalizeSpawn"/>), and every hit on any of them resolves to 1.
/// </para>
/// <para>
/// Targets: Lelantine Farm dogs (map 42, <see cref="LelantineFarmDogPolicy"/>), wild baby pets while
/// <c>pet.wild_capture</c> is on (<see cref="WildPetCapturePolicy"/>), the Medusa Baby Rock Elf (map 200)
/// and the two Atlantis pets (map 205). <c>GODSWAR_PET_CAPTURE_TEN_HITS=0</c> (read at start-up) restores
/// the old behaviour (normal damage, template health).
/// </para>
/// </remarks>
internal static class CapturablePetCombatPolicy
{
    public const string DisableVariable = "GODSWAR_PET_CAPTURE_TEN_HITS";

    /// <summary>The health every capturable pet has (ten hits of one).</summary>
    public const uint Health = 10;

    /// <summary>The damage every hit on a capturable pet resolves to.</summary>
    public const uint DamagePerHit = 1;

    private const int CurrentHealthOffset = 20;
    private const int MaximumHealthOffset = 24;

    public static bool Enabled { get; set; } =
        Environment.GetEnvironmentVariable(DisableVariable) != "0";

    /// <summary>Whether a monster on this map is one of the capturable pets.</summary>
    public static bool IsCaptureTarget(int mapId, string? templateKey, uint objectId = 0)
    {
        if (!Enabled || string.IsNullOrEmpty(templateKey))
        {
            return false;
        }

        return mapId switch
        {
            LelantineFarmProtocol.MapId => LelantineFarmDogPolicy.TryGetTier(templateKey, out _),
            200 => string.Equals(templateKey, MedusaIslandAmbientSpawnPolicy.BabyRockElfTemplateKey,
                StringComparison.Ordinal),
            205 => (!AtlantisPetSpawnPolicy.MermaidGivesDew ||
                    templateKey != AtlantisPetSpawnPolicy.MermaidTemplateKey) &&
                (objectId == 0
                    ? templateKey is AtlantisPetSpawnPolicy.MermaidTemplateKey or AtlantisPetSpawnPolicy.SirenTemplateKey
                    : AtlantisPetSpawnPolicy.Matches(objectId, templateKey)),
            _ => WildPetCapturePolicy.Enabled && WildPetCapturePolicy.IsWildPetTemplate(mapId, templateKey)
        };
    }

    public static bool IsCaptureTarget(CapturedMonsterSpawn definition) =>
        definition is not null && IsCaptureTarget(definition.MapId, definition.TemplateKey, definition.ObjectId);

    /// <summary>The damage a hit actually applies: 1 on a capturable pet, otherwise unchanged.</summary>
    public static uint ClampDamage(CapturedMonsterSpawn definition, uint damage) =>
        damage > DamagePerHit && IsCaptureTarget(definition) ? DamagePerHit : damage;

    /// <summary>
    /// A capturable pet's appearance with its current and maximum health set to <see cref="Health"/>;
    /// any other spawn is returned unchanged. The runtime reads its health from the same bytes, so the
    /// server and the client agree on ten hits.
    /// </summary>
    public static CapturedMonsterSpawn NormalizeSpawn(CapturedMonsterSpawn spawn)
    {
        if (spawn?.Packet is not { Length: >= MaximumHealthOffset + 4 } packet || !IsCaptureTarget(spawn))
        {
            return spawn!;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(CurrentHealthOffset)) == Health &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(MaximumHealthOffset)) == Health)
        {
            return spawn;
        }

        var copy = packet.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(CurrentHealthOffset), Health);
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(MaximumHealthOffset), Health);
        return spawn with { Packet = copy };
    }
}
