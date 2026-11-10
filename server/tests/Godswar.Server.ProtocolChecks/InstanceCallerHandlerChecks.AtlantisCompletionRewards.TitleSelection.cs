using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static async Task CheckAtlantisOwnedTitlePreservesSelectionAsync()
    {
        await using var fixture = await CreateAtlantisOpalFixtureAsync(null, null, partySize: 2);
        var registry = fixture.Leader.Registry;
        var store = new ScriptedAtlantisCompletionRewards(fixture.Characters)
        {
            LoseFirstCommitAcknowledgement = true
        };
        registry.ConfigureAtlantisCompletionRewards(store);
        var (runtime, initial) = await PrepareAtlantisDepartureAsync(fixture);
        var characters = fixture.Characters.ToArray();
        uint[] selectedTitles = [0, 5009];
        for (var index = 0; index < characters.Length; index++)
        {
            characters[index].SelectedTitleId = selectedTitles[index];
            if (selectedTitles[index] != 0) characters[index].AddOwnedTitle(selectedTitles[index]);
        }
        var titlesBefore = characters.Select(character => character.OwnedTitleIds.ToArray()).ToArray();
        var honorBefore = characters.Select(character => character.MedusaHonorPoints).ToArray();
        var packetsBefore = AtlantisPacketCounts(fixture);
        var completed = CompleteAtlantisRewardRun(runtime, initial);
        var at = completed.TerminalAt!.Value;

        await registry.AdvanceMonsterWorldOnceAsync(at.AddSeconds(1), CancellationToken.None);
        Check.True(store.CommitCount == 1 && store.DuplicateReceiptCount == 0 &&
            characters.Select(character => character.SelectedTitleId).SequenceEqual(selectedTitles),
            "a lost completion acknowledgement preserves both an empty and an existing title selection");

        await registry.AdvanceMonsterWorldOnceAsync(at.AddSeconds(2), CancellationToken.None);
        var awardedTitle = AtlantisCompletionRewardPolicy.SeabedExplorerTitleId;
        Check.True(store.CommitCount == 1 && store.DuplicateReceiptCount == 1,
            "title ownership projection recovers from the existing durable completion receipt");
        var completionPackets = fixture.ReadAllPackets();
        for (var index = 0; index < characters.Length; index++)
        {
            var character = characters[index];
            var expectedOwned = titlesBefore[index].Append(awardedTitle).Distinct().Order().ToArray();
            Check.True(character.SelectedTitleId == selectedTitles[index] &&
                character.OwnedTitleIds.Order().SequenceEqual(expectedOwned) &&
                character.MedusaHonorPoints == honorBefore[index] + 2800,
                "Atlantis awards title ownership and HardPoints without equipping or replacing a selected title");
            var packets = completionPackets[index].Skip(packetsBefore[index]).ToArray();
            var designations = packets.Where(packet => ReadOpcode(packet) == Opcodes.DesignationInfo).ToArray();
            Check.Equal(1, designations.Length, "the recovered award refreshes the owned-title dialog once");
            AssertAtlantisOwnedTitleSelection(designations[0], selectedTitles[index], expectedOwned);
            Check.True(packets.All(packet => ReadOpcode(packet) != 0x27D7),
                "earning a title sends neither a self nor an observer title-selection change packet");
        }

        var after = AtlantisPacketCounts(fixture);
        await registry.AdvanceMonsterWorldOnceAsync(at.AddSeconds(3), CancellationToken.None);
        await registry.AdvanceMonsterWorldOnceAsync(at.AddSeconds(4), CancellationToken.None);
        Check.True(store.Requests.Count == 2 && store.DuplicateReceiptCount == 1 &&
            characters.Select(character => character.SelectedTitleId).SequenceEqual(selectedTitles),
            "cached completion ticks preserve the chosen titles after a duplicate receipt");
        var cachedPackets = fixture.ReadAllPackets();
        for (var index = 0; index < cachedPackets.Count; index++)
        {
            Check.True(cachedPackets[index].Skip(after[index]).All(packet =>
                ReadOpcode(packet) != Opcodes.DesignationInfo && ReadOpcode(packet) != 0x27D7),
                "cached completion sends no further ownership or selected-title packets");
        }
    }

    private static void AssertAtlantisOwnedTitleSelection(byte[] packet, uint selectedTitle,
        IReadOnlyList<uint> ownedTitles)
    {
        Check.True(packet.Length == 12 + ownedTitles.Count * 12 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == selectedTitle &&
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(8)) == ownedTitles.Count,
            "the native title dialog preserves its selection header and includes the complete owned-title list");
        for (var index = 0; index < ownedTitles.Count; index++)
        {
            Check.Equal(ownedTitles[index],
                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12 + index * 12)),
                "the native title dialog contains the awarded title alongside all previously owned titles");
        }
    }
}
