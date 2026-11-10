using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Globalization;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.EventsNpc;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

// PROF slice 1 (2026-11-09, the admin F-59 "professions are next"): professions through the client's own windows.
//  - Profession Mentor (function 64): the learn buttons take one Profession Skillbook (4268) and teach the
//    profession with its starter blueprint; the unlearn confirmation forgets it (AP is kept).
//  - Recipe items (ItemType 53, Values = blueprint id) teach their blueprint on right-click.
//  - The Manufacture window (LiveSkillUI) sends 10340 after each cast bar; every request is answered with 10341
//    or a 10340 refusal, then AP (10339) and the profession attributes (10338) are re-sent.
//  - Login sends the profession, its blueprints and the AP; offline AP is added then, online AP on the 15 s ticker
//    (GameSessionRegistry.ProfessionAp.cs).
// Every write runs in one EventsNpc transaction with the character row locked. Hot switch profession.crafting
// (Finalise2Rates, default 1); at 0 the mentor gives its old answers and nothing here runs.
// Protocol and evidence: Development/professions/README.md. No pop-up follows a used item (the admin's rule).
internal sealed partial class GameClientHandler
{
    private const int ProfessionMentorFunction = 64; // NPC_FLAG_SYS_LIFESKILL, ScriptedNpcDialogueCatalog
    private const int MentorUnlearnConfirm = 207;
    private const int MentorAlreadyLearned = 401;
    private const int MentorNoSkillbook = 402;
    private const int MentorLearned = 403;
    private const int MentorNothingToUnlearn = 309;
    private const int MentorUnlearned = 310;
    private const int CraftErrorTerminated = 0x505; // ERROR_0505 "Manufacturing terminated!"

    private static bool ProfessionsOn => Finalise2Rates.On(Finalise2RateKeys.Professions);

    private static FrozenDictionary<uint, ProfessionBlueprint>? _blueprintByRecipeItem;

    private static FrozenDictionary<uint, ProfessionBlueprint> BlueprintByRecipeItem =>
        _blueprintByRecipeItem ??= ProfessionBlueprintCatalog.All
            .Where(static blueprint => blueprint.RecipeItemId != 0)
            .GroupBy(static blueprint => blueprint.RecipeItemId)
            .ToFrozenDictionary(static group => group.Key, static group => group.First());

    // ---- shared reads (inside the transaction; @c = the character) -------------------------------------------

    private static async Task<ProfessionProgress?> ReadProfessionAsync(EventsNpcTransaction tx)
    {
        var row = await tx.ScalarAsync(
            "SELECT profession || ':' || level || ':' || experience || ':' || ap FROM public.profession_state " +
            "WHERE character_id = @c FOR UPDATE;") as string;
        if (row is null)
        {
            return null;
        }

        var parts = row.Split(':');
        return new ProfessionProgress(
            byte.Parse(parts[0], CultureInfo.InvariantCulture),
            int.Parse(parts[1], CultureInfo.InvariantCulture),
            int.Parse(parts[2], CultureInfo.InvariantCulture),
            int.Parse(parts[3], CultureInfo.InvariantCulture));
    }

    private int ProfessionMaxAp => ProfessionTables.ApFor(_character?.Level ?? 1).MaxAp;

    // ---- login ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Sends the profession, its blueprints and the AP once the client's UI is up (EnterUiReady 10357), once per
    /// character per connection; offline AP is credited first. Rig 2026-11-09: sent during world entry (before
    /// ClientReady) the 10339 crashed GodsWar.exe in its _MSG_SYN_AP handler (eip 0x6d6854). Every EnterUiReady,
    /// including the one after each map change, re-allows the online AP ticker for this session.
    /// A fault never stops the session.
    /// </summary>
    private int _professionSyncedCharacterId;

    private async Task SendProfessionStateAfterUiReadyAsync(CancellationToken cancellationToken)
    {
        if (!_enterUiReadyReceived || IsMapTransitionPending || _character is not { } character)
        {
            return;
        }

        if (ProfessionsOn && _professionSyncedCharacterId != character.Id)
        {
            _professionSyncedCharacterId = character.Id;
            await SendProfessionSyncAsync(character, cancellationToken);
        }

        _registry.MarkProfessionUiReady(_session);
    }

    private async Task SendProfessionSyncAsync(GameCharacter character, CancellationToken cancellationToken)
    {
        if (EventsNpcRuntime.Store is not { } store)
        {
            return;
        }

        try
        {
            var row = await store.ExecuteScalarAsync(
                "SELECT profession || ':' || level || ':' || experience || ':' || ap || ':' || " +
                "(extract(epoch FROM ap_updated_at) * 1000)::bigint FROM public.profession_state WHERE character_id = @c;",
                cancellationToken, ("c", character.Id)) as string;
            // Clear whatever an earlier character of this client left in the window (unknown ids are ignored).
            for (byte profession = 1; profession <= 4; profession++)
            {
                await _session.SendAsync(PacketBuilder.UnlearnLifeSkill(profession), cancellationToken, "ProfessionReset");
            }

            if (row is null)
            {
                return;
            }

            var parts = row.Split(':');
            var progress = new ProfessionProgress(byte.Parse(parts[0], CultureInfo.InvariantCulture),
                int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture),
                int.Parse(parts[3], CultureInfo.InvariantCulture));
            var anchor = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(parts[4], CultureInfo.InvariantCulture));
            var (_, _, periods) = ProfessionTables.Accrue(progress.Ap, anchor, DateTimeOffset.UtcNow,
                character.Level, online: false);
            var ap = progress.Ap;
            if (periods > 0)
            {
                var (maxAp, _, offlineAdd) = ProfessionTables.ApFor(character.Level);
                // The anchor moves by whole periods only; the guard keeps a second login from crediting them twice.
                var updated = await store.ExecuteScalarAsync("""
                    UPDATE public.profession_state
                    SET ap = GREATEST(ap, LEAST(@max, ap + @gain)),
                        ap_updated_at = ap_updated_at + make_interval(secs => @span), updated_at = now()
                    WHERE character_id = @c AND ap_updated_at + make_interval(secs => @span) <= now()
                    RETURNING ap;
                    """, cancellationToken, ("c", character.Id), ("max", maxAp),
                    ("gain", (int)Math.Min(int.MaxValue, (long)periods * offlineAdd)),
                    ("span", (double)periods * ProfessionTables.ApTimeSeconds));
                ap = updated is int fresh ? fresh : ap;
            }

            if (progress.Profession != 0)
            {
                await _session.SendAsync(PacketBuilder.LearnLifeSkill(progress.Profession, progress.Level, progress.Experience),
                    cancellationToken, "ProfessionLearned");
                var blueprints = await store.ExecuteScalarAsync(
                    "SELECT string_agg(blueprint_id || ':' || proficiency || ':' || " +
                    "GREATEST(0, COALESCE(ceil(extract(epoch FROM (cooldown_until - now())))::bigint, 0)), ',' " +
                    "ORDER BY blueprint_id) FROM public.profession_blueprints WHERE character_id = @c;",
                    cancellationToken, ("c", character.Id)) as string;
                foreach (var entry in (blueprints ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var bits = entry.Split(':');
                    var blueprintId = int.Parse(bits[0], CultureInfo.InvariantCulture);
                    if (ProfessionBlueprintCatalog.TryGet(blueprintId, out var blueprint) &&
                        blueprint.Profession == progress.Profession)
                    {
                        // PROF slice 2: +16 = the seconds left of a Type 1 cooldown (0 = ready).
                        await _session.SendAsync(PacketBuilder.LearnBlueprint(progress.Profession, blueprintId,
                            int.Parse(bits[1], CultureInfo.InvariantCulture),
                            (int)Math.Min(int.MaxValue, long.Parse(bits[2], CultureInfo.InvariantCulture)), fresh: false),
                            cancellationToken, "ProfessionBlueprint");
                    }
                }
            }

            await _session.SendAsync(PacketBuilder.SyncActivityPoints(ap, ProfessionMaxAp), cancellationToken,
                "ProfessionAp");
            Console.WriteLine($"[profession] login character={character.Name} profession={progress.Profession} " +
                $"level={progress.Level} exp={progress.Experience} ap={ap} offline-periods={periods}");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[profession] login sync skipped character={character.Name}: " +
                $"{error.GetType().Name}: {error.Message}");
        }
    }

    // ---- Profession Mentor ------------------------------------------------------------------------------------

    /// <summary>The mentor's learn buttons (302/304/306/308) and unlearn confirmation (207); false = old answers.</summary>
    private async Task<bool> TryHandleProfessionMentorAsync(NpcSpawnDefinition npc, int[] path,
        CancellationToken cancellationToken)
    {
        if (!ProfessionsOn || _character is null || path.Length == 0)
        {
            return false;
        }

        var clicked = path[^1];
        var profession = ProfessionTables.ProfessionForMentorButton(clicked);
        if (profession == 0 && clicked != MentorUnlearnConfirm)
        {
            return false;
        }

        try
        {
            if (profession != 0)
            {
                await LearnProfessionAsync(npc, profession, cancellationToken);
            }
            else
            {
                await UnlearnProfessionAsync(npc, cancellationToken);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[profession] mentor fault character={_character.Name} click={clicked}: " +
                $"{error.GetType().Name}: {error.Message}");
        }

        return true;
    }

    // GENERATED: no character-level gate. The skillbook is the gate (NF_L0_LS201: it comes from a level 70 quest);
    // Lifing_Skill's PlayLv 50 for profession level 1 is not enforced here.
    // 10336 +20 is sent as 0 everywhere: its effect in the window refresh is unread, and an item was just used.
    private async Task LearnProfessionAsync(NpcSpawnDefinition npc, byte profession, CancellationToken cancellationToken)
    {
        var maxAp = ProfessionMaxAp;
        var starter = ProfessionTables.StarterBlueprint(profession);
        var (result, ap) = await RunEventsNpcAsync(async tx =>
        {
            var current = await ReadProfessionAsync(tx);
            if (current is { Profession: not 0 })
            {
                return (MentorAlreadyLearned, 0);
            }

            if (!await tx.ConsumeAsync(ProfessionTables.ProfessionSkillbookItem, 1))
            {
                return (MentorNoSkillbook, 0);
            }

            // GENERATED: a first profession starts with a full AP bar; an unlearned one keeps the AP it had.
            var startAp = current?.Ap ?? maxAp;
            await tx.ScalarAsync("""
                INSERT INTO public.profession_state (character_id, profession, level, experience, ap, ap_updated_at)
                VALUES (@c, @p, 1, 0, @ap, now())
                ON CONFLICT (character_id) DO UPDATE
                SET profession = @p, level = 1, experience = 0, updated_at = now();
                DELETE FROM public.profession_blueprints WHERE character_id = @c;
                INSERT INTO public.profession_blueprints (character_id, blueprint_id) VALUES (@c, @b);
                """, ("p", (short)profession), ("ap", startAp), ("b", starter));
            return (MentorLearned, startAp);
        }, (MentorNoSkillbook, 0), cancellationToken);

        Console.WriteLine($"[profession] learn character={_character!.Name} profession={profession} result={result}");
        if (result == MentorLearned)
        {
            await RefreshAfterEventsNpcAsync([], cancellationToken);
            await _session.SendAsync(PacketBuilder.LearnLifeSkill(profession, 1, 0), cancellationToken, "ProfessionLearned");
            await _session.SendAsync(PacketBuilder.LearnBlueprint(profession, starter, 0, 0, fresh: false),
                cancellationToken, "ProfessionBlueprint");
            await _session.SendAsync(PacketBuilder.SyncActivityPoints(ap, maxAp), cancellationToken, "ProfessionAp");
        }

        await SendEventsNpcReplyAsync(npc, ProfessionMentorFunction, [result], "ProfessionMentor", cancellationToken);
    }

    private async Task UnlearnProfessionAsync(NpcSpawnDefinition npc, CancellationToken cancellationToken)
    {
        var (result, old) = await RunEventsNpcAsync(async tx =>
        {
            var current = await ReadProfessionAsync(tx);
            if (current is not { Profession: not 0 } progress)
            {
                return (MentorNothingToUnlearn, (byte)0);
            }

            // NF_L0_LS206: "The level and recipes of the Profession you unlearn will also be lost".
            await tx.ScalarAsync("""
                UPDATE public.profession_state SET profession = 0, level = 1, experience = 0, updated_at = now()
                WHERE character_id = @c;
                DELETE FROM public.profession_blueprints WHERE character_id = @c;
                """);
            return (MentorUnlearned, progress.Profession);
        }, (MentorNothingToUnlearn, (byte)0), cancellationToken);

        Console.WriteLine($"[profession] unlearn character={_character!.Name} profession={old} result={result}");
        if (result == MentorUnlearned)
        {
            await _session.SendAsync(PacketBuilder.UnlearnLifeSkill(old), cancellationToken, "ProfessionUnlearned");
        }

        await SendEventsNpcReplyAsync(npc, ProfessionMentorFunction, [result], "ProfessionMentor", cancellationToken);
    }

    // ---- recipe items -----------------------------------------------------------------------------------------

    /// <summary>A right-clicked recipe item (ItemType 53) teaches its blueprint. False = not a recipe item.</summary>
    private async Task<bool> TryUseProfessionRecipeAsync(int itemId, CancellationToken cancellationToken)
    {
        if (!ProfessionsOn || _character is null || itemId <= 0 ||
            !BlueprintByRecipeItem.TryGetValue((uint)itemId, out var blueprint))
        {
            return false;
        }

        // PROF slice 2: Type 1 ("Perpetual (periodic)") blueprints are taught like the others; their cooldown runs at
        // make time. Only Types 0 and 1 exist in Lifing_Blueprint.xml.
        if (blueprint.Type > 1)
        {
            return false;
        }

        var result = await RunEventsNpcAsync(async tx =>
        {
            var current = await ReadProfessionAsync(tx);
            if (current is not { Profession: not 0 } progress || progress.Profession != blueprint.Profession ||
                progress.Level < blueprint.RecipeLevel)
            {
                return PacketBuilder.CraftErrorLevelOrClass;
            }

            if (await tx.ScalarAsync(
                    "SELECT 1 FROM public.profession_blueprints WHERE character_id = @c AND blueprint_id = @b;",
                    ("b", blueprint.Id)) is not null)
            {
                return PacketBuilder.CraftErrorKnown;
            }

            if (!await tx.ConsumeAsync(itemId, 1))
            {
                tx.Rollback();
                return -1;
            }

            await tx.ScalarAsync(
                "INSERT INTO public.profession_blueprints (character_id, blueprint_id) VALUES (@c, @b);",
                ("b", blueprint.Id));
            return 0;
        }, -1, cancellationToken);

        Console.WriteLine($"[profession] recipe character={_character.Name} item={itemId} blueprint={blueprint.Id} " +
            $"result=0x{result:x}");
        if (result == 0)
        {
            await RefreshAfterEventsNpcAsync([], cancellationToken);
            await _session.SendAsync(PacketBuilder.LearnBlueprint(blueprint.Profession, blueprint.Id, 0, 0, fresh: false),
                cancellationToken, "ProfessionBlueprint");
            return true;
        }

        await SendKitBagRefreshAsync(cancellationToken);
        if (result > 0)
        {
            // The client's own ERROR_0509 / ERROR_050A line, through the 10340 refusal frame.
            await _session.SendAsync(PacketBuilder.MakeBlueprintError(blueprint.Profession, blueprint.Id, result),
                cancellationToken, "ProfessionRecipeRefused");
        }

        return true;
    }

    // ---- manufacture ------------------------------------------------------------------------------------------

    private async Task<bool> TryHandleProfessionPacketAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (packet.Opcode != PacketBuilder.MakeBlueprintOpcode)
        {
            return false;
        }

        if (packet.Buffer.Length < PacketBuilder.MakeBlueprintRequestLength || _character is null)
        {
            return true;
        }

        var first = BinaryPrimitives.ReadInt32LittleEndian(packet.Buffer.AsSpan(4, 4));
        var second = BinaryPrimitives.ReadInt32LittleEndian(packet.Buffer.AsSpan(8, 4));
        var third = BinaryPrimitives.ReadInt32LittleEndian(packet.Buffer.AsSpan(12, 4));
        Console.WriteLine($"[profession] make request character={_character.Name} words={first},{second},{third}");

        // 0x6d2630 builds the request as {blueprint SkillID, blueprint ID, 0}. The swapped order is accepted too
        // until a rig capture confirms it.
        if (!(ProfessionBlueprintCatalog.TryGet(second, out var blueprint) && blueprint.Profession == first) &&
            !(ProfessionBlueprintCatalog.TryGet(first, out blueprint) && blueprint.Profession == second))
        {
            await _session.SendAsync(PacketBuilder.MakeBlueprintError((byte)Math.Clamp(first, 0, 255), second,
                CraftErrorTerminated), cancellationToken, "ProfessionMakeRefused");
            return true;
        }

        try
        {
            await MakeBlueprintAsync(blueprint, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[profession] make fault character={_character.Name} blueprint={blueprint.Id}: " +
                $"{error.GetType().Name}: {error.Message}");
            await _session.SendAsync(PacketBuilder.MakeBlueprintError(blueprint.Profession, blueprint.Id,
                CraftErrorTerminated), cancellationToken, "ProfessionMakeRefused");
        }

        return true;
    }

    private sealed record CraftResult(int Error, ProfessionCraftOutcome Outcome, int Ap, int Proficiency,
        int CooldownSeconds = 0);

    private async Task MakeBlueprintAsync(ProfessionBlueprint blueprint, CancellationToken cancellationToken)
    {
        var character = _character!;
        var error = !ProfessionsOn ? CraftErrorTerminated
            : blueprint.Type > 1 ? CraftErrorTerminated
            : !ProfessionTables.InCraftingArea(character.CurrentMap, character.PositionX, character.PositionZ)
                ? PacketBuilder.CraftErrorLocation
                : 0;
        CraftResult? result = null;
        if (error == 0)
        {
            var roll = Random.Shared.Next(100);
            var level = character.Level;
            result = await RunEventsNpcAsync(async tx =>
            {
                var current = await ReadProfessionAsync(tx);
                if (current is not { Profession: not 0 } progress || progress.Profession != blueprint.Profession ||
                    progress.Level < blueprint.RecipeLevel)
                {
                    return new CraftResult(PacketBuilder.CraftErrorLevelOrClass, default, 0, 0);
                }

                if (await tx.ScalarAsync(
                        "SELECT proficiency || ':' || COALESCE(ceil(extract(epoch FROM (cooldown_until - now())))::bigint, 0) " +
                        "FROM public.profession_blueprints WHERE character_id = @c AND blueprint_id = @b FOR UPDATE;",
                        ("b", blueprint.Id)) is not string known)
                {
                    return new CraftResult(PacketBuilder.CraftErrorLevelOrClass, default, 0, 0);
                }

                var proficiency = int.Parse(known.Split(':')[0], CultureInfo.InvariantCulture);
                // PROF slice 2: a Type 1 blueprint is refused while its cooldown runs (ERROR_0503, the client's own
                // check 0x6d77f0 refuses first while the stored cooldown word is not 0).
                if (long.Parse(known.Split(':')[1], CultureInfo.InvariantCulture) > 0)
                {
                    return new CraftResult(PacketBuilder.CraftErrorCooldown, default, progress.Ap, proficiency);
                }

                if (progress.Ap < blueprint.ActivityPoints)
                {
                    return new CraftResult(PacketBuilder.CraftErrorActivityPoints, default, progress.Ap, proficiency);
                }

                foreach (var material in blueprint.Materials)
                {
                    if (await tx.CountAsync((int)material.ItemId) < material.Count)
                    {
                        return new CraftResult(PacketBuilder.CraftErrorMaterials, default, progress.Ap, proficiency);
                    }
                }

                var success = roll < ProfessionTables.SuccessPercent(blueprint, proficiency);
                if (success)
                {
                    foreach (var material in blueprint.Materials)
                    {
                        if (!await tx.ConsumeAsync((int)material.ItemId, material.Count))
                        {
                            tx.Rollback();
                            return new CraftResult(PacketBuilder.CraftErrorMaterials, default, progress.Ap, proficiency);
                        }
                    }

                    // Blueprint Binding is 0 in every row: the product is unbound. Stacks never go above 99.
                    if (!await tx.GrantAsync((int)blueprint.ProductId, blueprint.ProductCount, bound: false, stackCap: 99))
                    {
                        tx.Rollback();
                        return new CraftResult(PacketBuilder.CraftErrorBagFull, default, progress.Ap, proficiency);
                    }
                }

                // GENERATED: a failed roll still spends the AP but keeps the materials (retail behaviour unknown).
                var outcome = ProfessionTables.ApplyCraft(blueprint, progress.Level, progress.Experience, proficiency,
                    level, success);
                var ap = progress.Ap - blueprint.ActivityPoints;
                // PROF slice 2: a successful Type 1 make starts the blueprint's Time cooldown (GENERATED: a failed
                // roll does not; IGG: "a fixed period before the next time that an item can be produced").
                var cooldown = ProfessionTables.CooldownAfterMake(blueprint, success);
                await tx.ScalarAsync("""
                    UPDATE public.profession_state SET ap = @ap, level = @l, experience = @e, updated_at = now()
                    WHERE character_id = @c;
                    UPDATE public.profession_blueprints SET proficiency = @pr,
                        cooldown_until = CASE WHEN @cd > 0 THEN now() + make_interval(secs => @cd) ELSE cooldown_until END
                    WHERE character_id = @c AND blueprint_id = @b;
                    """, ("ap", ap), ("l", (short)outcome.NewLevel), ("e", outcome.NewExperience),
                    ("pr", outcome.NewProficiency), ("b", blueprint.Id), ("cd", (double)cooldown));
                return new CraftResult(0, outcome, ap, proficiency, cooldown);
            }, new CraftResult(CraftErrorTerminated, default, 0, 0), cancellationToken);
            error = result.Error;
            Console.WriteLine($"[profession] make character={character.Name} blueprint={blueprint.Id} roll={roll} " +
                $"error=0x{error:x} success={result.Outcome.Success} exp+={result.Outcome.SkillExperienceGained} " +
                $"level={result.Outcome.NewLevel} ap={result.Ap}");
        }

        if (error != 0 || result is null)
        {
            await _session.SendAsync(PacketBuilder.MakeBlueprintError(blueprint.Profession, blueprint.Id, error),
                cancellationToken, "ProfessionMakeRefused");
            return;
        }

        var made = result.Outcome;
        if (made.Success)
        {
            await RefreshAfterEventsNpcAsync([], cancellationToken);
        }

        // AP and the attributes go first: the 10341 handler runs the client's continue check (0x6d77f0) at once,
        // and that check reads the stored AP before casting the next item of the batch.
        await _session.SendAsync(PacketBuilder.SyncActivityPoints(result.Ap, ProfessionMaxAp), cancellationToken,
            "ProfessionAp");
        if (made.LevelledUp)
        {
            await _session.SendAsync(PacketBuilder.LifeSkillAttribute(blueprint.Profession, 0,
                PacketBuilder.LifeSkillLevelAttribute, made.NewLevel), cancellationToken, "ProfessionLevel");
        }

        if (made.Success)
        {
            await _session.SendAsync(PacketBuilder.LifeSkillAttribute(blueprint.Profession, 0,
                PacketBuilder.LifeSkillExperienceAttribute, made.NewExperience), cancellationToken, "ProfessionExp");
            if (made.NewProficiency != result.Proficiency)
            {
                await _session.SendAsync(PacketBuilder.LifeSkillAttribute(blueprint.Profession, blueprint.Id,
                    PacketBuilder.BlueprintProficiencyAttribute, made.NewProficiency), cancellationToken,
                    "ProfessionProficiency");
            }
        }

        // PROF slice 2: the cooldown word before 10341, so the client's continue check stops a Type 1 batch at one.
        if (result.CooldownSeconds > 0)
        {
            await _session.SendAsync(PacketBuilder.LifeSkillAttribute(blueprint.Profession, blueprint.Id,
                PacketBuilder.BlueprintCooldownAttribute, result.CooldownSeconds), cancellationToken, "ProfessionCooldown");
        }

        await _session.SendAsync(PacketBuilder.MakeBlueprintReturn(blueprint.Profession, blueprint.Id,
            made.Success ? 1 : 0, made.SkillExperienceGained, blueprint.ProductId, blueprint.ProductCount, bound: false),
            cancellationToken, "ProfessionMakeReturn");
    }
}
