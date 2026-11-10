using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// Picks the successor for a running instance whose registered leader is no
    /// longer inside it, so the run's own end control never becomes unreachable.
    /// </summary>
    /// <remarks>
    /// Runs from the world tick under the caller's registry gate and never
    /// awaits: it only reads a presence list the caller already captured and
    /// reports which character should take over. The caller writes the new
    /// leader into whichever record its instance keeps it in.
    /// <para>
    /// The successor is the earliest entrant among those still present, with the
    /// character id as a stable tie-break for members an instance cannot order
    /// (for example a member admitted through an invitation or a late Enter
    /// window). An empty instance transfers nothing: the existing empty-runtime
    /// retirement owns that case.
    /// </para>
    /// </remarks>
    private bool TryResolveInstanceLeaderSuccessor(
        WorldInstanceId instanceId,
        string kind,
        int currentLeaderId,
        IReadOnlyList<(int CharacterId, int EntryOrder)> present,
        out int successorId)
    {
        successorId = 0;
        if (present.Count == 0 ||
            present.Any(member => member.CharacterId == currentLeaderId))
        {
            return false;
        }

        successorId = present
            .OrderBy(member => member.EntryOrder)
            .ThenBy(member => member.CharacterId)
            .First()
            .CharacterId;
        Console.WriteLine(
            "[instance] leader transferred instance=" + instanceId +
            $" from={currentLeaderId} to={successorId} kind={kind}");
        return true;
    }

    /// <summary>
    /// Orders a presence list by an instance's own entry order, falling back to
    /// the character id for members that order does not name.
    /// </summary>
    private static (int CharacterId, int EntryOrder)[] OrderInstanceMembers(
        IReadOnlyList<int> entryOrder,
        IReadOnlyList<int> presentCharacterIds)
    {
        var order = new Dictionary<int, int>(entryOrder.Count);
        for (var index = 0; index < entryOrder.Count; index++)
        {
            order.TryAdd(entryOrder[index], index);
        }
        var present = new (int, int)[presentCharacterIds.Count];
        for (var index = 0; index < presentCharacterIds.Count; index++)
        {
            var characterId = presentCharacterIds[index];
            present[index] = (
                characterId,
                order.TryGetValue(characterId, out var position)
                    ? position
                    : int.MaxValue);
        }
        return present;
    }
}
