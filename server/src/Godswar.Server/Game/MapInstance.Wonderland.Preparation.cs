using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Godswar.Server.Application.World;
using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private static CapturedMonsterSpawn[][] PrepareWonderlandDefinitions(GameplayContentCatalog content,
        IReadOnlyList<ImmutableArray<WonderlandSpawnPolicy>> stages) => stages.Select(stage => stage.Select(policy =>
    {
        var candidates = content.MonsterTemplates.Where(t => t.SourceMapId == WonderlandEncounterPolicy.Map &&
            string.Equals(t.TemplateKey, policy.TemplateKey, StringComparison.Ordinal)).ToArray();
        // The captured hostile Demonic Assaulter reuses the client's pet-marked
        // bird model (Monster=1). Its exact stage/role/template is deliberate;
        // unrelated pet templates still cannot enter the hostile roster.
        var capturedHostilePetModel = policy.Stage == 1 && policy.Role == WonderlandMonsterRole.DemonicAssaulter &&
            policy.TemplateKey == "B_normalg_famale_001";
        if (candidates.Length != 1 || (candidates[0].IsPet && !capturedHostilePetModel) ||
            candidates[0].Rank != (policy.IsBoss ? "boss" : "normal"))
            throw new InvalidDataException($"Wonderland requires one published map-207 template '{policy.TemplateKey}' of its declared rank.");
        var template = candidates[0];
        var packet = new byte[108];
        var key = Encoding.ASCII.GetBytes(template.TemplateKey);
        if (key.Length >= 64) throw new InvalidDataException("Wonderland template key exceeds its native appearance field.");
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 108);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), 10020);
        // Native 10020 byte 4 is the monster discriminator; byte 5 is its
        // camp, copied to actor+0x292 and compared with the local player's
        // camp for friendly presentation. Unaffiliated enemies retain 2.
        packet[4] = 0x12;
        packet[5] = policy.IsAllied
            ? policy.FactionCamp ?? throw new InvalidDataException("An allied Wonderland actor requires its party camp.")
            : (byte)2;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), WonderlandEncounterPolicy.Map);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), policy.ObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), checked((uint)policy.Stats.Level));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20), policy.Stats.MaximumHealth);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(24), policy.Stats.MaximumHealth);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(28), policy.Position.X);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(32), policy.Position.Y);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(36), policy.Position.Z);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(40), 1f);
        key.CopyTo(packet.AsSpan(44));
        var definition = new CapturedMonsterSpawn(WonderlandEncounterPolicy.Map,
            WonderlandEncounterPolicy.SceneKey, template.TemplateKey, template.DisplayName,
            policy.ObjectId, policy.Position.X, policy.Position.Z, packet);
        definition.Validate(WonderlandEncounterPolicy.Map);
        return definition;
    }).ToArray()).ToArray();
}
