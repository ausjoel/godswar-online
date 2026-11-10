using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static class AtlantisMonsterKillScoringChecks
{
    public const string CheckName = "Atlantis committed kill scoring and published rank authority";
    private static readonly DateTimeOffset Start = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync()
    {
        foreach (var mode in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        {
            await CheckCommitBoundaryAsync(mode);
            CheckCapturedDeathSurvivesRespawn(mode);
            CheckAuthoritativeIdentityAndRank(mode);
        }
    }

    private static async Task CheckCommitBoundaryAsync(MonsterRuntimeMode mode)
    {
        var map = CreateMap(mode);
        var death = Kill(map, 101);
        var content = Content("normal");
        var captured = AtlantisMonsterKillScoring.Capture(map, content, map.WorldInstanceId, death);
        Check.True(captured is not null && Points(map) == 0, "capturing a validated death does not score");
        void Score(string? settled)
        {
            if (settled is not null)
            {
                AtlantisMonsterKillScoring.RecordCommitted(
                    map, captured!, Start.AddSeconds(2));
            }
        }

        var attempts = 0;
        try
        {
            await MonsterDeathRewardCommitBoundary.ExecuteAsync<string?>(
                _ => { attempts++; return Task.FromException<string?>(new IOException("commit unavailable")); },
                allowImmediateReplay: true, onSettled: Score);
            throw new InvalidOperationException("failed durable settlement unexpectedly returned");
        }
        catch (IOException)
        {
        }
        Check.Equal(2, attempts, $"{mode}: failed durable reward replayed once");
        Check.Equal(0, Points(map), $"{mode}: failed reward commit does not score");

        await MonsterDeathRewardCommitBoundary.ExecuteAsync(
            _ => Task.FromResult<string?>(null), true, onSettled: Score);
        Check.Equal(0, Points(map), $"{mode}: rejected settlement does not score");

        attempts = 0;
        await MonsterDeathRewardCommitBoundary.ExecuteAsync(
            token =>
            {
                Check.True(!token.CanBeCanceled, "settlement survives session cancellation");
                return ++attempts == 1
                    ? Task.FromException<string?>(new IOException("commit acknowledgement lost"))
                    : Task.FromResult<string?>("replayed committed receipt");
            }, true, onSettled: Score);
        Check.Equal(1, Points(map), $"{mode}: recovered committed receipt scores once");
        await MonsterDeathRewardCommitBoundary.ExecuteAsync(
            _ => Task.FromResult<string?>("duplicate committed receipt"), true, onSettled: Score);
        Check.Equal(1, Points(map), $"{mode}: duplicate committed reward does not score twice");

        attempts = 0;
        try
        {
            await MonsterDeathRewardCommitBoundary.ExecuteAsync(
                _ => { attempts++; return Task.FromResult("committed"); }, true,
                onSettled: _ => throw new IOException("encounter projection unavailable"));
        }
        catch (IOException)
        {
        }
        Check.Equal(1, attempts, $"{mode}: score callback failure cannot replay reward transaction");
    }

    private static void CheckAuthoritativeIdentityAndRank(MonsterRuntimeMode mode)
    {
        var map = CreateMap(mode);
        var death = Kill(map, 101);
        var at = Start.AddSeconds(2);
        var forgedTemplate = death with
        {
            Monster = death.Monster with
            {
                Definition = death.Monster.Definition with { TemplateKey = "ForgedBoss" }
            }
        };
        var content = Content("elite") with
        {
            MonsterTemplates = [Template("Soldier", "elite", 205), Template("Soldier", "boss", 40),
                Template("ForgedBoss", "boss", 205)]
        };
        var result = AtlantisMonsterKillScoring.RecordCommitted(
            map, content, map.WorldInstanceId, forgedTemplate, at);
        Check.Equal(10, result.PointsAwarded,
            $"{mode}: rank uses exact-map published live template, ignoring supplied template and other maps");

        var other = CreateMap(mode);
        Kill(other, 101);
        result = AtlantisMonsterKillScoring.RecordCommitted(
            other, content, other.WorldInstanceId, death, at);
        Check.True(result.Outcome == AtlantisKillOutcome.InvalidMonsterIdentity && Points(other) == 0,
            $"{mode}: same object/generation from another monster runtime cannot score");

        var second = Kill(map, 102);
        result = AtlantisMonsterKillScoring.RecordCommitted(
            map, GameplayContentCatalog.Empty, map.WorldInstanceId, second, at);
        Check.True(result.Outcome == AtlantisKillOutcome.UnknownRank && Points(map) == 10,
            $"{mode}: unpublished template cannot use defensive boss fallback");
        result = AtlantisMonsterKillScoring.RecordCommitted(
            map, Content("boss"), map.WorldInstanceId, second, at);
        Check.Equal(50, result.PointsAwarded, $"{mode}: unknown rank did not consume death identity");

        var third = Kill(map, 103);
        var stale = third with { Monster = third.Monster with { SpawnGeneration = third.Monster.SpawnGeneration + 1 } };
        result = AtlantisMonsterKillScoring.RecordCommitted(map, Content("normal"), map.WorldInstanceId, stale, at);
        Check.True(result.Outcome == AtlantisKillOutcome.InvalidMonsterIdentity && Points(map) == 60,
            $"{mode}: generation mismatch cannot score");
        result = AtlantisMonsterKillScoring.RecordCommitted(map, Content("normal"), WorldInstanceId.New(), third, at);
        Check.True(result.Outcome == AtlantisKillOutcome.WrongInstance && Points(map) == 60,
            $"{mode}: wrong world instance cannot score");
    }

    private static void CheckCapturedDeathSurvivesRespawn(MonsterRuntimeMode mode)
    {
        var map = CreateMap(mode);
        var death = Kill(map, 101);
        var captured = AtlantisMonsterKillScoring.Capture(map, Content("elite"), map.WorldInstanceId, death);
        Check.True(captured is not null, "authoritative death is captured before durable settlement");
        var respawnAt = death.Monster.RespawnAt ?? throw new InvalidOperationException("missing timed respawn");
        map.AdvanceMonsters(Start.AddSeconds(1)); // Deliver the pending death before corpse lifecycle ticks.
        map.AdvanceMonsters(death.Monster.DespawnAt ?? respawnAt);
        map.AdvanceMonsters(respawnAt);
        Check.True(map.TryGetMonsterSnapshot(101, out var respawned) && respawned.IsAlive &&
            respawned.SpawnGeneration != death.Monster.SpawnGeneration,
            $"{mode}: corpse respawns while committed reward acknowledgement is delayed");
        Check.Equal(0, Points(map), "capturing and respawning does not score before settlement");
        var result = AtlantisMonsterKillScoring.RecordCommitted(map, captured!, respawnAt.AddSeconds(1));
        Check.Equal(10, result.PointsAwarded, $"{mode}: delayed committed reward scores original pinned death");
        result = AtlantisMonsterKillScoring.RecordCommitted(map, captured!, respawnAt.AddSeconds(1));
        Check.True(result.Outcome == AtlantisKillOutcome.DuplicateKill && Points(map) == 10,
            $"{mode}: delayed death replay cannot score the original generation twice");
    }

    private static GameplayContentCatalog Content(string rank) => GameplayContentCatalog.Empty with
    {
        MonsterTemplates = [Template("Soldier", rank, 205)]
    };

    private static GameplayMonsterTemplateDefinition Template(string key, string rank, short map) =>
        new($"{map}:{key}", "check", map, "Atlantis", key, key, rank,
            rank == "boss", rank == "elite", false, null, null);

    private static MapInstance CreateMap(MonsterRuntimeMode mode)
    {
        var map = new MapInstance(AtlantisRunRuntimeChecks.Descriptor(), mode);
        Check.True(map.TryStartAtlantisEncounter(Start, out _), "Atlantis test run starts");
        map.InitializeMonsters([Spawn(101), Spawn(102), Spawn(103)], Start);
        return map;
    }

    private static MonsterDamageResult Kill(MapInstance map, uint objectId)
    {
        Check.True(map.TryApplyMonsterDamage(objectId, 237, Start.AddSeconds(1), out var death) && death.Killed,
            "authoritative monster health mutation commits death");
        return death;
    }

    private static int Points(MapInstance map)
    {
        Check.True(map.TryGetAtlantisRunSnapshot(out var snapshot), "Atlantis snapshot exists");
        return snapshot.TeamPoints;
    }

    private static CapturedMonsterSpawn Spawn(uint objectId)
    {
        var packet = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 108);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), 10020);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 0x212);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20), 237);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(24), 237);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(40), 1f);
        Encoding.ASCII.GetBytes("Soldier").CopyTo(packet.AsSpan(44));
        return new(205, "Atlantis", "Soldier", "Soldier", objectId, 0, 0, packet);
    }
}
