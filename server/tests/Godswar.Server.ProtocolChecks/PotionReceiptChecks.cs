using System.Text;
using System.Text.Json.Nodes;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PotionReceiptChecks
{
    public const string CheckName = "Potion receipts prove exact vitals settlement and retain v5 v6 replay compatibility";
    public static Task RunAsync()
    {
        var before = new PotionVitalsSnapshot(54000, 9900, 55000, 10000, 12);
        Check.True(BagConsumableEffectCatalog.TryResolve(3100, out var effect), "reviewed HP effect");
        Check.True(PotionUsePolicy.TryPlan(before, effect, out var plan), "valid potion plan");
        var owner = new PlayerOwnershipFence(Guid.NewGuid(), 2);
        var evidence = new PotionUseEvidence(42, 4000, 3, 3100, 200, before, plan!.PersistedVitals, owner);
        Check.True(evidence.IsValid && evidence.Matches(plan, 4000, owner), "receipt matches pending owning-session plan");
        Check.True(!evidence.Matches(plan, 4001, owner), "different source template cannot settle a pending plan");
        Check.True(!evidence.Matches(plan, 4000, owner with { Generation = 3 }), "different owner cannot apply historical heal");
        foreach (var invalid in new[] { evidence with { AppliedAmount = 201 }, evidence with { SkillId = 4600 },
            evidence with { After = evidence.After with { Hp = 54201 } },
            evidence with { After = evidence.After with { Revision = 12 } },
            evidence with { Before = before with { MaximumHp = 1500 } },
            evidence with { Ownership = default }, evidence with { KitBagSlot = 96 } })
            Check.True(!invalid.IsValid, "forged, overflowing, base-only and unfenced evidence rejected");
        var receipt = new PetDurableReceipt(CommandFamily.BagItemActivation, PetDurableReceiptStatus.PotionUsed,
            44, 7, 3, -1, 0, 0, 0, 0, false, false, 0, 1, "42", Guid.NewGuid(), Potion: evidence);
        receipt.Validate();
        var bytes = PetDurablePersistenceCodec.Encode(receipt);
        Check.Equal(receipt, PetDurablePersistenceCodec.DecodeAndVerify(Encoding.UTF8.GetString(bytes),
            PetDurablePersistenceCodec.Hash(bytes)), "v7 potion proof round trips with canonical hash");
        Check.Throws<InvalidDataException>(() => (receipt with { Potion = null }).Validate(), "success needs potion proof");
        Check.Throws<InvalidDataException>(() => (receipt with { Potion = evidence with { KitBagSlot = 4 } }).Validate(),
            "receipt cannot name a different consumed slot");
        var oldMoney = receipt with { Status = PetDurableReceiptStatus.MoneyBagOpened, Potion = null,
            MoneyBag = new(43, 4150, 3, 4600, 10000, 0, 10000, 1, 2) };
        foreach (var version in new[] { 5, 6 })
        {
            var legacyReceipt = version == 6 ? oldMoney : oldMoney with
                { Status = PetDurableReceiptStatus.ItemNotFound, MoneyBag = null, OutboxEventId = null };
            var legacy = JsonNode.Parse(PetDurablePersistenceCodec.Encode(legacyReceipt))!.AsObject();
            legacy.Remove("Potion");
            legacy.Remove("ProgressItem");
            legacy.Remove("GiftPack");
            if (version == 5) legacy.Remove("MoneyBag");
            legacy["ContractVersion"] = version;
            var payload = Encoding.UTF8.GetBytes(legacy.ToJsonString());
            Check.Equal(legacyReceipt, PetDurablePersistenceCodec.DecodeAndVerify(Encoding.UTF8.GetString(payload),
                PetDurablePersistenceCodec.Hash(payload)), "historical v" + version + " retains its exact canonical fields");
            Check.True(PetDurablePersistenceCodec.IsSupportedContractVersion(CommandFamily.BagItemActivation,
                checked((short)version)), "historical version remains accepted by inbox replay");
        }
        return Task.CompletedTask;
    }
}
