using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.World;

internal static partial class WorldContentRevisionHasher
{
    // Payload hashes remain stable for historical releases and world manifests.
    // A publication identity also binds the dependency and loader contract.
    public static WorldContentFamilyRevision HashNpcDialogueRelease(
        WorldContentFamilyRevision payload,
        string spawnRevision,
        int contractVersion = 1)
    {
        if (payload.Family != "npc-dialogues" || payload.EntryCount < 0 ||
            contractVersion < 1 || !IsCanonicalRevision(payload.Sha256) ||
            !IsCanonicalRevision(spawnRevision))
        {
            throw new ArgumentException("Invalid NPC dialogue release manifest.");
        }

        using var hash = new CanonicalHashBuilder("npc-dialogue-release");
        hash.AppendInt32(contractVersion);
        hash.AppendString(payload.Sha256);
        hash.AppendInt32(payload.EntryCount);
        hash.AppendString(spawnRevision);
        return new WorldContentFamilyRevision(
            payload.Family, hash.Finish(), payload.EntryCount);
    }

    public static bool MatchesNpcDialogueRelease(
        string releaseRevision,
        WorldContentFamilyRevision payload,
        string spawnRevision) =>
        string.Equals(releaseRevision, payload.Sha256, StringComparison.Ordinal) ||
        string.Equals(releaseRevision,
            HashNpcDialogueRelease(payload, spawnRevision).Sha256,
            StringComparison.Ordinal);

    private static bool IsCanonicalRevision(string revision) =>
        revision is { Length: 64 } && revision.All(static character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');
}
