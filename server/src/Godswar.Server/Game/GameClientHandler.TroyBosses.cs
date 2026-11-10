using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Troy;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// W2 F-27 TROY slice 2: the five-hero sequence on map 44, the boss chests and the two transporters. Rules and
/// GENERATED choices: Application/Troy/TroyBossChain.cs and docs/TROY-EVENT.md section 8. Hot switch troy.bosses
/// (default 1); nothing happens unless a player stands on map 44 while the event.troy gate is open, and the NPCs only
/// exist once troy-spawns.sql is applied. troy.entry (default 0) still decides whether players can get there.
/// </summary>
internal sealed partial class GameClientHandler
{
    /// <summary>Function 67 with no buttons: every answer is sent by the handler on the opening request.</summary>
    private static readonly ScriptedNpcDialogue TroyMapNpcDialogue = new(
        FunctionNumber: TroyEvent.Function,
        OpeningMenu: [],
        Steps: new Dictionary<int, int[]>());

    private const int TroyTransporterRefusal = 103; // NF_L0_TR103 "get past the 3 Trojan Heroes on the beach"
    private const int TroyChestEmpty = 207, TroyChestRecipe = 208, TroyChestPaid = 209, TroyChestBagFull = 312,
        TroyChestMissed = 1107, TroyChestClaimed = 1108;

    private static bool IsTroyMapNpc(NpcSpawnDefinition npc) =>
        npc.MapId == TroyEvent.MapId && npc.NpcKey is "Troy_002" or "Troy_011" or "Troy_006" or "Troy_007" or
            "Troy_008" or "Troy_009" or "Troy_010";

    /// <summary>Map 44 rows may use the global Trojan templates of the generated layout (Monster/Monster.ini).</summary>
    private static void AddTroyTemplateKeys(short mapId, HashSet<string> catalogTemplateKeys)
    {
        if (mapId == TroyEvent.MapId)
        {
            catalogTemplateKeys.UnionWith(TroyLayout.RetinueTemplates.Select(static row => row.Template));
        }
    }

    private DateOnly TroyChainDay() => TroyEvent.ScoringDay(TroyWindows, TroyRealmNow);

    // ---- the sequence (20 s player tick, inside the character gate) ---------------------------------------------
    private async Task TickTroyBossesAsync(CancellationToken cancellationToken)
    {
        if (!TroyBossChain.BossesOn || _character is not { } character || character.CurrentMap != TroyEvent.MapId ||
            _pendingMapTransition is not null || _session.IsDisconnected ||
            !TroyEvent.IsOpen(_realmCalendar, DateTimeOffset.UtcNow))
        {
            return;
        }

        var chain = TroyBossChain.Shared;
        var day = TroyChainDay();
        var now = DateTimeOffset.UtcNow;

        // A hero that died outside the kill hook (damage over time) or timed out is noticed here.
        if (chain.LiveBoss(day) is { } live)
        {
            // Only a session on the hero's own world instance can see it; any other answers nothing.
            if (live.Instance is not { } heroInstance ||
                !_registry.TryGetSessionWorldInstanceId(_session, out var ownInstance) || ownInstance != heroInstance)
            {
                return;
            }

            var presence = _registry.GetWorldRuntimeSpawnPresence(_session, live.ObjectId, live.RuntimeInstanceId);
            if (presence == WorldRuntimeSpawnPresence.Dead ||
                presence == WorldRuntimeSpawnPresence.Gone && now - live.SpawnedAt < TroyBossChain.BossLifetime)
            {
                if (chain.RecordKill(day, live.ObjectId, live.RuntimeInstanceId, null, now, TroyNextDelay) is { } fallen)
                {
                    await AnnounceTroyAsync(TroyBossChain.FallNotice(fallen), cancellationToken);
                    Console.WriteLine($"[troy] hero down (polled) stage={fallen.Stage} presence={presence}");
                }
            }
            else if (presence == WorldRuntimeSpawnPresence.Gone)
            {
                chain.RecordVanished(day, live.ObjectId, now);
                Console.WriteLine($"[troy] hero timed out stage={live.Stage}");
            }

            return;
        }

        if (chain.TryBeginSpawn(day, now) is not { } boss)
        {
            return;
        }

        var spawned = false;
        try
        {
            spawned = await TrySpawnTroyHeroAsync(chain, day, boss, now, cancellationToken);
        }
        finally
        {
            if (!spawned)
            {
                chain.AbortSpawn(DateTimeOffset.UtcNow);
            }
        }
    }

    private static TimeSpan TroyNextDelay =>
        TimeSpan.FromSeconds((double)ServerRates.Current.Absolute(TroyBossChain.BossDelaySeconds));

    private async Task<bool> TrySpawnTroyHeroAsync(TroyBossChain chain, DateOnly day, TroyBossDefinition boss,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var template = _gameplayCatalogs.Content.MonsterTemplates.FirstOrDefault(candidate =>
            string.Equals(candidate.TemplateKey, boss.TemplateKey, StringComparison.Ordinal));
        if (template is null)
        {
            Console.Error.WriteLine($"[troy] hero template missing stage={boss.Stage} template={boss.TemplateKey}");
            return false;
        }

        var spec = new WorldRuntimeSpawnSpec(TroyBossChain.OwnerFor(boss.Stage), OwnerOnly: false,
            TroyBossChain.BossLifetime, [], $"troy:hero{boss.Stage}", Attackers: chain.AttackersFor(day, boss.Stage));
        var blueprint = new WorldRuntimeSpawnBlueprint(template, boss.X, boss.Z, 1f, (uint)boss.Level,
            TroyBossChain.HealthFor(boss), spec);
        if (!_registry.TryStageWorldRuntimeSpawn(_session, blueprint, now, out var ticket, out var refusal) ||
            !_registry.TryAttachWorldRuntimeSpawn(_session, ticket, now, out refusal))
        {
            Console.WriteLine($"[troy] hero spawn refused stage={boss.Stage} reason={refusal}");
            return false;
        }

        var runtimeId = ticket.Stage.Runtime.Snapshot()[0].RuntimeInstanceId;
        chain.CompleteSpawn(day, boss.Stage, ticket.Stage.Definition.ObjectId, runtimeId, now, ticket.Instance.InstanceId);
        Console.WriteLine($"[troy] hero up stage={boss.Stage} template={boss.TemplateKey} " +
            $"object={ticket.Stage.Definition.ObjectId} x={boss.X:F0} z={boss.Z:F0} hp={blueprint.MaximumHealth}");
        await AnnounceTroyAsync(TroyBossChain.AppearNotice(boss.Stage), cancellationToken);
        return true;
    }

    /// <summary>A centred line to everyone on this session's world instance of map 44 who is world-ready (the same
    /// ready-session snapshot every map broadcast uses), this session included.</summary>
    private Task AnnounceTroyAsync(string line, CancellationToken cancellationToken) =>
        _registry.BroadcastToCurrentWorldInstanceAsync(_session, PacketBuilder.CenteredGreenAnnouncement(line),
            cancellationToken, includeRoutingSession: true, label: "TroyNotice");

    /// <summary>Kill hook (from RecordTroyKillAsync): the live hero fell to this player.</summary>
    private async Task RecordTroyHeroKillAsync(MonsterDamageResult damageResult, CancellationToken cancellationToken)
    {
        if (!TroyBossChain.BossesOn || _character is not { } character ||
            !WorldRuntimeSpawnOverlay.IsRuntimeSpawnObjectId(damageResult.ObjectId))
        {
            return;
        }

        var fallen = TroyBossChain.Shared.RecordKill(TroyChainDay(), damageResult.ObjectId,
            damageResult.Monster.RuntimeInstanceId, character.Id, DateTimeOffset.UtcNow, TroyNextDelay);
        if (fallen is not null)
        {
            Console.WriteLine($"[troy] hero down stage={fallen.Stage} killer={character.Name}");
            await AnnounceTroyAsync(TroyBossChain.FallNotice(fallen), cancellationToken);
        }
    }

    // ---- chests and transporters (opening request of function 67) ---------------------------------------------
    private async Task<bool> HandleTroyMapNpcOpenAsync(NpcSpawnDefinition npc, CancellationToken cancellationToken)
    {
        if (!TroyBossChain.BossesOn)
        {
            return false;
        }

        var character = _character!;
        var day = TroyChainDay();
        int[] reply;
        switch (npc.NpcKey)
        {
            case "Troy_011":
                // GENERATED: the script has no "enter" button (only the 103 refusal), so the transporter carries the
                // player into Troy as soon as the three beach heroes are down; GMs who may skip gates always pass.
                if (!TroyBossChain.Shared.BeachCleared(day) &&
                    !(Application.Gates.GateRules.GmMaySkip(TroyEvent.GateKey) &&
                        await HasOperatorAuthorityAsync(cancellationToken)))
                {
                    reply = [TroyTransporterRefusal];
                    break;
                }

                var inward = await TryBeginSameMapSceneTransitionAsync(TroyLayout.CityArrivalX, TroyLayout.CityArrivalZ,
                    "troy-transporter", null, cancellationToken);
                Console.WriteLine($"[troy] into Troy character={character.Name} outcome={inward}");
                return true;
            case "Troy_002":
                var outward = await TryBeginSameMapSceneTransitionAsync(TroyEvent.ArrivalX, TroyEvent.ArrivalZ,
                    "troy-beach-transporter", null, cancellationToken);
                Console.WriteLine($"[troy] back to the beach character={character.Name} outcome={outward}");
                return true;
            default:
                if (TroyBossChain.BossOfChest(npc.NpcKey) is not { } boss)
                {
                    return false;
                }

                reply = await ClaimTroyChestAsync(boss, day, cancellationToken);
                break;
        }

        await SendFinalise2ReplyAsync(npc, TroyEvent.Function, reply, "TroyMapNpc", cancellationToken);
        Console.WriteLine($"[troy] npc={npc.NpcKey} character={character.Name} reply=[{string.Join(',', reply)}]");
        return true;
    }

    private async Task<int[]> ClaimTroyChestAsync(TroyBossDefinition boss, DateOnly day,
        CancellationToken cancellationToken)
    {
        var chain = TroyBossChain.Shared;
        var characterId = _character!.Id;
        switch (chain.ChestState(day, boss.Stage, characterId))
        {
            case TroyChestState.BossNotDown:
                return [106 + boss.Stage]; // NF_L0_TR107-111
            case TroyChestState.NotParticipant:
                return [TroyChestEmpty];
            case TroyChestState.Forfeited:
                return [TroyChestMissed];
            case TroyChestState.Claimed:
                return [TroyChestClaimed];
        }

        if (!chain.TryClaim(day, boss.Stage, characterId))
        {
            return [TroyChestClaimed];
        }

        var pills = TroyBossChain.PillsFor(boss.Stage);
        var recipe = PickTroyRecipe();
        var granted = new List<(int ItemId, int Quantity)>();
        var paid = false;
        try
        {
            paid = await RunEventsNpcAsync(async tx =>
            {
                // Exp Pills stack to 99 (template Overlap); GrantAsync never builds a stack above it.
                if (pills > 0 && !await tx.GrantAsync(TroyEvent.ExpPillItem, pills, bound: true) ||
                    recipe is { } item && !await tx.GrantAsync(item, 1, bound: true))
                {
                    tx.Rollback();
                    return false;
                }

                return true;
            }, false, cancellationToken);
        }
        finally
        {
            if (!paid)
            {
                chain.ReleaseClaim(day, boss.Stage, characterId);
            }
        }

        if (!paid)
        {
            return [TroyChestBagFull];
        }

        if (pills > 0)
        {
            granted.Add((TroyEvent.ExpPillItem, pills));
        }

        if (recipe is { } recipeItem)
        {
            granted.Add((recipeItem, 1));
        }

        await RefreshAfterEventsNpcAsync(granted, cancellationToken);
        var buffed = false;
        if (OperatorBuffCatalog.TryGetByStatus(TroyBossChain.AchillesRageFirstStatus + (uint)boss.Stage - 1,
                out var rage))
        {
            buffed = await _registry.ApplyRuntimeStatusAndPublishAsync(_session, rage.Definition, DateTimeOffset.UtcNow,
                "troy-chest", cancellationToken);
        }

        Console.WriteLine($"[troy] chest stage={boss.Stage} character={_character!.Name} pills={pills} " +
            $"recipe={recipe?.ToString() ?? "-"} achillesRage={buffed}");
        return [recipe is null ? TroyChestPaid : TroyChestRecipe];
    }

    /// <summary>GENERATED: troy.chest_recipe_percent chance of one recipe item whose blueprint professions slice 1 can
    /// teach (Type 0), uniformly.</summary>
    private static int? PickTroyRecipe()
    {
        var percent = (int)ServerRates.Current.Absolute(TroyBossChain.ChestRecipePercent);
        if (percent <= 0 || Random.Shared.Next(100) >= percent)
        {
            return null;
        }

        var pool = ProfessionBlueprintCatalog.All
            .Where(static blueprint => blueprint.RecipeItemId != 0 && blueprint.Type == 0)
            .Select(static blueprint => (int)blueprint.RecipeItemId)
            .Distinct()
            .ToArray();
        return pool.Length == 0 ? null : pool[Random.Shared.Next(pool.Length)];
    }
}
