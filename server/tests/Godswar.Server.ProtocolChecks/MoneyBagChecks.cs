using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class MoneyBagChecks
{
    public const string CheckName = "Money bags resolve pinned native rewards and preserve valid replay evidence";
    private static ItemTemplateDefinition Item(uint id, string kind, string stats)
    {
        var json=JsonNode.Parse(stats)!.AsObject();json["ID"]=id.ToString();
        return new(id,kind,"Test"+id,"Test "+id,(short)(kind=="head"?12:0),[],1,200,null,
            kind=="head"?12:0,"t.gwo","1,1",json.ToJsonString());
    }
    public static Task RunAsync()
    {
        var catalog = PinnedItemTemplateCatalog.Create("money-bag-checks", [
            Item(4150,"consume item","""{"Use":"1","Skill":"4600"}"""),
            Item(4151,"consume item","""{"Use":1,"Skill":4601}"""),
            Item(4152,"consume item","""{"Use":"1","Skill":"4602"}"""),
            Item(4990,"head","""{"Use":"1","Skill":"4600"}"""),
            Item(4991,"consume item","""{"Use":"0","Skill":"4600"}"""),
            Item(4992,"consume item","""{"Use":"1","Skill":"3100"}""")], [], [], []);
        foreach(var pair in new[] {(4150u,10000),(4151u,100000),(4152u,500000)})
        {
            Check.True(MoneyBagPolicy.TryResolve(catalog,pair.Item1,out var e),"reviewed money item resolves");
            Check.Equal(pair.Item2,e.Amount,"native money reward amount");
        }
        foreach(var id in new uint[]{4990,4991,4992,999999})
            Check.True(!MoneyBagPolicy.TryResolve(catalog,id,out _),"non-use, wearable, potion and absent template rejected");
        var evidence = new MoneyBagOpenEvidence(42,4150,3,4600,10000,int.MaxValue-10000,int.MaxValue,8,9);
        Check.True(evidence.IsValid,"exact wallet ceiling is valid");
        Check.True(!(evidence with {PreviousSilver=int.MaxValue}).IsValid,"wallet overflow evidence rejected");
        Check.True(!(evidence with {Amount=9999}).IsValid,"invented reward rejected");
        Check.True(!(evidence with {NewWalletRevision=8}).IsValid,"non-advancing wallet revision rejected");
        Check.True(!(evidence with {KitBagSlot=96}).IsValid,"out-of-range source slot rejected");
        var receipt = new PetDurableReceipt(CommandFamily.BagItemActivation,PetDurableReceiptStatus.MoneyBagOpened,
            44,7,3,-1,0,0,0,0,false,false,0,1,"42",Guid.NewGuid(),MoneyBag:evidence);
        receipt.Validate();
        var payload = PetDurablePersistenceCodec.Encode(receipt);
        Check.Equal(receipt,PetDurablePersistenceCodec.DecodeAndVerify(Encoding.UTF8.GetString(payload),
            PetDurablePersistenceCodec.Hash(payload)),"money receipt survives storage/replay with exact evidence");
        Check.Throws<InvalidDataException>(()=>(receipt with{MoneyBag=null}).Validate(),"success requires settlement proof");
        var old = receipt with {Status=PetDurableReceiptStatus.ItemNotFound,MoneyBag=null,OutboxEventId=null};
        var legacy = JsonNode.Parse(PetDurablePersistenceCodec.Encode(old))!.AsObject();legacy.Remove("MoneyBag");legacy.Remove("Potion");legacy.Remove("ProgressItem");legacy.Remove("GiftPack");legacy["ContractVersion"]=5;
        var legacyBytes=Encoding.UTF8.GetBytes(legacy.ToJsonString());
        Check.Equal(old,PetDurablePersistenceCodec.DecodeAndVerify(Encoding.UTF8.GetString(legacyBytes),
            PetDurablePersistenceCodec.Hash(legacyBytes)),"old v5 payload without money member remains readable");
        Check.Equal("10007428190000002502000010270000",Convert.ToHexString(PacketBuilder.BagSilverGrant(549,10000)).ToLowerInvariant(),
            "silver grant matches captured frame145492");
        var item=default(CompactItemEntry) with {Id=4150,Quality=1,Grade=1,Bound=1,Stack=1};
        var bag=KitBagSlots.SetSlot(GameDefaults.EmptyKitBag,3,item.ToCompactString());
        Check.Equal(4150u,KitBagSlots.GetItemId(bag,3),"activation fixture contains the real item");
        // The compact serializer is covered separately; compare the new overload against the established snapshot builder.
        var ch=new GameCharacter { KitBag=bag };
        var a=PacketBuilder.BagConsumableActivation(bag,3,549);
        Check.True(a.SequenceEqual(PacketBuilder.BagConsumableActivation(ch,3,549)),"pre-consumption activation uses established native snapshot layout");
        Check.Equal((ushort)10051,BinaryPrimitives.ReadUInt16LittleEndian(a.AsSpan(2)),"native activation opcode");
        Check.Equal(92,a.Length,"native activation frame length");
        Check.Equal((ushort)92,BinaryPrimitives.ReadUInt16LittleEndian(a),"native declared length");
        Check.Equal(549u,BinaryPrimitives.ReadUInt32LittleEndian(a.AsSpan(4)),"native activation recipient");
        Check.Equal((ushort)0,BinaryPrimitives.ReadUInt16LittleEndian(a.AsSpan(12)),"native bag page");
        Check.Equal((ushort)3,BinaryPrimitives.ReadUInt16LittleEndian(a.AsSpan(14)),"native bag index");
        Check.Equal(4150u,BinaryPrimitives.ReadUInt32LittleEndian(a.AsSpan(20)),"native item identity");
        return Task.CompletedTask;
    }
}
