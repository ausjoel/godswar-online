using System.Text.Json.Nodes;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Pets;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

// W6A (the admin 2026-11-13): "Login check" - can this account's character enter the world?
//
// A dry run of what a login does, without the player and without a connection: read the character snapshot the
// way the game login does, validate and hydrate it, and build the world-entry packets into memory (they are
// thrown away). Every failure is caught and returned with the stage it happened in. It writes nothing.
//
// severity "blocks": the character cannot log in until this is fixed.
// severity "hidden": the login works; this item or pet is left out of what the client is sent until it is fixed
//                    (PacketBuilder.ItemEntryGuard), and stays in the database.
// Not covered: the map, NPC and other-player packets that depend on who else is online.
internal static class CharacterEntryCheck
{
    internal const int MaximumAccountsPerCall = 500;

    private static ICharacterSnapshotReader? _snapshots;
    private static IPetContentCatalog? _petContent;
    private static RealmId _realmId;

    public static bool Available => _snapshots is not null;

    public static void Configure(ICharacterSnapshotReader snapshots, RealmId realmId, IPetContentCatalog? petContent)
    {
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _realmId = realmId;
        _petContent = petContent;
    }

    public static async Task<JsonObject> RunAsync(int accountId, CancellationToken cancellationToken)
    {
        var findings = new JsonArray();
        var result = new JsonObject { ["accountId"] = accountId };
        var snapshots = _snapshots ?? throw new InvalidOperationException("The login check is not configured.");
        HydratedCharacterLoadSnapshot? hydrated = null;
        try
        {
            var snapshot = await snapshots.ReadAsync(accountId, _realmId, cancellationToken);
            hydrated = CharacterLoadSnapshotHydrator.Hydrate(snapshot);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CharacterSnapshotUnavailableException error)
        {
            Add(findings, "blocks", "load", CharacterSnapshotMetrics.ReasonCode(error.Reason),
                Describe(error));
        }
        catch (Exception error)
        {
            Add(findings, "blocks", "load", error.GetType().Name, Describe(error));
        }

        if (hydrated is not null)
        {
            Inspect(hydrated, _petContent, findings);
            result["characterId"] = hydrated.Character.Id;
            result["character"] = hydrated.Character.Name;
        }
        else if (findings.Count == 0)
        {
            result["note"] = "no character";
        }

        var blocks = false;
        foreach (var finding in findings)
        {
            blocks |= (string?)finding!["severity"] == "blocks";
        }
        result["canEnter"] = !blocks;
        result["findings"] = findings;
        return result;
    }

    /// <summary>The packet-building half of the check; also used by the unit checks with a hand-made character.</summary>
    internal static void Inspect(HydratedCharacterLoadSnapshot hydrated, IPetContentCatalog? petContent, JsonArray findings)
    {
        var character = hydrated.Character;
        foreach (var item in PacketBuilder.UnsendableItems(character))
        {
            var finding = Add(findings, "hidden", "item", "unsendable_item", item.Reason);
            finding["container"] = item.Container;
            finding["slot"] = item.Slot;
            finding["itemId"] = item.ItemId;
        }

        Try(findings, "enter_main", () => PacketBuilder.EnterMain(character));
        Try(findings, "bag", () => PacketBuilder.KitBagDetailPages(character));
        Try(findings, "bag_index", () => PacketBuilder.KitBagSlotIndexes(character));
        Try(findings, "equipment", () => PacketBuilder.EnterEquipmentSummary(character));
        Try(findings, "player_detail", () => PacketBuilder.PlayerDetail(character));
        Try(findings, "titles", () => PacketBuilder.MedusaDesignationInfo(character.SelectedTitleId, character.OwnedTitleIds));
        if (petContent is not null)
        {
            try
            {
                _ = PacketBuilder.OwnedPetListForEntry(
                    petContent, hydrated.Pets, hydrated.PetShed.OpenedCellCount, out var hidden);
                foreach (var note in hidden)
                {
                    Add(findings, "hidden", "pet", "unsendable_pet", note);
                }
            }
            catch (Exception error)
            {
                Add(findings, "blocks", "pets", error.GetType().Name, Describe(error));
            }
        }
    }

    private static void Try(JsonArray findings, string stage, Func<object?> build)
    {
        try
        {
            _ = build();
        }
        catch (Exception error)
        {
            Add(findings, "blocks", stage, error.GetType().Name, Describe(error));
        }
    }

    private static JsonObject Add(JsonArray findings, string severity, string stage, string code, string message)
    {
        var finding = new JsonObject
        {
            ["severity"] = severity,
            ["stage"] = stage,
            ["code"] = code,
            ["message"] = message
        };
        findings.Add(finding);
        return finding;
    }

    private static string Describe(Exception error)
    {
        var text = error.Message;
        for (var inner = error.InnerException; inner is not null && text.Length < 400; inner = inner.InnerException)
        {
            text += " <- " + inner.GetType().Name + ": " + inner.Message;
        }
        return Operations.Health.SessionJourney.Clip(text.Length > 600 ? text[..600] : text) ?? error.GetType().Name;
    }
}
