using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

// PETNET: Treasure Land (map 25) net capture. A net (Wooden Tool .. Mysterious Tuck Net) thrown at a Treasure Land
// monster casts for six seconds like the pet capture; a catch removes the monster and gives a Travel Pack, a miss only
// uses up the net. Rules and sources: Application/WorldInstances/TreasureLandCapturePolicy.cs. Off while the server rate
// pet.treasure_land_capture is 0 (then the request falls through to the pet path, which refuses map 25 as before).
internal sealed partial class GameClientHandler
{
    private async Task<bool> TryHandleTreasureLandCaptureAsync(PetCaptureRequest request,
        CancellationToken cancellationToken)
    {
        if (_character is not { } character || character.CurrentMap != TreasureLandCapturePolicy.MapId ||
            !TreasureLandCapturePolicy.Enabled)
        {
            return false;
        }

        if (TreasureLandCaptureRefusal(character, request, out var target, out var rank) is { } refusal)
        {
            Console.WriteLine($"[treasure-capture] rejected reason={refusal} character={character.Name} " +
                $"target={request.TargetObjectId} slot={request.KitBagSlot}");
            await SendPetCaptureCastEndAsync(broadcastToWorld: false, cancellationToken);
            return true;
        }

        var control = ResolvePlayerSkillCastControl(DateTimeOffset.UtcNow);
        if (control != PlayerSkillCastControl.None)
        {
            await SendBlockedSkillCastNoticeAsync(control, cancellationToken);
            return true;
        }

        var mapId = character.CurrentMap;
        var netItemId = KitBagSlots.GetItem(character.KitBag, request.KitBagSlot).Id;
        var castSkillId = PetCaptureSkillIdFor(netItemId);
        var selfCastStart = PacketBuilder.MonsterSkillCastVisual(LocalPlayerObjectId, target.ObjectId, castSkillId,
            character.PositionX, character.PositionZ, target.X, target.Z);
        var worldCastStart = PacketBuilder.MonsterSkillCastVisual(CurrentPlayerObjectId, target.ObjectId, castSkillId,
            character.PositionX, character.PositionZ, target.X, target.Z);
        var started = await TryBeginPendingSkillCastAsync(
            castSkillId,
            PetCaptureCastTime,
            "treasure_land_capture",
            async token =>
            {
                if (!await _registry.DeliverMonsterCastStartToViewerAdmissionAsync(_session, mapId, target.ObjectId,
                        selfCastStart, target.SpawnGeneration, token, "TreasureCaptureCastStartSelf"))
                {
                    throw new InvalidOperationException("The Treasure Land capture cast start could not be admitted.");
                }

                await _registry.BroadcastMonsterCastStartToViewersAdmissionAsync(_session, mapId, target.ObjectId,
                    worldCastStart, target.SpawnGeneration, token, "TreasureCaptureCastStartWorld");
            },
            token => CompleteTreasureLandCaptureAsync(character, request, target, rank, netItemId, token),
            cancellationToken,
            () => IsTreasureLandCaptureCompletionValid(character, request, target, netItemId));
        if (!started)
        {
            await SendPetCaptureCastEndAsync(broadcastToWorld: false, cancellationToken);
        }

        return true;
    }

    /// <summary>Why this throw cannot start (null = it can), with the target and its rank.</summary>
    private string? TreasureLandCaptureRefusal(GameCharacter character, PetCaptureRequest request,
        out MonsterRuntimeSnapshot target, out TreasureLandCapturePolicy.Rank rank)
    {
        target = default!;
        rank = TreasureLandCapturePolicy.Rank.Normal;
        if (_account is null || character.CurrentHp <= 0)
        {
            return "no-account-or-dead";
        }

        var net = KitBagSlots.GetItem(character.KitBag, request.KitBagSlot);
        if (net.Id is < WoodenToolItemId or > MysteriousTuckNetItemId || net.Stack <= 0)
        {
            return "not-a-net";
        }

        // The pack needs room: the net's own slot frees up when it is the last one, otherwise an empty slot.
        if (net.Stack != 1 && !Enumerable.Range(0, 96).Any(slot => KitBagSlots.GetItem(character.KitBag, slot).IsEmpty))
        {
            return "bag-full";
        }

        if (!_registry.TryGetMonsterSnapshot(_session, character.CurrentMap, request.TargetObjectId, out var candidate) ||
            !candidate.IsAlive || !candidate.IsSpawned ||
            !_registry.IsMonsterVisibleTo(_session, candidate.ObjectId, candidate.SpawnGeneration))
        {
            return "no-target";
        }

        if (!TreasureLandCapturePolicy.TryGetRank(character.CurrentMap, candidate.Definition.TemplateKey, out rank))
        {
            return "not-catchable";
        }

        if (!IsWithinPetCaptureRange(character, candidate))
        {
            return "out-of-range";
        }

        if (!TreasureLandCapturePolicy.LevelAllows(character.Level, candidate.Definition.Tier))
        {
            return "level";
        }

        target = candidate;
        return null;
    }

    private bool IsTreasureLandCaptureCompletionValid(GameCharacter character, PetCaptureRequest request,
        MonsterRuntimeSnapshot expected, uint netItemId)
    {
        string? reason;
        if (_character != character || !TreasureLandCapturePolicy.Enabled)
        {
            reason = "character-or-switch";
        }
        else if (TreasureLandCaptureRefusal(character, request, out var current, out _) is { } refusal)
        {
            reason = refusal;
        }
        else if (KitBagSlots.GetItem(character.KitBag, request.KitBagSlot).Id != netItemId)
        {
            reason = "net-changed";
        }
        else if (current.RuntimeInstanceId != expected.RuntimeInstanceId ||
                 current.SpawnGeneration != expected.SpawnGeneration ||
                 current.HealthRevision != expected.HealthRevision)
        {
            reason = "target-changed";
        }
        else
        {
            return true;
        }

        Console.WriteLine($"[treasure-capture] completion invalid reason={reason} character={character.Name} " +
            $"target={request.TargetObjectId}");
        return false;
    }

    private async Task CompleteTreasureLandCaptureAsync(GameCharacter character, PetCaptureRequest request,
        MonsterRuntimeSnapshot target, TreasureLandCapturePolicy.Rank rank, uint netItemId,
        CancellationToken cancellationToken)
    {
        var outcome = "unavailable";
        try
        {
            var chance = TreasureLandCapturePolicy.CaptureChance(netItemId, rank);
            var caught = Random.Shared.NextDouble() < chance;
            if (caught && !_registry.TryCaptureMonster(_session, target, DateTimeOffset.UtcNow, out _))
            {
                // Someone else caught or killed it first: the net is kept.
                outcome = "claim-lost";
                return;
            }

            if (caught)
            {
                await _registry.BroadcastToCurrentWorldInstanceAsync(_session,
                    PacketBuilder.RemoveWorldObjects(target.ObjectId), CancellationToken.None,
                    includeRoutingSession: true, label: "TreasureCapturedRemove");
            }

            var pack = TreasureLandCapturePolicy.PackFor(rank);
            var granted = new List<(int ItemId, int Quantity)>();
            outcome = await RunEventsNpcAsync(async tx =>
            {
                if (await tx.ReadSlotAsync(request.KitBagSlot) is not { } slot || slot.ItemId != (int)netItemId ||
                    slot.Stack <= 0 || !await tx.ConsumeAsync((int)netItemId, 1))
                {
                    tx.Rollback();
                    return "net-gone";
                }

                if (caught && !await tx.GrantAsync((int)pack, TreasureLandCapturePolicy.PackQuantity, bound: null))
                {
                    tx.Rollback();
                    return "bag-full";
                }

                if (caught)
                {
                    granted.Add(((int)pack, TreasureLandCapturePolicy.PackQuantity));
                }

                return caught ? "caught" : "missed";
            }, "store-unavailable", cancellationToken);

            // Repaint the bag; only a received pack is announced (no notice for the used-up net).
            await RefreshAfterEventsNpcAsync(granted, cancellationToken);
            Console.WriteLine($"[treasure-capture] {outcome} character={character.Name} target={target.ObjectId} " +
                $"template={target.Definition.TemplateKey} level={target.Definition.Tier} rank={rank} " +
                $"net={netItemId} chance={chance:0.00} pack={(granted.Count > 0 ? pack : 0)}");
        }
        finally
        {
            if (outcome is "claim-lost" or "unavailable")
            {
                Console.WriteLine($"[treasure-capture] {outcome} character={character.Name} target={target.ObjectId}");
            }

            await SendPetCaptureCastEndAsync(broadcastToWorld: true, CancellationToken.None);
        }
    }
}
