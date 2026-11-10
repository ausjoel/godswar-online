using System.Collections.Immutable;
using Godswar.Server.Domain.Characters;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.WorldInstances;

internal static class WonderlandEncounterPolicy
{
    public const byte Map = 207;
    public const ushort NativeScene = 227;
    public const string SceneKey = "Fane";
    public const int IslandCount = 8;
    public static readonly MapId ContentMapId = new(Map);
    public static readonly TimeSpan TimeLimit = TimeSpan.FromMinutes(40);
    public static bool IsWonderlandInstance(WorldInstanceDescriptor descriptor) =>
        descriptor.Kind == InstanceKind.Dungeon && descriptor.MapId == ContentMapId;

    public static ImmutableArray<WonderlandParticipant> ValidateParty(
        IReadOnlyList<WonderlandParticipant> participants)
    {
        ArgumentNullException.ThrowIfNull(participants);
        if (participants.Count is < 1 or > 5 ||
            participants.Any(p => p.CharacterId <= 0 || p.Level < 120 ||
                p.Camp is not (FactionPortalSkillPolicy.SpartaCamp or FactionPortalSkillPolicy.AthensCamp)) ||
            participants.Select(p => p.CharacterId).Distinct().Count() != participants.Count)
        {
            throw new ArgumentException("Wonderland requires 1-5 distinct level-120+ admitted characters.", nameof(participants));
        }
        // Preserve leader first: faction is fixed by admission, not enumeration order.
        return participants.ToImmutableArray();
    }

    public static void ValidatePartySize(int partySize)
    {
        if (partySize is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(partySize));
    }
}
