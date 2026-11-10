using System.Collections.Concurrent;
using Godswar.Server.Application.World.Content;
using Godswar.Server.State;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private const float MonsterLootPickupRadius = 12f;

    /// <summary>
    /// Captured corpse window for a looted field kill. The reference kept the
    /// corpse of the Athens kill on object 10492 from 01:37:23.758 until its
    /// 10023 removal at 01:37:43.820, and its captured Medusa rules use the
    /// same twenty seconds.
    /// </summary>
    private const int CorpseWithLootMilliseconds = 20_000;

    private readonly ConcurrentDictionary<
        MonsterLootRuntimeKey,
        MonsterLootRuntimeState> _monsterLoot = [];

    internal bool TryResolveMedusaMonsterRule(
        ClientSession session,
        MonsterDamageResult damage,
        out MedusaMonsterRule rule)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(damage);
        rule = default;
        if (!_sessions.TryGetValue(session, out var context) ||
            !context.WorldReady ||
            context.MapId != damage.Monster.Definition.MapId ||
            !TryGetWorldInstance(context, out var runtime))
        {
            return false;
        }

        var resolved = InvokeWorldOwner(
            runtime,
            map =>
            {
                var found = map.TryResolveMedusaMonsterRule(
                    damage.ObjectId,
                    damage.Monster.SpawnGeneration,
                    out var value);
                return (Found: found, Rule: value);
            });
        rule = resolved.Rule;
        return resolved.Found;
    }

    /// <summary>F-166: Medusa rolls as <see cref="RolledMonsterLoot"/> for CorpseLootLimit.KeepBest (chance kept,
    /// no attributes: Medusa drops carry none, so they rank in the Common quality tier).</summary>
    internal static IReadOnlyList<RolledMonsterLoot> MedusaRanking(IReadOnlyList<MedusaRolledLoot> rolled) =>
        rolled.Select(static drop => new RolledMonsterLoot(
                drop.LootIndex,
                drop.ItemId,
                drop.Quantity,
                ChanceBasisPoints: drop.ChanceBasisPoints))
            .ToArray();

    internal MonsterLootPresentation? PrepareMedusaMonsterLoot(
        ClientSession session,
        MonsterDamageResult damage,
        Guid deathEventId,
        DateTimeOffset diedAt)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(damage);
        if (deathEventId == Guid.Empty || !damage.Killed ||
            !_sessions.TryGetValue(session, out var context) ||
            !context.WorldReady ||
            context.MapId != damage.Monster.Definition.MapId ||
            !TryGetWorldInstance(context, out var runtime))
        {
            return null;
        }

        var prepared = InvokeWorldOwner(
            runtime,
            map =>
            {
                if (!map.TryResolveMedusaMonsterRule(
                        damage.ObjectId,
                        damage.Monster.SpawnGeneration,
                        out var rule))
                {
                    return default(PreparedMonsterLoot?);
                }

                var rolled = MedusaMonsterContentCatalog.Current.RollLoot(
                    rule.Difficulty,
                    rule.TemplateAlias,
                    deathEventId);
                // F-166: a Medusa corpse is held to the same 8-slot cap and keeps the best drops like every other
                // corpse. Ranking here is rarer rule chance, then template value (Money), then roll order. Quality
                // cannot take part: it is a per-roll attribute (ItemGrantAttributes) that Medusa loot rules do not
                // carry, and item templates (ItemBaseAttribute.xml) have no quality field, so every Medusa drop is
                // granted in the Common tier and ties on quality.
                var corpseRates = Godswar.Server.Application.Rates.ServerRates.Current;
                var corpseMaximum = Godswar.Server.Application.World.Content.CorpseLootLimit.Maximum(corpseRates);
                // LOOT PAGING: drop.corpse_pages 2 keeps up to 2 x the cap and hands it out one 10029 page at a time.
                var corpsePages = Godswar.Server.Application.World.Content.CorpseLootLimit.Pages(corpseRates);
                var best = Godswar.Server.Application.World.Content.CorpseLootLimit.KeepBestPaged(
                    MedusaRanking(rolled),
                    corpseMaximum,
                    corpsePages,
                    out var filtered);
                if (filtered > 0)
                {
                    Console.WriteLine(
                        $"[loot] corpse overflow filtered monster={damage.ObjectId} template={rule.TemplateAlias} medusa={rule.Difficulty} rolled={rolled.Count} kept={best.Count} dropped={filtered} death={deathEventId:N}");
                }
                var pages = Godswar.Server.Application.World.Content.CorpseLootLimit.Paginate(best.Select((drop, index) =>
                        new MonsterLootEntry(
                            index,
                            drop.LootIndex,
                            drop.ItemId,
                            drop.Quantity))
                    .ToArray(), corpseMaximum, corpsePages); // last guard: every page <= 8 (one page = the old Cap)
                var entries = pages[0];
                var delayMilliseconds = entries.Count == 0
                    ? rule.CorpseWithoutLootMilliseconds
                    : rule.CorpseWithLootMilliseconds;
                DateTimeOffset? expiresAt = delayMilliseconds.HasValue
                    ? diedAt + TimeSpan.FromMilliseconds(
                        delayMilliseconds.Value)
                    : null;
                if (!map.TrySetMonsterCorpseDespawnAt(
                        damage.ObjectId,
                        damage.Monster.SpawnGeneration,
                        expiresAt))
                {
                    return default(PreparedMonsterLoot?);
                }
                return new PreparedMonsterLoot(
                    rule,
                    pages,
                    expiresAt,
                    rule.CorpseWithLootMilliseconds is { } window ? TimeSpan.FromMilliseconds(window) : null);
            });
        if (prepared is null)
        {
            return null;
        }

        var key = new MonsterLootRuntimeKey(
            context.WorldInstanceId,
            damage.ObjectId);
        if (prepared.Pages[0].Count == 0)
        {
            _monsterLoot.TryRemove(key, out _);
            return new(
                damage.ObjectId,
                damage.Monster.SpawnGeneration,
                deathEventId,
                []);
        }

        var state = new MonsterLootRuntimeState(
            context.CharacterId,
            damage.Monster.SpawnGeneration,
            deathEventId,
            prepared.ExpiresAt,
            prepared.Pages,
            prepared.PageWindow,
            damage.Monster.X,
            damage.Monster.Z);
        _monsterLoot[key] = state;
        return new(
            damage.ObjectId,
            damage.Monster.SpawnGeneration,
            deathEventId,
            prepared.Pages[0]);
    }

    /// <summary>
    /// Rolls the database-owned drop table of the killed monster's template and
    /// starts tracking its ground items. Unlike the Medusa path this needs no
    /// instance rule: the table is keyed by the published monster template key,
    /// which is the same identity the client already received in its spawn
    /// frame.
    /// </summary>
    internal MonsterLootPresentation? PrepareMonsterLoot(
        ClientSession session,
        MonsterDamageResult damage,
        Guid deathEventId,
        DateTimeOffset diedAt)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(damage);
        if (deathEventId == Guid.Empty || !damage.Killed ||
            !_sessions.TryGetValue(session, out var context) ||
            !context.WorldReady ||
            context.MapId != damage.Monster.Definition.MapId ||
            !TryGetWorldInstance(context, out var runtime))
        {
            return null;
        }

        // AREA LOOT: one snapshot for both the lookup and the roll, and the
        // table is chosen per (template, map): "template@map" first, then the
        // plain template table (unchanged behaviour for every other monster).
        var lootContent = MonsterLootContentCatalog.Current;
        var lootKey = lootContent.ResolveTableKey(
            damage.Monster.Definition.TemplateKey,
            damage.Monster.Definition.MapId);
        // FINALISE-PETS: Cerberus on the farm drops a level 1-2 pet skill book half of the time, rolled in code
        // so the global drop.chance multiplier cannot change the 50%.
        var cerberusBook = LelantineFarmLootPolicy.IsCerberus(
                damage.Monster.Definition.MapId,
                damage.Monster.Definition.TemplateKey) &&
            LelantineFarmLootPolicy.TryRollCerberusBook(deathEventId, out var bookItemId)
                ? new RolledMonsterLoot(
                    LelantineFarmLootPolicy.CerberusBookLootIndex,
                    bookItemId,
                    1,
                    BoundOnPickup: false)
                : (RolledMonsterLoot?)null;
        // ROUND3 W3 Spring Festival: festival goods and firecrackers in the suburbs (maps 2 and 4) while it is open.
        var springDrops = Godswar.Server.Application.Seasons.SpringFestivalRules.Drops(
                damage.Monster.Definition.MapId, deathEventId, diedAt,
                Godswar.Server.Application.Finalise2.Finalise2Rates.Amount(
                    Godswar.Server.Application.Seasons.SpringFestivalRules.GoodsDropPercent),
                Godswar.Server.Application.Finalise2.Finalise2Rates.Amount(
                    Godswar.Server.Application.Seasons.SpringFestivalRules.FirecrackerDropPercent))
            .ToArray();
        // W3 seasons: Thanksgiving turkey items on Marathon / Peloponnese while the season is open.
        var seasonDrops = Godswar.Server.Application.Seasons.SeasonalEvents.ThanksgivingDrops(
                damage.Monster.Definition.MapId, deathEventId, diedAt,
                Godswar.Server.Application.Finalise2.Finalise2Rates.Amount(
                    Godswar.Server.Application.Finalise2.Finalise2RateKeys.ThanksgivingDropPercent),
                Godswar.Server.Application.Finalise2.Finalise2Rates.Amount(
                    Godswar.Server.Application.Finalise2.Finalise2RateKeys.ThanksgivingCardPercent))
            .Concat(Godswar.Server.Application.Seasons.OrientalCarnivalSeason.Drops( // W3 oriental/carnival
                damage.Monster.Definition.MapId, deathEventId, diedAt))
            .Concat(Godswar.Server.Application.Seasons.MidAutumnSeason.Drops(deathEventId, diedAt, // W3 mid-autumn
                Godswar.Server.Application.Finalise2.Finalise2Rates.Amount(
                    Godswar.Server.Application.Seasons.MidAutumnSeason.PortionPercentKey),
                Godswar.Server.Application.Finalise2.Finalise2Rates.Amount(
                    Godswar.Server.Application.Seasons.MidAutumnSeason.DelicatePercentKey)))
            // W3 Christmas: Antlers from Little Deer (Athens) / Vicious Fawn (Sparta suburbs) while it is open.
            .Concat(Godswar.Server.Application.Seasons.SeasonalEvents.ChristmasDrops(
                damage.Monster.Definition.MapId, damage.Monster.Definition.TemplateKey, deathEventId, diedAt,
                Godswar.Server.Application.Finalise2.Finalise2Rates.Amount(
                    Godswar.Server.Application.Finalise2.Finalise2RateKeys.ChristmasAntlerDropPercent)))
            .Concat(Godswar.Server.Application.Seasons.HalloweenMayDaySeason.Drops( // W3 halloween/may day
                deathEventId, diedAt))
            .ToArray();
        seasonDrops = [.. seasonDrops, .. Godswar.Server.Application.Seasons.EasterAnniversarySeason.Drops( // W3 easter/igg
            damage.Monster.Definition.MapId, deathEventId, diedAt)];
        // ROUND3 runtime spawns: a seal-box / scroll monster drops exactly its own lines (stacks of at most 99)
        // instead of its template's table, so a summoned boss can never farm an instance boss table.
        var runtimeSpawnDrops = ResolveWorldRuntimeSpawnDrops(runtime, damage);
        if (runtimeSpawnDrops is not null)
        {
            lootKey = null;
            cerberusBook = null;
            seasonDrops = [];
            springDrops = [];
        }
        if (lootKey is null && cerberusBook is null && seasonDrops.Length == 0 && springDrops.Length == 0 &&
            runtimeSpawnDrops is not { Count: > 0 })
        {
            return null;
        }

        // F-143 Magic Bell: 0.5% (GENERATED list of 15 boss/elite templates, in code), outside the table.
        var magicBell = lootKey is not null &&
            Godswar.Server.Application.World.Content.MagicBellLootRule.TryRoll(
                damage.Monster.Definition.TemplateKey, deathEventId,
                Godswar.Server.Application.Rates.ServerRates.Current);

        // FINALISE: per-map/tier "drops anything at all" gate (drop_tier_overrides; no row = always roll).
        // The FINALISE-PETS Cerberus book below is rolled on its own (the admin's 50%), outside this gate.
        IReadOnlyList<RolledMonsterLoot> tableDrops = runtimeSpawnDrops is not null
            ? runtimeSpawnDrops.Take(32)
                .Select((drop, index) => new RolledMonsterLoot(index, drop.ItemId, drop.Quantity, BoundOnPickup: null))
                .ToArray()
            : lootKey is null ||
            Godswar.Server.Application.Npc.MonsterTierOverrides.SkipsAllLoot(
                damage.Monster.Definition.MapId,
                damage.Monster.Definition.TemplateKey,
                deathEventId)
            ? []
            : lootContent.RollLoot(
                lootKey,
                deathEventId,
                diedAt); // EVENT DROPS (q07)
        var merged = Godswar.Server.Application.World.Content.CorpseLootLimit.Merge(
            tableDrops,
            cerberusBook,
            magicBell
                ? (int)Math.Clamp(Godswar.Server.Application.Rates.ServerRates.Current.Get(
                    Godswar.Server.Application.Finalise2.Finalise2RateKeys.MagicBellDropChance), 0m, 10_000m)
                : null,
            seasonDrops.Select(static drop => drop.Item1),
            springDrops.Select(static drop => drop.Item1)); // ROUND3 W3 Spring Festival
        // F-166 (b): at most CorpseLootLimit.Maximum drops (8: the client's 10029 window overflows at 9).
        // F-128 first: the Atlantis Mermaid's kill never drops a Morning Dew (only a net capture pays). It is removed
        // BEFORE the cap, so a withheld dew never takes a slot; then the best of what can drop is kept and the
        // commonest overflow goes.
        // LOOT PAGING: drop.corpse_pages 2 keeps up to 2 x the cap (16) and hands it out one 10029 page at a time;
        // 1 (default) is exactly the single 8-slot window.
        var corpseRates = Godswar.Server.Application.Rates.ServerRates.Current;
        var corpseMaximum = Godswar.Server.Application.World.Content.CorpseLootLimit.Maximum(corpseRates);
        var corpsePages = Godswar.Server.Application.World.Content.CorpseLootLimit.Pages(corpseRates);
        var rolled = Godswar.Server.Application.World.Content.CorpseLootLimit.KeepBestDroppable(
            merged,
            damage.Monster.Definition.MapId,
            damage.Monster.Definition.TemplateKey,
            corpseMaximum,
            out var withheld,
            out var filtered,
            corpsePages);
        if (withheld > 0)
        {
            Console.WriteLine($"[atlantis] mermaid kill Morning Dew withheld monster={damage.ObjectId} death={deathEventId:N}");
        }
        if (filtered > 0)
        {
            Console.WriteLine(
                $"[loot] corpse overflow filtered monster={damage.ObjectId} template={damage.Monster.Definition.TemplateKey} rolled={merged.Count - withheld} kept={rolled.Count} dropped={filtered} death={deathEventId:N}");
        }

        // The extras' log lines name only drops the corpse really lists (an extra cut by the cap is counted in the
        // overflow line above).
        var tableIndexes = tableDrops.Select(static drop => drop.LootIndex).ToHashSet();
        foreach (var extra in rolled.Where(drop => !tableIndexes.Contains(drop.LootIndex)))
        {
            var tag = cerberusBook is { } book && extra.LootIndex == book.LootIndex && extra.ItemId == book.ItemId ? "farm"
                : magicBell && extra.ItemId == Godswar.Server.Application.World.Content.MagicBellLootRule.ItemId ? "magic-bell"
                : "season";
            Console.WriteLine(
                $"[{tag}] {tag} drop monster={damage.ObjectId} template={damage.Monster.Definition.TemplateKey} item={extra.ItemId} death={deathEventId:N}");
        }

        var key = new MonsterLootRuntimeKey(
            context.WorldInstanceId,
            damage.ObjectId);
        if (rolled.Count == 0)
        {
            _monsterLoot.TryRemove(key, out _);
            return null;
        }

        var pages = Godswar.Server.Application.World.Content.CorpseLootLimit.Paginate(rolled.Select((drop, index) =>
                new MonsterLootEntry(
                    index,
                    drop.LootIndex,
                    drop.ItemId,
                    drop.Quantity,
                    drop.BoundOnPickup,
                    drop.Attributes))
            .ToArray(), corpseMaximum, corpsePages); // F-166: last guard, every page <= 8 (one page = the old Cap)
        var entries = pages[0];
        // The client only opens a corpse's loot window while that corpse is on
        // screen, so a looted field corpse keeps the captured window instead of
        // the five-second default. The reference kept the corpse of a looted
        // Athens kill (object 10492) for 20.06 s.
        var expiresAt = diedAt +
            TimeSpan.FromMilliseconds(CorpseWithLootMilliseconds);
        var corpseAccepted = InvokeWorldOwner(
            runtime,
            map => map.TrySetMonsterCorpseDespawnAt(
                damage.ObjectId,
                damage.Monster.SpawnGeneration,
                expiresAt));
        if (!corpseAccepted)
        {
            MonsterLootTrace.Log(
                $"corpse-window-rejected map={context.MapId} monster={damage.ObjectId} generation={damage.Monster.SpawnGeneration}");
        }

        _monsterLoot[key] = new MonsterLootRuntimeState(
            context.CharacterId,
            damage.Monster.SpawnGeneration,
            deathEventId,
            expiresAt,
            pages,
            TimeSpan.FromMilliseconds(CorpseWithLootMilliseconds),
            damage.Monster.X,
            damage.Monster.Z);
        MonsterLootTrace.Log(
            $"prepared map={context.MapId} monster={damage.ObjectId} corpse=({damage.Monster.X:F2},{damage.Monster.Z:F2}) drops={entries.Count} corpseWindow={corpseAccepted} expires={expiresAt:O}");
        if (pages.Count > 1)
        {
            Console.WriteLine(
                $"[loot] corpse paged monster={damage.ObjectId} template={damage.Monster.Definition.TemplateKey} pages={pages.Count} drops={rolled.Count} first={entries.Count} death={deathEventId:N}");
        }
        return new(
            damage.ObjectId,
            damage.Monster.SpawnGeneration,
            deathEventId,
            entries);
    }

    /// <summary>
    /// True when this session still owns an unclaimed ground item on the given
    /// corpse at the drop index the client clicked, which is what the client's
    /// corpse click has to be answered for. The captured reply echoes that same
    /// index, so an index the corpse never dropped must stay unanswered.
    /// </summary>
    internal bool HasPendingMonsterLoot(
        ClientSession session,
        uint monsterObjectId,
        int dropIndex)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (monsterObjectId == 0 || dropIndex < 0 ||
            !_sessions.TryGetValue(session, out var context))
        {
            return false;
        }

        var key = new MonsterLootRuntimeKey(
            context.WorldInstanceId,
            monsterObjectId);
        if (!_monsterLoot.TryGetValue(key, out var state) ||
            state.ClaimantCharacterId != context.CharacterId)
        {
            return false;
        }

        lock (state.Gate)
        {
            return state.Entries.ContainsKey(dropIndex) &&
                !state.Pending.ContainsKey(dropIndex);
        }
    }

    internal bool TryReserveMonsterLootPickup(
        ClientSession session,
        uint monsterObjectId,
        int pickupIndex,
        DateTimeOffset now,
        out MonsterLootPickupReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(session);
        reservation = default!;
        if (monsterObjectId == 0 || pickupIndex is < 0 or >= 32 ||
            !_sessions.TryGetValue(session, out var context) ||
            !context.WorldReady ||
            !TryGetWorldInstance(context, out var runtime))
        {
            return false;
        }

        var key = new MonsterLootRuntimeKey(
            context.WorldInstanceId,
            monsterObjectId);
        if (!_monsterLoot.TryGetValue(key, out var state))
        {
            return false;
        }

        return TryReserveLootEntry(
            session,
            context,
            runtime,
            key,
            state,
            pickupIndex,
            now,
            out reservation);
    }

    /// <summary>
    /// Reserves a ground item for the installed client's pickup request. That
    /// request carries the item id and the destination bag slot but not the
    /// corpse, so the owning loot state is found by the echoed ground key when
    /// the client sends one, and by item id otherwise.
    /// </summary>
    internal bool TryReserveGroundLootPickup(
        ClientSession session,
        uint itemId,
        uint groundKeyHigh,
        uint groundKeyLow,
        DateTimeOffset now,
        out MonsterLootPickupReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(session);
        reservation = default!;
        if (itemId == 0 ||
            !_sessions.TryGetValue(session, out var context) ||
            !context.WorldReady ||
            !TryGetWorldInstance(context, out var runtime))
        {
            return false;
        }

        var hasGroundKey = groundKeyHigh != 0 || groundKeyLow != 0;
        var candidates = new List<(
            MonsterLootRuntimeKey Key,
            MonsterLootRuntimeState State,
            int PickupIndex)>();
        foreach (var pair in _monsterLoot)
        {
            var state = pair.Value;
            if (state.ClaimantCharacterId != context.CharacterId ||
                pair.Key.WorldInstanceId != context.WorldInstanceId)
            {
                continue;
            }

            foreach (var entry in state.Entries)
            {
                var key = MonsterLootGroundKey.Resolve(
                    state.DeathEventId,
                    entry.Value.RuleLootIndex);
                var matchesKey =
                    hasGroundKey &&
                    key.High == groundKeyHigh &&
                    key.Low == groundKeyLow;
                if (matchesKey || entry.Value.ItemId == itemId)
                {
                    candidates.Add((pair.Key, state, entry.Key));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        // An exact ground-key match wins; otherwise the item id decides, which
        // is all the client sends for a normal pickup.
        candidates.Sort(static (left, right) =>
            left.PickupIndex.CompareTo(right.PickupIndex));
        if (hasGroundKey)
        {
            foreach (var candidate in candidates)
            {
                var key = MonsterLootGroundKey.Resolve(
                    candidate.State.DeathEventId,
                    candidate.State.Entries[candidate.PickupIndex]
                        .RuleLootIndex);
                if (key.High == groundKeyHigh && key.Low == groundKeyLow)
                {
                    return TryReserveLootEntry(
                        session,
                        context,
                        runtime,
                        candidate.Key,
                        candidate.State,
                        candidate.PickupIndex,
                        now,
                        out reservation);
                }
            }
        }

        foreach (var candidate in candidates)
        {
            if (TryReserveLootEntry(
                    session,
                    context,
                    runtime,
                    candidate.Key,
                    candidate.State,
                    candidate.PickupIndex,
                    now,
                    out reservation))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryReserveLootEntry(
        ClientSession session,
        GameSessionContext context,
        WorldInstanceRuntime runtime,
        MonsterLootRuntimeKey key,
        MonsterLootRuntimeState state,
        int pickupIndex,
        DateTimeOffset now,
        out MonsterLootPickupReservation reservation)
    {
        reservation = default!;
        // The corpse is already gone from the map runtime: a field kill clears
        // both IsAlive and IsSpawned as soon as the lethal hit commits, so the
        // pickup has to validate against the death position captured with the
        // drop instead of a live monster snapshot. A live snapshot is still
        // used while the corpse exists so a respawned generation cannot hand
        // out the old loot.
        var corpseX = state.CorpseX;
        var corpseZ = state.CorpseZ;
        var monsterAttempt = InvokeWorldOwner(
            runtime,
            map =>
            {
                var found = map.TryGetMonsterSnapshot(
                    key.MonsterObjectId,
                    out var monster);
                return (Found: found, Monster: monster);
            });
        if (monsterAttempt.Found)
        {
            var live = monsterAttempt.Monster;
            if (live.SpawnGeneration != state.SpawnGeneration)
            {
                return false;
            }

            if (live.IsSpawned)
            {
                corpseX = live.X;
                corpseZ = live.Z;
            }
        }

        if (DistanceSquared(
                context.Character.PositionX,
                context.Character.PositionZ,
                corpseX,
                corpseZ) >
            MonsterLootPickupRadius * MonsterLootPickupRadius)
        {
            return false;
        }

        lock (state.Gate)
        {
            if (state.ClaimantCharacterId != context.CharacterId ||
                state.ExpiresAt is { } expiresAt && now >= expiresAt ||
                !state.Entries.TryGetValue(pickupIndex, out var entry) ||
                state.Pending.ContainsKey(pickupIndex))
            {
                if (state.ExpiresAt is { } expired && now >= expired)
                {
                    _monsterLoot.TryRemove(
                        new KeyValuePair<
                            MonsterLootRuntimeKey,
                            MonsterLootRuntimeState>(key, state));
                }
                return false;
            }

            var attemptId = Guid.NewGuid();
            state.Pending.Add(pickupIndex, attemptId);
            reservation = new(
                key.WorldInstanceId,
                key.MonsterObjectId,
                state.SpawnGeneration,
                state.DeathEventId,
                pickupIndex,
                entry.RuleLootIndex,
                entry.ItemId,
                entry.Quantity,
                attemptId,
                entry.BoundOnPickup,
                entry.Attributes);
            return true;
        }
    }

    /// <summary>
    /// Settles a paid pickup. LOOT PAGING: when it emptied the current page and the corpse has a later page
    /// (drop.corpse_pages 2), that page becomes current, the corpse window restarts (state and map corpse) and the
    /// page is returned for the caller to send as a fresh 10029 after its pickup ack. Otherwise null (one page:
    /// always null, as before).
    /// </summary>
    internal MonsterLootPresentation? CompleteMonsterLootPickup(
        MonsterLootPickupReservation reservation,
        DateTimeOffset? now = null)
    {
        var key = new MonsterLootRuntimeKey(
            reservation.WorldInstanceId,
            reservation.MonsterObjectId);
        if (!_monsterLoot.TryGetValue(key, out var state))
        {
            return null;
        }

        IReadOnlyList<MonsterLootEntry>? nextPage;
        DateTimeOffset? nextExpiresAt;
        lock (state.Gate)
        {
            if (!state.Pending.TryGetValue(
                    reservation.PickupIndex,
                    out var attemptId) ||
                attemptId != reservation.AttemptId)
            {
                return null;
            }
            state.Pending.Remove(reservation.PickupIndex);
            state.Entries.Remove(reservation.PickupIndex);
            if (state.Entries.Count != 0)
            {
                return null;
            }

            nextPage = state.Pages.TryAdvance();
            if (nextPage is null)
            {
                _monsterLoot.TryRemove(
                    new KeyValuePair<
                        MonsterLootRuntimeKey,
                        MonsterLootRuntimeState>(key, state));
                return null;
            }

            // The corpse window restarts for every page (TIMER: reset per page), so page 2 gets the full window.
            if (state.PageWindow is { } window)
            {
                state.ExpiresAt = (now ?? DateTimeOffset.UtcNow) + window;
            }
            nextExpiresAt = state.ExpiresAt;
        }

        // The client removes the corpse object (10023) when the map's despawn time passes, so the map corpse is
        // extended too; if it is already gone the next page cannot be shown and the corpse's loot ends here.
        var corpseKept = WorldInstances.TryFind(key.WorldInstanceId, out var runtime) &&
            InvokeWorldOwner(
                runtime,
                map => map.TrySetMonsterCorpseDespawnAt(
                    key.MonsterObjectId,
                    state.SpawnGeneration,
                    nextExpiresAt));
        if (!corpseKept)
        {
            _monsterLoot.TryRemove(
                new KeyValuePair<
                    MonsterLootRuntimeKey,
                    MonsterLootRuntimeState>(key, state));
            Console.WriteLine(
                $"[loot] corpse page lost monster={key.MonsterObjectId} page={state.Pages.PageNumber} drops={nextPage.Count} death={state.DeathEventId:N} (corpse gone)");
            return null;
        }

        Console.WriteLine(
            $"[loot] corpse next page monster={key.MonsterObjectId} page={state.Pages.PageNumber}/{state.Pages.PageCount} drops={nextPage.Count} expires={nextExpiresAt:O} death={state.DeathEventId:N}");
        return new(
            key.MonsterObjectId,
            state.SpawnGeneration,
            state.DeathEventId,
            nextPage);
    }

    internal void ReleaseMonsterLootPickup(
        MonsterLootPickupReservation reservation)
    {
        var key = new MonsterLootRuntimeKey(
            reservation.WorldInstanceId,
            reservation.MonsterObjectId);
        if (!_monsterLoot.TryGetValue(key, out var state))
        {
            return;
        }
        lock (state.Gate)
        {
            if (state.Pending.TryGetValue(
                    reservation.PickupIndex,
                    out var attemptId) &&
                attemptId == reservation.AttemptId)
            {
                state.Pending.Remove(reservation.PickupIndex);
            }
        }
    }

    private static float DistanceSquared(
        float leftX,
        float leftZ,
        float rightX,
        float rightZ)
    {
        var deltaX = leftX - rightX;
        var deltaZ = leftZ - rightZ;
        return (deltaX * deltaX) + (deltaZ * deltaZ);
    }

    private readonly record struct MonsterLootRuntimeKey(
        WorldInstanceId WorldInstanceId,
        uint MonsterObjectId);

    private sealed class MonsterLootRuntimeState
    {
        public MonsterLootRuntimeState(
            int claimantCharacterId,
            uint spawnGeneration,
            Guid deathEventId,
            DateTimeOffset? expiresAt,
            IReadOnlyList<IReadOnlyList<MonsterLootEntry>> pages,
            TimeSpan? pageWindow,
            float corpseX,
            float corpseZ)
        {
            ClaimantCharacterId = claimantCharacterId;
            SpawnGeneration = spawnGeneration;
            DeathEventId = deathEventId;
            ExpiresAt = expiresAt;
            Pages = new CorpseLootPages(pages);
            PageWindow = pageWindow;
            CorpseX = corpseX;
            CorpseZ = corpseZ;
        }

        public object Gate { get; } = new();
        public int ClaimantCharacterId { get; }
        public uint SpawnGeneration { get; }
        public Guid DeathEventId { get; }
        /// <summary>Written only under <see cref="Gate"/> (a new loot page restarts the window).</summary>
        public DateTimeOffset? ExpiresAt { get; set; }

        /// <summary>LOOT PAGING: the corpse window each later page restarts (null = the corpse never expires).</summary>
        public TimeSpan? PageWindow { get; }

        public CorpseLootPages Pages { get; }

        /// <summary>
        /// Where the corpse died. The map runtime despawns a field corpse as
        /// soon as the lethal hit commits, so the ground items keep their own
        /// position for the pickup radius check.
        /// </summary>
        public float CorpseX { get; }
        public float CorpseZ { get; }

        /// <summary>The current 10029 page, by page-local pickup index (LOOT PAGING: later pages stay hidden).</summary>
        public Dictionary<int, MonsterLootEntry> Entries => Pages.Current;
        public Dictionary<int, Guid> Pending { get; } = [];
    }

    private sealed record PreparedMonsterLoot(
        MedusaMonsterRule Rule,
        IReadOnlyList<IReadOnlyList<MonsterLootEntry>> Pages,
        DateTimeOffset? ExpiresAt,
        TimeSpan? PageWindow);
}

internal readonly record struct MonsterLootEntry(
    int PickupIndex,
    int RuleLootIndex,
    uint ItemId,
    int Quantity,
    bool? BoundOnPickup = null,
    ItemGrantAttributes Attributes = default);

internal sealed record MonsterLootPresentation(
    uint MonsterObjectId,
    uint SpawnGeneration,
    Guid DeathEventId,
    IReadOnlyList<MonsterLootEntry> Entries);

internal sealed record MonsterLootPickupReservation(
    WorldInstanceId WorldInstanceId,
    uint MonsterObjectId,
    uint SpawnGeneration,
    Guid DeathEventId,
    int PickupIndex,
    int RuleLootIndex,
    uint ItemId,
    int Quantity,
    Guid AttemptId,
    bool? BoundOnPickup = null,
    ItemGrantAttributes Attributes = default);
