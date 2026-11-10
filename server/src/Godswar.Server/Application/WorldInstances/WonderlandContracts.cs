using System.Collections.Immutable;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.WorldInstances;

internal enum WonderlandRunState { Active, Completed, TimedOut, Cancelled }
internal enum WonderlandMonsterRole
{
    AlphaDemon, ArrowTower, ChestGuard, Derskey, Monkeyface, Wolfspider, Troll,
    Spirit, Petbird, FlameRooster, RockSpirit, StoneGuardian, ManaDefSpoiler,
    AthenianMarshal, SpartanMarshal, ExpeditionSoldier, PlatinumDragon,
    MultiHead, PutridBird, LostTower, Minotaur, Deer, DragonKing, ScorpionKing, Atlas,
    DemonicStooge, DemonicRaider, DemonicAssaulter, FiringHoop
}

internal readonly record struct WonderlandParticipant(int CharacterId, int Level, byte Camp);
internal readonly record struct WonderlandPosition(float X, float Y, float Z);
internal readonly record struct WonderlandBounds(float MinimumX, float MinimumZ, float MaximumX, float MaximumZ)
{
    public bool Contains(float x, float z) => float.IsFinite(x) && float.IsFinite(z) &&
        x >= MinimumX && x <= MaximumX && z >= MinimumZ && z <= MaximumZ;
}

internal sealed record WonderlandIslandGeometry(int Island, WonderlandPosition Entrance,
    WonderlandPosition Exit, WonderlandPosition Center, WonderlandPosition TreasureChest, WonderlandBounds Bounds,
    ImmutableArray<WonderlandPosition> SpawnPositions,
    ImmutableArray<WonderlandPosition> GroundFirePositions);

internal readonly record struct WonderlandCombatStats(uint MaximumHealth, int Level,
    int PhysicalAttack, int MagicAttack, int PhysicalDefense, int MagicDefense,
    int Hit, int Dodge, bool UsesMagicDamage, float AttackRange, TimeSpan AttackInterval);

internal sealed record WonderlandSpawnPolicy(uint ObjectId, int Stage,
    string TemplateKey, WonderlandMonsterRole Role, bool IsBoss,
    bool RequiredForProgression, bool IsAllied, byte? FactionCamp,
    WonderlandPosition Position, WonderlandCombatStats Stats,
    bool Stationary, float AggroRadius, float LeashRadius)
{
    public string MechanicKey => Role switch
    {
        WonderlandMonsterRole.AlphaDemon => "alpha",
        WonderlandMonsterRole.Derskey => "derskey",
        WonderlandMonsterRole.Monkeyface => "monkeyface",
        WonderlandMonsterRole.FlameRooster => "rooster",
        WonderlandMonsterRole.RockSpirit => "rock",
        WonderlandMonsterRole.AthenianMarshal or WonderlandMonsterRole.SpartanMarshal => "marshal",
        WonderlandMonsterRole.PlatinumDragon => "platinum",
        WonderlandMonsterRole.MultiHead => "multihead",
        WonderlandMonsterRole.Minotaur => "minotaur",
        WonderlandMonsterRole.Deer => "deer",
        WonderlandMonsterRole.DragonKing => "dragonking",
        WonderlandMonsterRole.ScorpionKing => "scorpion",
        WonderlandMonsterRole.Atlas => "atlas",
        WonderlandMonsterRole.ChestGuard => "chest",
        WonderlandMonsterRole.DemonicRaider => "raider",
        WonderlandMonsterRole.DemonicAssaulter => "assaulter",
        WonderlandMonsterRole.Petbird => "petbird",
        WonderlandMonsterRole.PutridBird => "putridbird",
        WonderlandMonsterRole.ArrowTower => "tower",
        WonderlandMonsterRole.LostTower => "losttower",
        _ => "support"
    };
}

internal readonly record struct WonderlandMonsterIdentity(uint ObjectId, uint SpawnGeneration);
internal sealed record WonderlandIslandClear(int Island, DateTimeOffset ClearedAt);
internal sealed record WonderlandSnapshot(WorldInstanceId InstanceId, DateTimeOffset StartedAt,
    DateTimeOffset Deadline, DateTimeOffset LastObservedAt, WonderlandRunState State,
    int CurrentIsland, int CompletedIslands, DateTimeOffset? LastIslandClearedAt,
    DateTimeOffset? TerminalAt, DateTimeOffset StageEnteredAt, byte PartyCamp,
    bool PublicationPending, int RequiredMonstersRemaining,
    ImmutableArray<WonderlandParticipant> Participants,
    ImmutableArray<WonderlandSpawnPolicy> ActiveSpawns,
    ImmutableArray<WonderlandIslandClear> Clears)
{
    public int ActiveStage => CurrentIsland;
    public WonderlandIslandGeometry Geometry => WonderlandTerrainPolicy.GetIsland(CurrentIsland);
    public WonderlandPosition Entrance => Geometry.Entrance;
    public WonderlandPosition Exit => Geometry.Exit;
}

internal enum WonderlandKillOutcome
{
    Applied, IslandCleared, RunCompleted, Duplicate, UnknownMonster,
    WrongInstance, StaleTimestamp, RunNotActive, NotPublished
}

internal sealed record WonderlandKillResult(WonderlandKillOutcome Outcome,
    WonderlandSnapshot Snapshot, WonderlandIslandClear? ClearedIsland = null);
