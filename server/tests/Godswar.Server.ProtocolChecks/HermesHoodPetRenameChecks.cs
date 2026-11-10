using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>ITEMS-HERMES: Hermes Hood per-viewer camp (F-56) and the pet rename / Renaming Stone wire (F-55).</summary>
internal static class HermesHoodPetRenameChecks
{
    public const string CheckName = "ITEMS-HERMES Hermes Hood camp per viewer and pet rename (10242)";

    public static Task RunAsync()
    {
        ServerRates.ResetToDefaults();
        Check.True(Finalise2Rates.On(Finalise2RateKeys.HermesHood), "item.hermes_hood defaults to on");
        Check.True(Finalise2Rates.On(Finalise2RateKeys.PetRename), "item.pet_rename defaults to on");
        Check.True(ServerRateCatalog.TryGet(Finalise2RateKeys.HermesHood, out _) &&
                   ServerRateCatalog.TryGet(Finalise2RateKeys.PetRename, out _), "both switches are in the catalog");
        CheckHood();
        CheckRename();
        return Task.CompletedTask;
    }

    private static void CheckHood()
    {
        var wearer = new GameCharacter
        {
            Id = 913_001,
            AccountId = 99,
            Name = "HoodWearer",
            Profession = 1,
            Level = 60,
            Camp = GameDefaults.SpartaCamp,
            Equipment = GameDefaults.DefaultEquipment(1)
        };
        const uint objectId = 0x7135_B24E;

        // No hood: every viewer sees the real camp and the frames are unchanged.
        var plain = PacketBuilder.PlayerWorldSpawn(wearer, objectId);
        var plainForAthens = PacketBuilder.PlayerWorldSpawn(wearer, objectId, viewerCamp: GameDefaults.AthensCamp);
        Check.True(plain.SequenceEqual(plainForAthens), "without a hood the viewer camp changes nothing");
        Check.Equal(GameDefaults.SpartaCamp, plain[53], "spawn byte 53 is the real camp");

        var copy = PacketBuilder.WithPlayerWorldSpawnCamp(plain, GameDefaults.AthensCamp);
        Check.True(copy[53] == GameDefaults.AthensCamp && plain[53] == GameDefaults.SpartaCamp &&
                   copy.Where((b, i) => i != 53 && b != plain[i]).Count() == 0,
            "per-recipient spawn copy changes byte 53 only and leaves the shared frame alone");

        HermesHoods.Set(wearer.Id, DateTimeOffset.UtcNow + HermesHoods.Lifetime);
        try
        {
            var athensSpawn = PacketBuilder.PlayerWorldSpawn(wearer, objectId, viewerCamp: GameDefaults.AthensCamp);
            var spartaSpawn = PacketBuilder.PlayerWorldSpawn(wearer, objectId, viewerCamp: GameDefaults.SpartaCamp);
            Check.Equal(GameDefaults.AthensCamp, athensSpawn[53], "hooded: an Athenian sees an Athenian");
            Check.Equal(GameDefaults.SpartaCamp, spartaSpawn[53], "hooded: a Spartan still sees a Spartan");
            Check.True(athensSpawn.Where((b, i) => i != 53 && b != plain[i]).Count() == 0,
                "the hood changes only spawn byte 53");
            Check.Equal(GameDefaults.SpartaCamp, PacketBuilder.PlayerWorldSpawn(wearer, objectId)[53],
                "no viewer camp (a shared frame): the real camp");

            var status = PacketBuilder.RemotePlayerStatusUpdate(wearer, objectId, ClientStatusAggregate.Empty, null,
                GameDefaults.AthensCamp);
            Check.Equal(GameDefaults.AthensCamp, status[62], "hooded: remote status byte 62 is the viewer's camp");
            var bundle = PacketBuilder.PlayerInspectEquipmentRemoteStatusBundle(wearer, objectId,
                ClientStatusAggregate.Empty, null, GameDefaults.AthensCamp);
            var statusStart = bundle.Length - status.Length;
            Check.Equal(GameDefaults.AthensCamp, bundle[statusStart + 62], "the inspect bundle's status frame too");
            var local = PacketBuilder.PlayerStatusUpdate(wearer, ClientStatusAggregate.Empty);
            Check.Equal(GameDefaults.SpartaCamp, local[62], "the wearer's own status keeps the real camp");

            ServerRates.Install(ServerRatesSnapshot.Create(
                [KeyValuePair.Create(Finalise2RateKeys.HermesHood, 0m)], 1, DateTimeOffset.UtcNow, "check", "test"));
            Check.Equal(GameDefaults.SpartaCamp,
                PacketBuilder.PlayerWorldSpawn(wearer, objectId, viewerCamp: GameDefaults.AthensCamp)[53],
                "switch at 0: active hoods stop showing");
        }
        finally
        {
            ServerRates.ResetToDefaults();
            HermesHoods.Set(wearer.Id, DateTimeOffset.UtcNow.AddMinutes(-1));
        }

        Check.True(!HermesHoods.IsActive(wearer.Id), "an expired hood is inactive");

        // F-156: an attack ends the hood early; Clear reports whether one was active.
        HermesHoods.Set(wearer.Id, DateTimeOffset.UtcNow + HermesHoods.Lifetime);
        Check.True(HermesHoods.IsActive(wearer.Id) && HermesHoods.Clear(wearer.Id) && !HermesHoods.IsActive(wearer.Id),
            "Clear ends an active hood");
        Check.True(!HermesHoods.Clear(wearer.Id), "Clear on no hood is a no-op");
        Check.Equal(GameDefaults.SpartaCamp,
            PacketBuilder.PlayerWorldSpawn(wearer, objectId, viewerCamp: GameDefaults.AthensCamp)[53],
            "after Clear the re-announce carries the real camp");
    }

    private static void CheckRename()
    {
        var frame = new byte[PetRenamePolicy.RequestLength];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, 0x28);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), Opcodes.PetRenameRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), 1234);
        Encoding.ASCII.GetBytes("Fluffy").CopyTo(frame, 8);
        Check.Equal((ushort)0x2802, Opcodes.PetRenameRequest, "rename request is 10242 (client 0x69c748)");
        Check.True(PetRenamePolicy.TryParseRequest(frame, out var petId, out var raw) && petId == 1234 &&
                   Encoding.ASCII.GetString(raw) == "Fluffy", "40-byte frame: pet id at 4, name at 8");
        Check.True(!PetRenamePolicy.TryParseRequest(frame.AsSpan(0, 39), out _, out _), "short frame refused");

        Check.True(PetRenamePolicy.TryValidateName(Encoding.ASCII.GetBytes("Fluffy7"), out var name) &&
                   name == "Fluffy7", "letters and digits pass");
        Check.True(!PetRenamePolicy.TryValidateName([], out _), "empty name refused");
        Check.True(!PetRenamePolicy.TryValidateName(Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOPQ"), out _),
            "17 characters refused");
        Check.True(PetRenamePolicy.TryValidateName(Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOP"), out _),
            "16 characters pass");
        Check.True(!PetRenamePolicy.TryValidateName(Encoding.ASCII.GetBytes("Fl uffy"), out _), "space refused");
        Check.True(!PetRenamePolicy.TryValidateName([0xC4, 0xE3], out _), "non-ASCII refused");
        // F-156 (the admin): swear words and slurs only. GM/Admin-ish names, soft toilet words and innocent words that
        // hide a short stem all pass; the list's words are refused in any case and anywhere in the name.
        foreach (var fine in new[] { "xGMx", "MyAdmin", "Sigma", "Dogma", "IGGy", "GWfan", "Poo", "Fart", "Butt",
                     "Pooface", "Custard", "Peacock", "Raccoon", "Grape", "Titan", "Bass", "Cucumber", "Spice",
                     "Dickens", "Moron", "Swanky" })
        {
            Check.True(PetRenamePolicy.TryValidateName(Encoding.ASCII.GetBytes(fine), out var ok) && ok == fine,
                $"F-156: {fine} passes");
        }
        foreach (var bad in new[] { "shit", "SHIT", "xShitx", "Fucker", "MrBitch", "Bastard9", "FAGGOT", "Nigga", "Wanker" })
        {
            Check.True(!PetRenamePolicy.TryValidateName(Encoding.ASCII.GetBytes(bad), out _), $"F-156: {bad} refused");
        }
        Check.True(NameFilter.Words.Count is > 10 and < 100 &&
                   NameFilter.Words.All(static w => w.Length >= 4 && w == w.ToLowerInvariant()) &&
                   !NameFilter.Words.Any(static w => w is "gm" or "admin" or "poo" or "fart" or "butt"),
            "name-filter.txt: embedded, lower case, no GM/Admin or toilet words, no stem shorter than 4");
        Check.True(NameFilter.Parse("# c\r\nFuck\r\n\r\nfuck\nslut ").SequenceEqual(["fuck", "slut"]),
            "list parser: comments, blanks, CRLF, case and duplicates");

        // Code 9 stays defined (stock client value) but the server no longer sends it (F-156: no box).
        var success = PacketBuilder.PetOperationResult(1234, PetOperationResultCode.RenameSucceeded);
        Check.True(success.SequenceEqual(new byte[] { 9, 0, 0x04, 0x28, 0xD2, 0x04, 0, 0, 9 }),
            "rename success layout: 10244 [pet id][9]");
        Check.Equal((byte)8, PacketBuilder.PetOperationResult(1, PetOperationResultCode.RenameFailedAlreadyRenamed)[8],
            "needs a stone: PetOperator8");
        Check.Equal((byte)12, PacketBuilder.PetOperationResult(1, PetOperationResultCode.RenameFailedInvalidName)[8],
            "bad name: PetOperator12");
    }
}
