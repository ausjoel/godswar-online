using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.Operators;

namespace Godswar.Server.Game;

// ROUND2: the loopback /admin endpoints of the management port (9090 on live).
// Every edit of an ONLINE character runs on that character's own handler
// (OperatorAdminEditAction, under its character-state gate): the database
// write advances the matching revision column, then the handler reloads its
// character from the database and pushes the bag, player detail, status
// frame (wallet) and zodiac state to the client. An OFFLINE character gets
// 409 "offline", so the editor writes the database as it does today.
// Every call is logged as "[admin] ..." and as an operator_log row.

/// <summary>A database edit run on the target's own handler, then reloaded.</summary>
internal sealed record OperatorAdminEditAction(
    string ActorName,
    Func<int, CancellationToken, Task<OperatorItemResult>> Edit,
    bool AdvanceWorld,
    bool ZodiacChanged,
    bool QuestsChanged = false) : OperatorSessionAction(ActorName); // ROUND-QUESTRESET: QuestsChanged

internal sealed partial class GameSessionRegistry
{
    // ROUND3: statusId selection is additive; exactly one identity is accepted.
    public const string OperatorAdminCapabilitiesJson =
        """
        {"version":1,"offline":"409 {\"error\":\"offline\"}","busy":"409 {\"error\":\"busy\"} (logging in or changing maps; retry, never write the database)","endpoints":[
        {"method":"GET","path":"/admin/capabilities"},
        {"method":"GET","path":"/admin/buffs/catalog"},
        {"method":"POST","path":"/admin/buff/inspect","body":{"actor":"string","character":"string"}},
        {"method":"POST","path":"/admin/buff/remove","body":{"actor":"string","character":"string","epoch":"opaque status lifetime","kind":"int","statusId":"int","revision":"exact decimal string"}},
        {"method":"POST","path":"/admin/buff/apply","body":{"actor":"string","character":"string","skillId":"class-skill buff ID (exclusive with statusId)","statusId":"any row of GET /admin/buffs/catalog buffs (exclusive with skillId)","durationSeconds":"uint32 seconds (optional; defaults to authored duration)"}},
        {"method":"POST","path":"/admin/item/add","body":{"actor":"string","character":"string","itemId":"int","quantity":"int 1-9999 (default 1)","quality":"int 0-30 (default 1)","grade":"int 0-30 (default 1)","bound":"0|1 (default 0)"}},
        {"method":"POST","path":"/admin/item/remove","body":{"actor":"string","character":"string","slot":"int 0-95"}},
        {"method":"POST","path":"/admin/item/move","body":{"actor":"string","character":"string","fromSlot":"int 0-95","toSlot":"int 0-95 (must be empty)"}},
        {"method":"POST","path":"/admin/progression","body":{"actor":"string","character":"string","level":"int 1-200 (optional; resets EXP unless experience is given)","experience":"int (optional)","talentPoints":"int (optional)","zodiacEnergy":"int (optional)"}},
        {"method":"POST","path":"/admin/money","body":{"actor":"string","character":"string","silver":"int (optional)","gold":"int (optional)","bindGold":"int (optional)"}},
        {"method":"POST","path":"/admin/teleport","body":{"actor":"string","character":"string","map":"int 0-255","x":"number","z":"number"}},
        {"method":"POST","path":"/admin/quest/chain","body":{"actor":"string","character":"string","operation":"complete_main | reset","level":"int 1-200 (complete_main)","scope":"main | all (reset)","expected":"int[] the quest ids the preview showed; any other plan is refused"}},
        {"method":"POST","path":"/admin/kick","body":{"actor":"string","character":"string","reason":"string (optional)"}},
        {"method":"POST","path":"/admin/board/post","body":{"actor":"string","text":"string 1-106"}}
        ]}
        """;

    private static string BuildOperatorCapabilitiesJson()
    {
        var doc = JsonNode.Parse(OperatorAdminCapabilitiesJson)!.AsObject();
        doc["gmAuthority"] = OperatorControls.UsesCharacterGmFlags ? "character_flag" : "account_allowlist";
        // Editors must require this capability as well as the additive schema:
        // an older running binary must never be given grant data it cannot load.
        doc["titleGrants"] = new JsonObject { ["version"] = 1,
            ["ownedLimit"] = Godswar.Server.Application.Characters.CharacterSnapshotLimits.OwnedTitleCount };
        return doc.ToJsonString();
    }

    internal async Task<OperatorAdminResponse> HandleOperatorAdminAsync(
        OperatorAdminRequest request,
        IItemTemplateCatalog templates,
        CancellationToken cancellationToken)
    {
        if (request.Method == "GET")
        {
            return request.Path switch
            {
                "/admin/capabilities" => new(200, BuildOperatorCapabilitiesJson()),
                "/admin/buffs/catalog" => new(200, BuildOperatorBuffCatalogueJson()),
                _ => Error(404, "unknown admin route")
            };
        }

        JsonObject body;
        try
        {
            body = JsonNode.Parse(request.Body) as JsonObject ??
                throw new JsonException("the body must be a JSON object");
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            return Error(400, "invalid JSON: " + error.Message);
        }

        var actor = Text(body, "actor");
        // the admin 2026-11-08: instant post from the editor to the board above the chat box (older clients: info line).
        if (request.Path == "/admin/board/post")
        {
            var message = Text(body, "text");
            if (string.IsNullOrWhiteSpace(actor) || actor.Length > 64 || string.IsNullOrWhiteSpace(message) || message.Length > 106)
                return Error(400, "actor (1-64 characters) and text (1-106 characters) are required");
            var delivered = await PublishBoardMessageAsync(message, cancellationToken);
            if (OperatorControls.Store is { } boardStore)
            {
                try { await boardStore.LogAsync(0, "web:" + actor, "board/post", "all", $"delivered={delivered} text='{message}'", cancellationToken); }
                catch (Exception error) when (error is not OperationCanceledException) { Console.WriteLine($"[admin] log row failed: {error.Message}"); }
            }
            return new(200, $"{{\"ok\":true,\"delivered\":{delivered}}}");
        }
        var name = Text(body, "character");
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 64 ||
            string.IsNullOrWhiteSpace(name) || name.Length > 32)
        {
            return Error(400, "actor (1-64 characters) and character (a name) are required");
        }

        var response = await HandleOperatorAdminCoreAsync(
            request.Path,
            body,
            actor,
            name,
            templates,
            cancellationToken);
        Console.WriteLine(
            $"[admin] {request.Path} actor={actor} character={name} " +
            $"status={response.StatusCode} result={response.Json}");
        if (OperatorControls.Store is { } store)
        {
            try
            {
                await store.LogAsync(
                    0,
                    "web:" + actor,
                    request.Path.TrimStart('/'),
                    name,
                    $"status={response.StatusCode} body={body.ToJsonString()} result={response.Json}",
                    cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Console.WriteLine($"[admin] log row failed: {error.Message}");
            }
        }

        return response;
    }

    private async Task<OperatorAdminResponse> HandleOperatorAdminCoreAsync(
        string path,
        JsonObject body,
        string actor,
        string name,
        IItemTemplateCatalog templates,
        CancellationToken cancellationToken)
    {
        var online = FindOnlineCharacter(name);
        if (online is null && IsCharacterConnected(name))
        {
            // Logging in or changing maps: online, but not ready for an edit.
            // Never "offline", or the editor would write behind the session.
            return Error(409, "busy");
        }

        if (online is null)
        {
            var store = OperatorControls.Store;
            if (store is null)
            {
                return Error(503, "operator store unavailable");
            }

            return await store.FindCharacterAsync(name, cancellationToken) is null
                ? Error(404, "no such character")
                : Error(409, "offline");
        }

        var operatorStore = OperatorControls.Store;
        if (operatorStore is null)
        {
            return Error(503, "operator store unavailable");
        }

        OperatorSessionAction action;
        switch (path)
        {
            case "/admin/buff/inspect":
                action = new OperatorInspectBuffAction("web:" + actor);
                break;
            case "/admin/buff/remove":
                if (!TryReadEditorBuffToken(body, out var token))
                    return Error(400, "epoch, kind, statusId and exact revision string are required");
                action = new OperatorRemoveBuffAction("web:" + actor, token);
                break;
            case "/admin/buff/apply":
            {
                if (!TryReadOperatorBuff(body, out var definition, out var error))
                {
                    return Error(400, error);
                }
                action = new OperatorApplyBuffAction("web:" + actor, definition);
                break;
            }
            case "/admin/kick":
            {
                try
                {
                    await online.Session.SendAsync(
                        Packets.PacketBuilder.ServerNote("You were disconnected by the server staff."),
                        cancellationToken,
                        "AdminKickNotice");
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Console.WriteLine($"[admin] kick notice failed: {error.Message}");
                }

                online.Session.Disconnect();
                return Ok(online.CharacterName, "disconnected");
            }
            case "/admin/teleport":
            {
                if (Int(body, "map") is not { } map || map is < 0 or > 255 ||
                    Number(body, "x") is not { } x || Number(body, "z") is not { } z)
                {
                    return Error(400, "map (0-255), x and z are required");
                }

                action = new OperatorTeleportAction("web:" + actor, (byte)map, x, z);
                break;
            }
            case "/admin/item/add":
            {
                if (Int(body, "itemId") is not { } itemId || itemId <= 0 ||
                    !templates.TryGet((uint)itemId, out var template))
                {
                    return Error(400, "itemId must be an item template of this server");
                }

                var quantity = Int(body, "quantity") ?? 1;
                var quality = Int(body, "quality") ?? 1;
                var grade = Int(body, "grade") ?? 1;
                var bound = Int(body, "bound") ?? 0;
                if (quantity is < 1 or > 9999 || quality is < 0 or > 30 ||
                    grade is < 0 or > 30 || bound is < 0 or > 1)
                {
                    return Error(400, "quantity 1-9999, quality 0-30, grade 0-30, bound 0|1");
                }

                var stackCap = GameClientHandler.ReadItemStackCapForOperator(template.StatsJson);
                action = new OperatorAdminEditAction(
                    "web:" + actor,
                    (characterId, token) => operatorStore.GrantItemAsync(
                        characterId,
                        (uint)itemId,
                        quantity,
                        stackCap,
                        (short)bound,
                        token,
                        (short)quality,
                        (short)grade),
                    AdvanceWorld: false,
                    ZodiacChanged: false);
                break;
            }
            case "/admin/item/remove":
            {
                if (Int(body, "slot") is not { } slot || slot is < 0 or > 95)
                {
                    return Error(400, "slot 0-95 is required");
                }

                action = new OperatorAdminEditAction(
                    "web:" + actor,
                    (characterId, token) => operatorStore.RemoveBagItemAsync(characterId, (short)slot, token),
                    AdvanceWorld: false,
                    ZodiacChanged: false);
                break;
            }
            case "/admin/item/move":
            {
                if (Int(body, "fromSlot") is not { } from || Int(body, "toSlot") is not { } to ||
                    from is < 0 or > 95 || to is < 0 or > 95)
                {
                    return Error(400, "fromSlot and toSlot 0-95 are required");
                }

                action = new OperatorAdminEditAction(
                    "web:" + actor,
                    (characterId, token) => operatorStore.MoveBagItemAsync(
                        characterId, (short)from, (short)to, token),
                    AdvanceWorld: false,
                    ZodiacChanged: false);
                break;
            }
            case "/admin/progression":
            {
                var level = Int(body, "level");
                var experience = Long(body, "experience");
                var talentPoints = Int(body, "talentPoints");
                var zodiacEnergy = Int(body, "zodiacEnergy");
                if (level is null && experience is null && talentPoints is null && zodiacEnergy is null)
                {
                    return Error(400, "give at least one of level, experience, talentPoints, zodiacEnergy");
                }

                if (level is < 1 or > 200)
                {
                    return Error(400, "level must be 1-200");
                }

                var targetLevel = level ?? online.Character.Level;
                if (experience is { } exp &&
                    (exp < 0 || exp >= Math.Max(1, State.PlayerExperienceCatalog.GetNextLevelExperience(targetLevel))))
                {
                    return Error(400,
                        $"experience must be 0..{State.PlayerExperienceCatalog.GetNextLevelExperience(targetLevel) - 1} at level {targetLevel}");
                }

                action = new OperatorAdminEditAction(
                    "web:" + actor,
                    (characterId, token) => operatorStore.SetProgressionAsync(
                        characterId,
                        level is { } l ? (short)l : null,
                        experience,
                        talentPoints,
                        zodiacEnergy,
                        token),
                    AdvanceWorld: level is not null,
                    ZodiacChanged: zodiacEnergy is not null);
                break;
            }
            case "/admin/money":
            {
                var silver = Int(body, "silver");
                var gold = Int(body, "gold");
                var bindGold = Int(body, "bindGold");
                if (silver is null && gold is null && bindGold is null)
                {
                    return Error(400, "give at least one of silver, gold, bindGold");
                }

                action = new OperatorAdminEditAction(
                    "web:" + actor,
                    (characterId, token) => operatorStore.SetWalletAsync(
                        characterId, silver, gold, bindGold, token),
                    AdvanceWorld: false,
                    ZodiacChanged: false);
                break;
            }
            case "/admin/quest/chain":
            {
                // ROUND-QUESTRESET (issue #18): completes without rewards; refuses a changed plan or a carried quest.
                var operationText = Text(body, "operation");
                var operation = operationText switch
                {
                    "complete_main" => Godswar.Server.Domain.World.Content.QuestChainOperation.CompleteMainLine,
                    "reset" => Godswar.Server.Domain.World.Content.QuestChainOperation.ResetCompleted,
                    _ => (Godswar.Server.Domain.World.Content.QuestChainOperation)0
                };
                if (operation == 0)
                {
                    return Error(400, "operation must be complete_main or reset");
                }

                var questLevel = Int(body, "level") ?? 0;
                if (operation == Godswar.Server.Domain.World.Content.QuestChainOperation.CompleteMainLine &&
                    questLevel is < Godswar.Server.Domain.World.Content.QuestChainPlanner.MinimumLevel or
                        > Godswar.Server.Domain.World.Content.QuestChainPlanner.MaximumLevel)
                {
                    return Error(400, "level must be 1-200");
                }

                var scopeText = Text(body, "scope");
                var scope = scopeText switch
                {
                    "" or "main" => Godswar.Server.Domain.World.Content.QuestChainScope.MainLine,
                    "all" => Godswar.Server.Domain.World.Content.QuestChainScope.All,
                    _ => (Godswar.Server.Domain.World.Content.QuestChainScope)0
                };
                if (scope == 0)
                {
                    return Error(400, "scope must be main or all");
                }

                if (body["expected"] is not JsonArray expectedArray || expectedArray.Count > 4096)
                {
                    return Error(400, "expected (the previewed quest ids, at most 4096) is required");
                }

                var expected = new List<uint>(expectedArray.Count);
                foreach (var node in expectedArray)
                {
                    if (node is not JsonValue value || !value.TryGetValue<int>(out var questId) || questId <= 0)
                    {
                        return Error(400, "expected must hold positive quest ids");
                    }

                    expected.Add((uint)questId);
                }

                action = new OperatorAdminEditAction(
                    "web:" + actor,
                    (characterId, token) => operatorStore.ApplyQuestChainAsync(
                        characterId, operation, questLevel, scope, expected, token),
                    AdvanceWorld: false,
                    ZodiacChanged: false,
                    QuestsChanged: true);
                break;
            }
            default:
                return Error(404, "unknown admin route");
        }

        var result = await RunOperatorActionAsync(online.Session, action, cancellationToken);
        if (action is OperatorApplyBuffAction or OperatorInspectBuffAction or OperatorRemoveBuffAction &&
            result.StartsWith("{", StringComparison.Ordinal))
        {
            var payload = JsonNode.Parse(result)!.AsObject();
            return new(payload["ok"]?.GetValue<bool>() == false ? 409 : 200, result);
        }
        return result.StartsWith("error: ", StringComparison.Ordinal) ||
            result is "that player is not in the world" or "that player's session is busy; try again" or
                "not in the world" ||
            result.StartsWith("refused", StringComparison.Ordinal)
            ? Error(409, result.StartsWith("error: ", StringComparison.Ordinal) ? result[7..] : result)
            : Ok(online.CharacterName, result);
    }

    private static OperatorAdminResponse Ok(string character, string result) =>
        new(200, new JsonObject
        {
            ["ok"] = true,
            ["character"] = character,
            ["result"] = result
        }.ToJsonString());

    private static OperatorAdminResponse Error(int status, string error) =>
        new(status, new JsonObject { ["error"] = error }.ToJsonString());

    private static string Text(JsonObject body, string name) =>
        body[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : string.Empty;

    private static int? Int(JsonObject body, string name) =>
        body[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number :
        body[name] is JsonValue text && text.TryGetValue<string>(out var s) &&
            int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static long? Long(JsonObject body, string name) =>
        body[name] is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

    private static float? Number(JsonObject body, string name) =>
        body[name] is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number)
            ? (float)number
            : null;
}
