using System.Collections.Concurrent;
using Godswar.Server.Application.Rates;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.Troy;

/// <summary>One of the five Trojan heroes (TroyLayout.Generated.cs): where it appears and where its chest stands.</summary>
internal sealed record TroyBossDefinition(
    int Stage,
    string TemplateKey,
    string DisplayName,
    float X,
    float Z,
    int Level,
    int BaseHealth,
    string ChestNpcKey,
    float ChestX,
    float ChestZ);

/// <summary>What a boss chest answers one player (NpcFunTroy.lua, Index 1).</summary>
internal enum TroyChestState
{
    /// <summary>107-111 "You have to bring down X to get his treasure chest."</summary>
    BossNotDown,
    /// <summary>207 "All the prizes in the treasure chest have been claimed." (GENERATED for a non-participant.)</summary>
    NotParticipant,
    /// <summary>1107 "You've missed the opportunity ... before beginning combat against another one".</summary>
    Forfeited,
    /// <summary>1108 "You've already claimed the rewards."</summary>
    Claimed,
    /// <summary>The claim may run: 208 (with a recipe) or 209.</summary>
    Available
}

/// <summary>
/// W2 F-27 TROY slice 2: the five-hero sequence of one event day (Pandarus, Sarpedon, Lycaon on the beach, then Paris
/// and Hector in Troy; IGG and NF_L0_TR103). One hero is up at a time; the next appears a short delay after the previous
/// falls. Process-wide, in memory (a restart during the event starts the chain again at the stage it had not finished:
/// GENERATED). Pure state: the spawning, the chest payout and the packets are in Game/GameClientHandler.TroyBosses.cs.
/// <para>
/// Chests (Troy_006-010, "Claim the rewards for defeating a BOSS before beginning combat against another one, or else
/// you will forfeit the rewards"): a chest pays each player whose damage landed on its boss, once, until that player's
/// damage lands on a later hero.
/// </para>
/// </summary>
internal sealed class TroyBossChain
{
    public const int StageCount = 5;
    public const int BeachHeroes = 3;

    public const string BossesSwitch = "troy.bosses";
    public const string BossHpPercent = "troy.boss_hp_percent";
    public const string BossDelaySeconds = "troy.boss_delay_seconds";
    public const string ChestPills = "troy.chest_pills";
    public const string ChestRecipePercent = "troy.chest_recipe_percent";

    public static bool BossesOn => ServerRates.Current.Absolute(BossesSwitch) == 1;

    /// <summary>Achilles' Rage Lv. 1-5 (Status.ini 620-624, "super buff"): chest N gives Lv N (GENERATED pairing).</summary>
    public const uint AchillesRageFirstStatus = 620;

    /// <summary>A hero stays this long if nobody kills it (GENERATED: the 60-minute window plus a margin).</summary>
    public static readonly TimeSpan BossLifetime = TimeSpan.FromMinutes(65);

    /// <summary>A failed spawn (no ready session, map limit) is retried after this long.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(20);

    /// <summary>The overlay allows one live spawn per owner and keeps a dead one for its 20 s loot window, so each
    /// hero has its own synthetic (negative) owner id; no character has it.</summary>
    public static int OwnerFor(int stage) => -(TroyEvent.MapId * 100 + stage);

    public static IReadOnlyList<ServerRateDefinition> Definitions { get; } =
    [
        new(BossesSwitch, TroyEvent.Category, "Troy heroes and chests (five-boss sequence)", "0/1",
            ServerRateKind.Absolute, 1m, 0m, 1m, 1m, 8, "Game/GameClientHandler.TroyBosses.cs",
            "1 = while the event is open, the five Trojan heroes appear on map 44 one at a time (Pandarus, Sarpedon, " +
            "Lycaon on the beach, then Paris and Hector inside Troy), each a short delay after the previous falls; their " +
            "chests pay Exp Pills, Achilles' Rage and a chance of a recipe to those who fought them; the Troy " +
            "Transporter opens after the three beach heroes. Needs troy-spawns.sql for the NPCs. 0 = no heroes, chests silent."),
        new(BossHpPercent, TroyEvent.Category, "Hero HP", "%", ServerRateKind.Absolute, 100m, 10m, 1000m, 10m, 9,
            "Application/Troy/TroyLayout.Generated.cs",
            "100 = the generated hero HP (2008 base HP curve x 40: Pandarus about 148,000 ... Hector about 384,000)."),
        new(BossDelaySeconds, TroyEvent.Category, "Next hero after", "s", ServerRateKind.Absolute, 30m, 0m, 600m, 5m, 10,
            "Application/Troy/TroyBossChain.cs",
            "Seconds between a hero's death and the next hero (checked on the 20 s player tick)."),
        new(ChestPills, TroyEvent.Category, "Chest Exp Pills (first hero)", "pills", ServerRateKind.Absolute, 10m, 0m,
            99m, 1m, 11, "Application/Troy/TroyBossChain.cs",
            "Exp Pills from Pandarus' chest; each later chest gives 5 more (10/15/20/25/30). Scaled by Reward size."),
        new(ChestRecipePercent, TroyEvent.Category, "Chest recipe chance", "%", ServerRateKind.Absolute, 10m, 0m,
            100m, 1m, 12, "Application/Troy/TroyBossChain.cs",
            "Chance that a chest also holds one random profession recipe (NF_L0_TR208 'rare Profession recipe').")
    ];

    public static int PillsFor(int stage) =>
        (int)Math.Clamp((ServerRates.Current.Absolute(ChestPills) + 5 * (stage - 1)) *
            ServerRates.Current.Absolute(TroyEvent.RewardPercent) / 100m, 0m, 99m * 5m);

    public static uint HealthFor(TroyBossDefinition boss) =>
        (uint)Math.Clamp(boss.BaseHealth * ServerRates.Current.Absolute(BossHpPercent) / 100m, 1m, 2_000_000_000m);

    public static TroyBossDefinition? BossOfChest(string npcKey) =>
        TroyLayout.Bosses.FirstOrDefault(boss => string.Equals(boss.ChestNpcKey, npcKey, StringComparison.Ordinal));

    /// <summary>The map announcement as a hero appears: Troy_Notice1-4 for Sarpedon..Hector (LuaText); Pandarus has
    /// none in the client (GENERATED line).</summary>
    public static string AppearNotice(int stage) => stage switch
    {
        1 => "Archer Pandarus guards the beach of Troy!",
        2 => "Sarpedon has appeared at the Apollo Temple.",
        3 => "Lycaon, a prince of Troy, has appeared at the gates of Troy.",
        4 => "Paris, the seductive Trojan prince, has appeared in Troy!",
        _ => "Hector, the top Trojan warrior, has appeared in the palace of Troy!"
    };

    /// <summary>GENERATED: the line as a hero falls (the chest rule is the NPC description's).</summary>
    public static string FallNotice(TroyBossDefinition boss) =>
        $"{ShortName(boss)} has fallen! Claim his chest before you fight the next hero.";

    public static string ShortName(TroyBossDefinition boss) => boss.Stage switch
    {
        1 => "Pandarus", 2 => "Sarpedon", 3 => "Lycaon", 4 => "Paris", _ => "Hector"
    };

    private sealed class Stage
    {
        public uint ObjectId;
        public Guid RuntimeInstanceId;
        public WorldInstanceId? Instance;
        public DateTimeOffset SpawnedAt;
        public DateTimeOffset? KilledAt;
        public ConcurrentDictionary<int, byte> Attackers = new();
        public readonly HashSet<int> Claimed = [];
        public bool Live => ObjectId != 0 && KilledAt is null;
    }

    private readonly object _sync = new();
    private DateOnly _day;
    private Stage[] _stages = NewStages();
    private int _next; // index of the hero that is up or due (0..5)
    private DateTimeOffset _nextSpawnAt = DateTimeOffset.MinValue;
    private bool _spawning;

    public static TroyBossChain Shared { get; } = new();

    private static Stage[] NewStages() => [.. Enumerable.Range(0, StageCount).Select(static _ => new Stage())];

    private void Roll(DateOnly day)
    {
        if (day == _day)
        {
            return;
        }

        _day = day;
        _stages = NewStages();
        _next = 0;
        _nextSpawnAt = DateTimeOffset.MinValue;
        _spawning = false;
    }

    /// <summary>The number of heroes killed on <paramref name="day"/> (0-5).</summary>
    public int Killed(DateOnly day)
    {
        lock (_sync)
        {
            return day == _day ? _stages.Count(static stage => stage.KilledAt is not null) : 0;
        }
    }

    public bool BeachCleared(DateOnly day) => Killed(day) >= BeachHeroes;

    /// <summary>
    /// The hero to spawn now, or null (one already up, all five dead, a spawn running, or the delay not over). A
    /// non-null answer reserves the spawn: finish it with <see cref="CompleteSpawn"/> or <see cref="AbortSpawn"/>.
    /// </summary>
    public TroyBossDefinition? TryBeginSpawn(DateOnly day, DateTimeOffset now)
    {
        lock (_sync)
        {
            Roll(day);
            if (_spawning || _next >= StageCount || _stages[_next].Live || now < _nextSpawnAt)
            {
                return null;
            }

            _spawning = true;
            return TroyLayout.Bosses[_next];
        }
    }

    /// <summary>The live spawn's identity; its attackers set is the one the overlay fills.</summary>
    public ConcurrentDictionary<int, byte> AttackersFor(DateOnly day, int stage)
    {
        lock (_sync)
        {
            Roll(day);
            return _stages[stage - 1].Attackers;
        }
    }

    public void CompleteSpawn(DateOnly day, int stage, uint objectId, Guid runtimeInstanceId, DateTimeOffset now,
        WorldInstanceId? instance = null)
    {
        lock (_sync)
        {
            _spawning = false;
            if (day != _day || stage - 1 != _next)
            {
                return;
            }

            var state = _stages[stage - 1];
            state.ObjectId = objectId;
            state.RuntimeInstanceId = runtimeInstanceId;
            state.Instance = instance;
            state.SpawnedAt = now;
        }
    }

    public void AbortSpawn(DateTimeOffset now)
    {
        lock (_sync)
        {
            _spawning = false;
            _nextSpawnAt = now + RetryDelay;
        }
    }

    /// <summary>The hero that is up, for polling its presence (DoT kills, timeouts).</summary>
    public (int Stage, uint ObjectId, Guid RuntimeInstanceId, DateTimeOffset SpawnedAt, WorldInstanceId? Instance)?
        LiveBoss(DateOnly day)
    {
        lock (_sync)
        {
            if (day != _day || _next >= StageCount || !_stages[_next].Live)
            {
                return null;
            }

            var state = _stages[_next];
            return (_next + 1, state.ObjectId, state.RuntimeInstanceId, state.SpawnedAt, state.Instance);
        }
    }

    /// <summary>
    /// Records a kill of the live hero (the kill hook, or polling that found its corpse / found it gone within its
    /// lifetime). Returns the hero when this call recorded it, null for any other monster or a repeat.
    /// </summary>
    public TroyBossDefinition? RecordKill(DateOnly day, uint objectId, Guid runtimeInstanceId, int? killerId,
        DateTimeOffset now, TimeSpan nextDelay)
    {
        lock (_sync)
        {
            if (day != _day || _next >= StageCount)
            {
                return null;
            }

            var state = _stages[_next];
            if (!state.Live || state.ObjectId != objectId || state.RuntimeInstanceId != runtimeInstanceId)
            {
                return null;
            }

            state.KilledAt = now;
            if (killerId is { } killer)
            {
                state.Attackers.TryAdd(killer, 0);
            }

            var boss = TroyLayout.Bosses[_next];
            _next++;
            _nextSpawnAt = now + nextDelay;
            return boss;
        }
    }

    /// <summary>The live hero timed out unkilled: it may appear again at once.</summary>
    public void RecordVanished(DateOnly day, uint objectId, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (day == _day && _next < StageCount && _stages[_next].Live && _stages[_next].ObjectId == objectId)
            {
                _stages[_next] = new Stage();
                _nextSpawnAt = now;
            }
        }
    }

    public TroyChestState ChestState(DateOnly day, int stage, int characterId)
    {
        lock (_sync)
        {
            if (day != _day || _stages[stage - 1].KilledAt is null)
            {
                return TroyChestState.BossNotDown;
            }

            var state = _stages[stage - 1];
            if (state.Claimed.Contains(characterId))
            {
                return TroyChestState.Claimed;
            }

            if (!state.Attackers.ContainsKey(characterId))
            {
                return TroyChestState.NotParticipant;
            }

            for (var later = stage; later < StageCount; later++)
            {
                if (_stages[later].Attackers.ContainsKey(characterId))
                {
                    return TroyChestState.Forfeited;
                }
            }

            return TroyChestState.Available;
        }
    }

    /// <summary>Reserves the claim (true once per player and chest). Undo with <see cref="ReleaseClaim"/>.</summary>
    public bool TryClaim(DateOnly day, int stage, int characterId)
    {
        lock (_sync)
        {
            return ChestState(day, stage, characterId) == TroyChestState.Available &&
                _stages[stage - 1].Claimed.Add(characterId);
        }
    }

    public void ReleaseClaim(DateOnly day, int stage, int characterId)
    {
        lock (_sync)
        {
            if (day == _day)
            {
                _stages[stage - 1].Claimed.Remove(characterId);
            }
        }
    }

    /// <summary>Test seam: a fresh chain.</summary>
    internal static TroyBossChain CreateForChecks() => new();
}
