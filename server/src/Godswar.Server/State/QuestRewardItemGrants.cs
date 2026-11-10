namespace Godswar.Server.State;

/// <summary>What happened to one quest reward item announcement.</summary>
internal enum QuestRewardItemGrantStatus : byte
{
    /// <summary>The item was written into the character's kit bag.</summary>
    Added = 1,

    /// <summary>That reward slot was already paid; nothing was written again.</summary>
    Duplicate = 2,

    /// <summary>The kit bag has no room for the item.</summary>
    InsufficientCapacity = 3,

    CharacterNotFound = 4,

    /// <summary>The item id is not in the pinned item content.</summary>
    Unsupported = 5
}

internal sealed record QuestRewardItemGrantResult(
    QuestRewardItemGrantStatus Status,
    GameCharacter? Character)
{
    public bool Succeeded => Status is
        QuestRewardItemGrantStatus.Added or
        QuestRewardItemGrantStatus.Duplicate;
}
