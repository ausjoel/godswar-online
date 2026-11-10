using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private static bool RequiresOpalPayment(
        LegacyInstanceDailyEntryClaimResult claim) =>
        claim.PaymentRequiredCharacterIds.Count != 0;

    private async Task<LegacyInstanceOpalChargeResult?>
        TryChargeLegacyInstanceOpalsAsync(
            Guid reservationId,
            LegacyInstancePartySnapshot party,
            LegacyInstanceDailyEntryClaimResult claim,
            DateTimeOffset chargedAtUtc,
            CancellationToken cancellationToken)
    {
        if (_legacyInstanceOpalPayments is null)
        {
            return null;
        }
        var payers = party.Members
            .Where(member =>
                claim.PaymentRequiredCharacterIds.Contains(
                    member.CharacterId))
            .Select(static member => new LegacyInstanceOpalPayer(
                member.AccountId,
                member.CharacterId,
                member.Ownership))
            .ToArray();
        if (payers.Length != claim.PaymentRequiredCharacterIds.Count)
        {
            throw new InvalidDataException(
                "The Atlantis claim named a payer outside the party.");
        }
        return await _legacyInstanceOpalPayments.ChargeAsync(
            new LegacyInstanceOpalChargeRequest(
                reservationId,
                party.RealmId,
                payers,
                chargedAtUtc.ToUniversalTime()),
            cancellationToken);
    }

    private async Task<bool> ProjectLegacyInstanceOpalMutationsAsync(
        LegacyInstancePartySnapshot party,
        IReadOnlyList<LegacyInstanceOpalInventoryMutation> mutations)
    {
        foreach (var mutation in mutations)
        {
            var member = party.Members.SingleOrDefault(candidate =>
                candidate.CharacterId == mutation.CharacterId);
            if (member is null ||
                !_registry.TryProjectLegacyInstanceOpalMutation(
                    member,
                    mutation,
                    out var evictions,
                    out var details,
                    out var indexes))
            {
                Console.Error.WriteLine(
                    "[instance-caller] Atlantis Opal projection rejected " +
                    $"character={mutation.CharacterId}");
                return false;
            }

            foreach (var packet in evictions)
            {
                await member.Session.SendAsync(
                    packet,
                    CancellationToken.None,
                    "AtlantisOpalKitBagEviction");
            }
            foreach (var packet in details)
            {
                await member.Session.SendAsync(
                    packet,
                    CancellationToken.None,
                    "AtlantisOpalKitBagDetail");
            }
            foreach (var packet in indexes)
            {
                await member.Session.SendAsync(
                    packet,
                    CancellationToken.None,
                    "AtlantisOpalKitBagIndex");
            }
        }
        return true;
    }

    private async Task<bool> SettleLegacyInstanceOpalsAsync(
        Guid reservationId,
        LegacyInstancePartySnapshot party,
        IReadOnlyCollection<int> admittedCharacterIds)
    {
        if (_legacyInstanceOpalPayments is null)
        {
            return false;
        }
        var settlementWasRetried = false;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var result = await _legacyInstanceOpalPayments.SettleAsync(
                    reservationId,
                    admittedCharacterIds,
                    CancellationToken.None);
                if (!settlementWasRetried)
                {
                    if (await ProjectLegacyInstanceOpalMutationsAsync(
                        party,
                        result.RefundMutations))
                    {
                        return true;
                    }
                    return await ReloadLegacyInstanceOpalKitBagsAsync(
                        party);
                }
                return await ReloadLegacyInstanceOpalKitBagsAsync(party);
            }
            catch (Exception error)
            {
                settlementWasRetried = true;
                Console.Error.WriteLine(
                    "[instance-caller] Atlantis Opal settlement failed " +
                    $"reservation={reservationId} attempt={attempt}: " +
                    error.Message);
            }
        }
        return false;
    }

    private async Task<bool> RecordLegacyInstanceOpalAdmissionsAsync(
        Guid reservationId,
        IReadOnlyCollection<int> admittedCharacterIds)
    {
        if (_legacyInstanceOpalPayments is null)
        {
            return false;
        }
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await _legacyInstanceOpalPayments.RecordAdmissionsAsync(
                    reservationId,
                    admittedCharacterIds,
                    CancellationToken.None);
                return true;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(
                    "[instance-caller] Atlantis admission marker failed " +
                    $"reservation={reservationId} attempt={attempt}: " +
                    error.Message);
            }
        }
        return false;
    }

    private async Task<bool> ReloadLegacyInstanceOpalKitBagsAsync(
        LegacyInstancePartySnapshot party)
    {
        var projected = true;
        foreach (var member in party.Members)
        {
            if (member.RealmId != _processRealmId)
            {
                projected = false;
                continue;
            }
            var account = await _characterSnapshots.ReadAsync(
                member.AccountId,
                _processRealmId,
                CancellationToken.None);
            var hydrated = CharacterLoadSnapshotHydrator.Hydrate(account);
            if (hydrated is null ||
                hydrated.Character.Id != member.CharacterId ||
                !_registry.TryProjectLegacyInstanceOpalKitBag(
                    member,
                    hydrated.Character.KitBag,
                    out var evictions,
                    out var details,
                    out var indexes))
            {
                projected = false;
                continue;
            }
            foreach (var packet in evictions.Concat(details).Concat(indexes))
            {
                await member.Session.SendAsync(
                    packet,
                    CancellationToken.None,
                    "AtlantisOpalAuthoritativeKitBag");
            }
        }
        return projected;
    }

    private Task SendAtlantisEntryPolicyAsync(
        uint npcId,
        CancellationToken cancellationToken) =>
        _session.SendAsync(
            Packets.PacketBuilder.NpcFunctionActionResponse(
                npcId,
                InstanceCallerProtocol.DialogIndex,
                InstanceCallerProtocol.AtlantisEntryPolicyResultSubId),
            cancellationToken,
            "AtlantisEntryPolicy");

    private Task SendAtlantisInsufficientOpalAsync(
        uint npcId,
        CancellationToken cancellationToken) =>
        _session.SendAsync(
            Packets.PacketBuilder.NpcFunctionActionResponse(
                npcId,
                InstanceCallerProtocol.DialogIndex,
                InstanceCallerProtocol.AtlantisInsufficientOpalResultSubId),
            cancellationToken,
            "AtlantisInsufficientOpal");

    private Task SendAtlantisOpalConsentRecordedAsync(
        uint npcId,
        CancellationToken cancellationToken) =>
        _session.SendAsync(
            Packets.PacketBuilder.NpcFunctionActionResponse(
                npcId,
                InstanceCallerProtocol.DialogIndex,
                InstanceCallerProtocol
                    .AtlantisOpalConsentRecordedResultSubId),
            cancellationToken,
            "AtlantisOpalConsentRecorded");

    private Task SendAtlantisOpalConsentMissingAsync(
        uint npcId,
        CancellationToken cancellationToken) =>
        _session.SendAsync(
            Packets.PacketBuilder.NpcFunctionActionResponse(
                npcId,
                InstanceCallerProtocol.DialogIndex,
                InstanceCallerProtocol
                    .AtlantisOpalConsentMissingResultSubId),
            cancellationToken,
            "AtlantisOpalConsentMissing");
}
