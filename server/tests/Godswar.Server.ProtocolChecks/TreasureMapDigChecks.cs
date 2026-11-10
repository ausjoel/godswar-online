using System.Buffers.Binary;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// Treasure Map digs: the 96 places from ItemDynamicAttribute.xml, the spot check, the switch default, the stamping
// SQL and the wire word the client reads the Place from (item record offset 32).
internal static class TreasureMapDigChecks
{
    public const string CheckName = "Treasure Map digs: places, spot check, wire word";

    public static Task RunAsync()
    {
        try
        {
            CheckCatalog();
            CheckRegions();
            CheckSpot();
            CheckSwitch();
            CheckStampSql();
            CheckWire();
        }
        finally
        {
            ServerRates.ResetToDefaults();
        }

        return Task.CompletedTask;
    }

    private static void CheckCatalog()
    {
        Check.Equal(96, TreasureMapPlaces.All.Length, "the client file lists 96 dig places");
        Check.Equal(96, TreasureMapPlaces.All.Select(static p => p.PlaceId).Distinct().Count(), "place ids are unique");
        Check.True(TreasureMapPlaces.All.All(static p => p.PlaceId is > 0 and <= short.MaxValue),
            "every place id fits the 16-bit word the client reads");
        Check.True(TreasureMapPlaces.TryGet(10000, out var first) && first == new TreasureMapPlace(10000, 0, -179, 141),
            "Place 10000 = Athens (-179, 141) (Coordinate 20000)");
        Check.True(TreasureMapPlaces.TryGet(12505, out var last) && last == new TreasureMapPlace(12505, 152, 178, 181),
            "Place 12505 = Thermopylae (178, 181) (Coordinate 22505)");
        Check.Equal(16, TreasureMapPlaces.All.Select(static p => p.Region).Distinct().Count(), "places span 16 regions");
        Check.True(TreasureMapPlaces.All.All(static p => p.MapId >= 0), "every place's region has a server map id");
        Check.True(!TreasureMapPlaces.TryGet(0, out _), "an unstamped map (0) has no place");
        Check.True(TreasureMapPlaces.IsTreasureMap(4470) && TreasureMapPlaces.IsTreasureMap(4471) &&
            !TreasureMapPlaces.IsTreasureMap(4472), "4470 / 4471 are the treasure maps, 4472 is not");
    }

    private static void CheckRegions()
    {
        // The file's Map value is the client REGION (QuestRegion.dat), mapped to the server map id through the
        // client's MapIdToNameConfig.ini ("MapID<server> = Name,<region>").
        Check.Equal((short)1, TreasureMapPlaces.ServerMapIdForRegion(0), "region 0 Athens = server map 1");
        Check.Equal((short)2, TreasureMapPlaces.ServerMapIdForRegion(1), "region 1 Athens' Suburb = server map 2");
        Check.Equal((short)11, TreasureMapPlaces.ServerMapIdForRegion(2), "region 2 Marathon = server map 11");
        Check.Equal((short)0, TreasureMapPlaces.ServerMapIdForRegion(50), "region 50 Sparta = server map 0");
        Check.Equal((short)13, TreasureMapPlaces.ServerMapIdForRegion(52), "region 52 Peloponnese = server map 13");
        Check.Equal((short)15, TreasureMapPlaces.ServerMapIdForRegion(100), "region 100 Derveni = server map 15");
        Check.Equal((short)8, TreasureMapPlaces.ServerMapIdForRegion(152), "region 152 Thermopylae = server map 8");
        Check.Equal((short)-1, TreasureMapPlaces.ServerMapIdForRegion(999), "an unknown region has no map");
        Check.True(TreasureMapPlaces.TryGet(10200, out var marathon) && marathon.Region == 2 && marathon.MapId == 11,
            "Place 10200 is in region 2 (Marathon) = server map 11");
        Check.Equal("Athens' Suburb", TreasureMapPlaces.RegionName(1), "region names follow QuestRegion.dat");
    }

    private static void CheckSpot()
    {
        TreasureMapPlaces.TryGet(10000, out var place);
        // Place 10000 = region 0 (Athens) = server map 1.
        Check.True(TreasureMapPlaces.IsAtPlace(place, 1, -179f, 141f), "on the spot (server map 1)");
        Check.True(TreasureMapPlaces.IsAtPlace(place, 1, -170f, 141f), "9 units away (client radius 10)");
        Check.True(TreasureMapPlaces.IsAtPlace(place, 1, -179f, 155.5f), "14.5 units away (server tolerance 15)");
        Check.True(!TreasureMapPlaces.IsAtPlace(place, 1, -179f, 157f), "16 units away is refused");
        Check.True(!TreasureMapPlaces.IsAtPlace(place, 0, -179f, 141f),
            "server map 0 (Sparta) is refused although the file says Map 000 (region 0 = Athens)");
    }

    private static void CheckSwitch()
    {
        ServerRates.ResetToDefaults();
        Check.True(ServerRateCatalog.TryGet(Finalise2RateKeys.TreasureMapDigs, out var definition) &&
            definition.DefaultValue == 1m, "item.treasure_map_digs is a switch that defaults to 1");
        Check.True(Finalise2Rates.On(Finalise2RateKeys.TreasureMapDigs), "digs are on by default");
    }

    private static void CheckStampSql()
    {
        var sql = TreasureMapPlaces.StampSql();
        Check.True(sql.Contains("ARRAY[10000,10001,") && sql.Contains(",12505]::integer[]") && sql.Contains("% 96"),
            "the stamping function picks from all 96 place ids");
        Check.True(sql.Contains("NEW.prop_id IN (4470, 4471) AND NEW.holy_suit_code = 0"),
            "the trigger only touches unstamped treasure maps");
        Check.True(sql.Contains("WHERE prop_id IN (4470, 4471) AND holy_suit_code = 0"),
            "the backfill only touches unstamped treasure maps");
    }

    private static void CheckWire()
    {
        var item = CompactItemEntry.Empty with
        {
            Id = 4471, Quality = 1, Grade = 1, Bound = 0, Stack = 1, HolySuitCode = 12505
        };
        var packet = PacketBuilder.SystemAddItemWithAcquisitionLog(item);
        const int record = 8; // SystemAddItemHeaderLength
        Check.Equal(4471u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(record, 4)), "record item id");
        Check.Equal((ushort)12505, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(record + 32, 2)),
            "the Place id travels in the record word at offset 32 (client +0x50 / +0x20)");
    }
}
