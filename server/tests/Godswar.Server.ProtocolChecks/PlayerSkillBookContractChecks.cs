using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PlayerSkillBookContractChecks
{
    public const string CheckName =
        "Authoritative player skill-book contract";

    public static async Task RunAsync()
    {
        CheckEvidenceAndReceiptContract();
        CheckStockSkillProjectionContract();
        CheckTierReplacementSourceContract();
        await PostgresPetDurableCommandIntegrationChecks
            .RunPlayerSkillBookOnlyAsync();
    }

    private static void CheckEvidenceAndReceiptContract()
    {
        var evidence = Evidence();
        Check.True(evidence.IsValid,
            "player skill evidence accepts a fighter tier");
        Check.True(
            !(evidence with { SkillLevel = 0 }).IsValid &&
            !(evidence with { SkillLevel = 256 }).IsValid,
            "player skill evidence rejects invalid tiers");

        var receipt = new PetDurableReceipt(
            CommandFamily.BagItemActivation,
            PetDurableReceiptStatus.PlayerSkillLearned,
            AccountId: 13,
            CharacterId: 7008,
            KitBagSlot: 0,
            EquipmentSlot: -1,
            PetId: 0,
            PetLevel: 0,
            PetExperience: 0,
            PetRevision: 0,
            IsCarried: false,
            IsSummoned: false,
            PresenceOperation: 0,
            AggregateRevision: 1,
            AuditReference: "player-skill-book-contract",
            OutboxEventId: Guid.NewGuid(),
            PlayerSkillLearn: evidence);
        var payload = PetDurablePersistenceCodec.Encode(receipt);
        Check.True(
            PetDurablePersistenceCodec.ReadContractVersion(payload) ==
                PetDurablePersistenceCodec.BagItemActivationContractVersion &&
            PetDurablePersistenceCodec.BagItemActivationContractVersion == 5 &&
            PetDurablePersistenceCodec.Decode(payload) == receipt,
            "v5 activation round-trips player skill-book evidence");
        var historical = JsonNode.Parse(payload)!.AsObject();
        historical["ContractVersion"] = 4;
        historical.Remove("WonderlandSack");
        historical.Remove("PlayerExperience");
        var legacyPayload = Encoding.UTF8.GetBytes(historical.ToJsonString());
        Check.True(PetDurablePersistenceCodec.DecodeAndVerify(Encoding.UTF8.GetString(legacyPayload),
            PetDurablePersistenceCodec.Hash(legacyPayload)) == receipt,
            "historical v4 activation retains player skill-book evidence");

        Check.Throws<InvalidDataException>(
            () => (receipt with
            {
                Status =
                    PetDurableReceiptStatus.PlayerSkillBookAlreadyLearned,
                OutboxEventId = null
            }).Validate(),
            "a rejected player skill book cannot carry success evidence");
    }

    private static void CheckStockSkillProjectionContract()
    {
        SkillState[] skills =
        [
            new() { SkillId = 50, Level = 1 },
            new() { SkillId = 4904, Level = 1 }
        ];
        var packet = PacketBuilder.ActiveSkillInfo(skills);
        Check.True(
            packet.Length == 28 &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(0, 2)) ==
                packet.Length &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2, 2)) ==
                10_041 &&
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(8, 4)) == 2 &&
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(12, 4)) == 50 &&
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(16, 4)) == 0 &&
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(20, 4)) ==
                4904 &&
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(24, 4)) == 0,
            "stock active-skill refresh is opcode 10041 with padded records");

        var root = FindRepositoryRoot();
        var projection = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Godswar.Server",
            "Game",
            "GameClientHandler.PlayerSkillBooks.Projection.cs"));
        var activeSkillOffset = projection.IndexOf(
            "PacketBuilder.ActiveSkillInfo(skills)",
            StringComparison.Ordinal);
        var slotClearOffset = projection.IndexOf(
            "PacketBuilder.StorageItemKitBagDelete(",
            StringComparison.Ordinal);
        var bagRefreshOffset = projection.IndexOf(
            "await SendKitBagRefreshAsync(cancellationToken)",
            StringComparison.Ordinal);
        Check.True(
            activeSkillOffset >= 0 &&
            !projection.Contains(
                "PacketBuilder.SkillList(skills)",
                StringComparison.Ordinal),
            "skill-book projection emits opcode 10041 only, never title opcode 10196");
        Check.True(
            activeSkillOffset < slotClearOffset &&
            slotClearOffset < bagRefreshOffset,
            "successful skill projection precedes slot clear and authoritative bag refresh");
    }

    private static void CheckTierReplacementSourceContract()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Godswar.Server",
            "Infrastructure",
            "Pets",
            "PostgresPetDurableCommandExecutor.PlayerSkillBooks.cs"));
        Check.True(
            source.Contains(
                "book.SkillLevel is not (>= 1 and <= byte.MaxValue)",
                StringComparison.Ordinal) &&
            source.Contains("FOR UPDATE;", StringComparison.Ordinal),
            "player books reject null tiers and lock learned family state");
        Check.True(
            source.Contains(
                "DELETE FROM public.character_skills",
                StringComparison.Ordinal) &&
            source.Contains(
                "INSERT INTO public.character_skills",
                StringComparison.Ordinal) &&
            source.Contains(
                "family.base_name = @baseName",
                StringComparison.Ordinal) &&
            source.Contains(
                "expectedAffectedRows = book.PreviousSkillId.HasValue ? 2 : 1",
                StringComparison.Ordinal),
            "player tier upgrades replace one family row instead of retaining old tiers");
    }

    private static PlayerSkillLearnEvidence Evidence() => new(
        ItemInstanceId: 70_008,
        ItemTemplateId: 5_019,
        KitBagSlot: 0,
        SkillId: 50,
        SkillLevel: 1,
        PreviousSkillId: null,
        BaseName: "Blood Claw",
        Profession: 0,
        CharacterLevel: 89,
        ItemContentRevision: new string('A', 64),
        GameplayContentRevision: new string('B', 64));

    private static string FindRepositoryRoot()
    {
        var configured = Environment.GetEnvironmentVariable(
            "GODSWAR_REPOSITORY_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) &&
            File.Exists(Path.Combine(
                configured,
                "src",
                "Godswar.Server",
                "Godswar.Server.csproj")))
        {
            return configured;
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "src",
                    "Godswar.Server",
                    "Godswar.Server.csproj")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the GodsWar repository root.");
    }
}
