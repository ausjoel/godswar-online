using System.Text;
using System.Text.Json.Nodes;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class ProgressItemChecks
{
    public const string CheckName = "Progress items resolve pinned rewards and keep v7 and v8 receipts verifiable";
    private static ItemTemplateDefinition Item(uint id, string kind, string use, string skill)
    {
        var json = new JsonObject { ["ID"] = id.ToString(), ["Use"] = use, ["Skill"] = skill };
        return new(id, kind, "Test" + id, "Test " + id, 0, [], 1, 200, null, 0, "t.gwo", "1,1", json.ToJsonString());
    }

    public static Task RunAsync()
    {
        var catalog = PinnedItemTemplateCatalog.Create("progress-item-checks", [
            Item(4160, "consume item", "1", "4610"), Item(4164, "consume item", "1", "4614"),
            Item(4199, "consume item", "1", "4629"), Item(4180, "consume item", "1", "4630"),
            Item(4190, "consume item", "1", "4640"), Item(3943, "consume item", "1", "4750"),
            Item(3981, "consume item", "1", "4758"), Item(4170, "consume item", "1", "4620"),
            Item(4173, "consume item", "1", "4623"), Item(4174, "consume item", "1", "5149"),
            Item(4161, "consume item", "1", "4610"), Item(4162, "consume item", "0", "4612"),
            Item(4163, "head", "1", "4613"), Item(4340, "consume item", "1", "4660"),
            Item(4350, "consume item", "1", "4670"), Item(3941, "consume item", "1", "4823")], [], [], []);
        foreach (var (id, kind, amount) in new[] {
            (4160u, ProgressItemKind.Gold, 10), (4164u, ProgressItemKind.BindingGold, 2009),
            (4199u, ProgressItemKind.BindingGold, 50000), (4180u, ProgressItemKind.TalentPoints, 5),
            (4190u, ProgressItemKind.Honor, 20), (3943u, ProgressItemKind.Honor, 88),
            (3981u, ProgressItemKind.ExchangePoint, 888), (4340u, ProgressItemKind.GuildSilver, 20000),
            (4350u, ProgressItemKind.GuildGold, 100), (3941u, ProgressItemKind.Firework, 1) })
        {
            Check.True(ProgressItemPolicy.TryResolve(catalog, id, out var grant), "reviewed item resolves");
            Check.True(kind == grant.Kind, "reward kind");
            Check.Equal(amount, grant.Amount, "reward amount");
        }
        foreach (var id in new uint[] { 4161, 4162, 4163, 4165, 4153, 3989, 4080, 999999 })
            Check.True(!ProgressItemPolicy.TryResolve(catalog, id, out _), "changed skill, non-use, wearable, unknown reward refused");
        Check.True(PlayerExperienceItemPolicy.TryResolveAmount(catalog, 4170, out var stone) && stone == 2000, "EXP Stone 1 pays Power2");
        Check.True(PlayerExperienceItemPolicy.TryResolveAmount(catalog, 4173, out var big) && big == 30000, "EXP Stone 4 pays Power2");
        Check.True(PlayerExperienceItemPolicy.TryResolveAmount(catalog, 4174, out var pill) && pill > 0, "EXP Pill resolves");
        Check.True(!PlayerExperienceItemPolicy.TryResolveAmount(catalog, 4160, out _), "gold bag is not an EXP item");

        var evidence = new ProgressItemEvidence(42, 4160, 3, ProgressItemKind.Gold, 10, int.MaxValue - 10, int.MaxValue, 8, 9);
        Check.True(evidence.IsValid, "exact ceiling is valid");
        Check.True(!(evidence with { PreviousBalance = int.MaxValue - 9 }).IsValid, "overflow rejected");
        Check.True(!(evidence with { NewBalance = 7 }).IsValid, "wrong balance rejected");
        Check.True(!(evidence with { NewRevision = 8 }).IsValid, "non-advancing revision rejected");
        Check.True(!(evidence with { Amount = 0 }).IsValid, "zero reward rejected");
        var receipt = new PetDurableReceipt(CommandFamily.BagItemActivation, PetDurableReceiptStatus.ProgressItemUsed,
            44, 7, 3, -1, 0, 0, 0, 0, false, false, 0, 1, "42", Guid.NewGuid(), ProgressItem: evidence);
        receipt.Validate();
        Check.Throws<InvalidDataException>(() => (receipt with { ProgressItem = null }).Validate(), "success requires settlement proof");
        var payload = PetDurablePersistenceCodec.Encode(receipt);
        Check.Equal((short)8, PetDurablePersistenceCodec.ReadContractVersion(payload), "new receipts are v8");
        Check.Equal(receipt, PetDurablePersistenceCodec.DecodeAndVerify(Encoding.UTF8.GetString(payload),
            PetDurablePersistenceCodec.Hash(payload)), "progress receipt survives storage and replay");

        // Byte-exact shape of a v7 row as live wrote it: no ProgressItem member, version 7.
        var old = receipt with { Status = PetDurableReceiptStatus.ItemNotFound, ProgressItem = null, OutboxEventId = null };
        var v7 = JsonNode.Parse(PetDurablePersistenceCodec.Encode(old))!.AsObject();
        v7.Remove("ProgressItem");
        v7.Remove("GiftPack");
        v7["ContractVersion"] = 7;
        var v7Bytes = Encoding.UTF8.GetBytes(v7.ToJsonString());
        Check.Equal(old, PetDurablePersistenceCodec.DecodeAndVerify(Encoding.UTF8.GetString(v7Bytes),
            PetDurablePersistenceCodec.Hash(v7Bytes)), "existing v7 receipts still verify");

        var pack = new GiftPackOpenEvidence(42, 3951, 3, GiftPackOpenEvidence.Format([(4231u, 25, (short)0), (4011u, 25, (short)1)]));
        Check.True(pack.IsValid, "pack evidence is valid");
        Check.Equal(2, pack.Rewards.Count, "pack evidence keeps every reward");
        Check.True(!(pack with { Contents = "" }).IsValid && !(pack with { Contents = "1:0:0" }).IsValid, "empty or zero pack contents rejected");
        var packReceipt = new PetDurableReceipt(CommandFamily.BagItemActivation, PetDurableReceiptStatus.GiftPackOpened,
            44, 7, 3, -1, 0, 0, 0, 0, false, false, 0, 1, "42", Guid.NewGuid(), GiftPack: pack);
        packReceipt.Validate();
        var packPayload = PetDurablePersistenceCodec.Encode(packReceipt);
        Check.Equal(packReceipt, PetDurablePersistenceCodec.DecodeAndVerify(Encoding.UTF8.GetString(packPayload),
            PetDurablePersistenceCodec.Hash(packPayload)), "gift pack receipt survives storage and replay");
        return Task.CompletedTask;
    }
}
