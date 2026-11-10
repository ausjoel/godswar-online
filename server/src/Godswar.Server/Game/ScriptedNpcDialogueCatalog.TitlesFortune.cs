using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Titles;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// TITLES: the Title Expert (functions 12 and 13) and the Fortune Teller (function 19) answer their
/// second and third pages when switched on (<see cref="TitleExpertPolicy.IsEnabled"/>,
/// <see cref="FortuneTellerPolicy.IsEnabled"/>); switched off, every click falls through to the old
/// menu-only table exactly as before.
/// </summary>
/// <remarks>
/// Both scripts reuse small numbers on every page (the Fortune Teller's <c>1</c> is "free change" on
/// page one, Aries on page two and "changed" on page three), so the click alone cannot say which page it
/// was made on. The handler keeps the page-one choice per session: a page-one click opens page two and
/// is remembered; the next click on the same NPC and function is read against that page; the opening
/// request (no click) and any other NPC forget it. The clicked number itself is the newest click the
/// request carries (<see cref="ResolveNpcFunctionSelection(ReadOnlySpan{byte})"/>), whichever slot the
/// client put it in.
/// </remarks>
internal sealed partial class GameClientHandler
{
    private (uint Npc, int Function, int PageOne)? _titleFortunePage;

    private async Task<bool> TryHandleTitleFortuneAsync(
        NpcSpawnDefinition npc,
        ScriptedNpcDialogue dialogue,
        int dialogIndex,
        int newestClick,
        CancellationToken cancellationToken)
    {
        var function = dialogue.FunctionNumber;
        var isTitle = function is TitleExpertPolicy.IdentificationFunction or TitleExpertPolicy.RewardFunction;
        var isFortune = function == FortuneTellerPolicy.Function;
        if ((!isTitle && !isFortune) || _character is null || _account is null)
        {
            return false;
        }

        if ((isTitle && !TitleExpertPolicy.IsEnabled()) || (isFortune && !FortuneTellerPolicy.IsEnabled()))
        {
            _titleFortunePage = null;
            return false;
        }

        if (newestClick < 0)
        {
            // The opening request: the old table sends page one.
            _titleFortunePage = null;
            return false;
        }

        var pageOne = _titleFortunePage is { } open && open.Npc == npc.InteractionId && open.Function == function
            ? open.PageOne
            : (int?)null;
        _titleFortunePage = null;

        if (function == TitleExpertPolicy.RewardFunction)
        {
            // NpcFunDesaward: the reward page pays items for owned titles; the guide lists no rewards,
            // so every type answers the script's own "This title type hasn't been released." line.
            if (pageOne is null && TitleExpertPolicy.RewardPageOneButtons.Contains(newestClick))
            {
                await SendScriptedNpcDialogueReplyAsync(
                    npc, dialogue, dialogIndex, newestClick,
                    [TitleExpertPolicy.RewardNotReleasedLine], cancellationToken);
                return true;
            }

            return false;
        }

        if (function == TitleExpertPolicy.IdentificationFunction)
        {
            await GrantMissingQuestTitlesAsync("Title Expert visit", cancellationToken);
            if (pageOne == TitleExpertPolicy.AttributeButton &&
                TitleExpertPolicy.AttributeButtons.Contains(newestClick))
            {
                var claim = TitleExpertPolicy.ResolveAttributeClaim(
                    newestClick, CurrentTitleStats(), _character.OwnedTitleIds);
                await FinishTitleClaimAsync(npc, dialogue, dialogIndex, newestClick, claim, "attribute", cancellationToken);
                return true;
            }

            if (newestClick == TitleExpertPolicy.LevelButton)
            {
                var claim = TitleExpertPolicy.ResolveLevelClaim(
                    _character.Profession, CurrentTitleStats(), _character.OwnedTitleIds);
                await FinishTitleClaimAsync(npc, dialogue, dialogIndex, newestClick, claim, "leveling", cancellationToken);
                return true;
            }

            if (newestClick == TitleExpertPolicy.AttributeButton)
            {
                _titleFortunePage = (npc.InteractionId, function, newestClick);
                await SendScriptedNpcDialogueReplyAsync(
                    npc, dialogue, dialogIndex, newestClick, TitleExpertPolicy.AttributeButtons, cancellationToken);
                return true;
            }

            return false;
        }

        // The Fortune Teller.
        if (pageOne is { } chosen &&
            FortuneTellerPolicy.ResolvePageTwo(chosen, newestClick) is { } request)
        {
            await ApplyFortuneAsync(npc, dialogue, dialogIndex, newestClick, request.Action, request.Sign, cancellationToken);
            return true;
        }

        if (FortuneTellerPolicy.IsPageOneButton(newestClick))
        {
            _titleFortunePage = (npc.InteractionId, function, newestClick);
            await SendScriptedNpcDialogueReplyAsync(
                npc, dialogue, dialogIndex, newestClick, FortuneTellerPolicy.PageTwo(newestClick), cancellationToken);
            return true;
        }

        return false;
    }

    private TitleExpertPolicy.TitleStats CurrentTitleStats()
    {
        // "Not including bonuses": the character's own stats, without altar or title bonuses.
        var stats = CharacterStats.FromCharacterBase(_character!);
        return new(
            _character!.Level, stats.MaxHp, stats.PhysicalAttack, stats.MagicAttack, stats.PhysicalDefense,
            stats.MagicDefense, stats.Hit, stats.Dodge, stats.DamageAbsorb);
    }

    private async Task FinishTitleClaimAsync(
        NpcSpawnDefinition npc,
        ScriptedNpcDialogue dialogue,
        int dialogIndex,
        int selection,
        TitleExpertPolicy.Claim claim,
        string kind,
        CancellationToken cancellationToken)
    {
        var reply = claim.Reply;
        if (claim.Grant.Count > 0)
        {
            var granted = await GrantTitlesAsync(claim.Grant, "title-expert", $"Title Expert {kind} title", cancellationToken);
            if (granted is null)
            {
                reply = [TitleExpertPolicy.ErrorLine];
            }
        }

        Console.WriteLine(
            $"[titles] title expert {kind} character={_character!.Name} profession={_character.Profession} " +
            $"level={_character.Level} click={selection} stats={CurrentTitleStats()} " +
            $"grant=[{string.Join(',', claim.Grant)}] " +
            $"reply=[{string.Join(',', reply)}]");
        await SendScriptedNpcDialogueReplyAsync(npc, dialogue, dialogIndex, selection, reply, cancellationToken);
    }

    /// <summary>Writes the grants and tells the client its new title list; null when the store failed.</summary>
    private async Task<IReadOnlyList<uint>?> GrantTitlesAsync(
        IReadOnlyList<uint> titleIds, string grantedBy, string note, CancellationToken cancellationToken)
    {
        if (TitleFortuneControls.Store is not { } store || _character is null)
        {
            return null;
        }

        IReadOnlyList<uint> granted;
        try
        {
            granted = await store.GrantTitlesAsync(_character.Id, titleIds, grantedBy, note, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[titles] grant failed character={_character.Name}: {error.Message}");
            return null;
        }

        // The rows that were already there (a second click) are owned too: refresh the list for all.
        foreach (var titleId in titleIds)
        {
            await _registry.PublishItemTitleAsync(_session, titleId, 0, cancellationToken);
        }

        Console.WriteLine(
            $"[titles] granted character={_character.Name} by={grantedBy} new=[{string.Join(',', granted)}]");
        return granted;
    }

    /// <summary>Quest titles (IGG "Quest Titles"): granted on hand-in and swept at the Title Expert.</summary>
    private async Task GrantMissingQuestTitlesAsync(string note, CancellationToken cancellationToken)
    {
        if (_character is null || !TitleExpertPolicy.IsEnabled())
        {
            return;
        }

        var missing = TitleExpertPolicy.MissingQuestTitles(_character.QuestCompletedIds, _character.OwnedTitleIds);
        if (missing.Count > 0)
        {
            await GrantTitlesAsync(missing, "quest-title", note, cancellationToken);
        }
    }

    /// <summary>The one-line hook at the end of a quest hand-in.</summary>
    private async Task TryGrantQuestTitleAsync(uint questId, CancellationToken cancellationToken)
    {
        if (!TitleExpertPolicy.QuestTitles.ContainsKey(questId))
        {
            return;
        }

        try
        {
            await GrantMissingQuestTitlesAsync($"quest {questId} handed in", cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[titles] quest title failed quest={questId}: {error.Message}");
        }
    }

    private async Task ApplyFortuneAsync(
        NpcSpawnDefinition npc,
        ScriptedNpcDialogue dialogue,
        int dialogIndex,
        int selection,
        FortuneTellerAction action,
        byte sign,
        CancellationToken cancellationToken)
    {
        if (TitleFortuneControls.Store is not { } store || _character is null || _account is null)
        {
            return;
        }

        if (!TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            RejectLostPlayerOwnership();
            return;
        }

        FortuneTellerResult result;
        var now = DateTimeOffset.UtcNow;
        try
        {
            result = await store.ApplyFortuneAsync(
                new CommandSubject(_account.Id, _character.Id),
                ownership,
                action,
                sign,
                FortuneTellerPolicy.Price(),
                now,
                _realmCalendar.GetUtcOffset(now),
                cancellationToken);
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return;
        }

        Console.WriteLine(
            $"[fortune] character={_character.Name} action={action} target={sign} status={result.Status} " +
            $"line={result.Line} sign={result.Sign} lucky={result.LuckyStatus}/{result.LuckyExpiresAt:O} " +
            $"gold={result.Gold} paid={result.GoldPaid}");
        if (result.Status == FortuneTellerStatus.Done)
        {
            _character.ZodiacType = result.Sign;
            _character.ZodiacLuckyStatus = result.LuckyStatus;
            _character.ZodiacLuckyExpiresAt = result.LuckyExpiresAt;
            if (result.GoldPaid > 0)
            {
                _character.Gold = result.Gold;
            }

            _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        }

        await SendScriptedNpcDialogueReplyAsync(npc, dialogue, dialogIndex, selection, [result.Line], cancellationToken);
        if (result.Status == FortuneTellerStatus.Done)
        {
            // The zodiac window's own full sync carries the sign (+0) and the Lucky Day flag (+4).
            await SendZodiacFullSyncAsync(cancellationToken);
            if (result.GoldPaid > 0)
            {
                await _session.SendAsync(BuildLocalPlayerStatusUpdate(), cancellationToken, "FortuneTellerWallet");
            }
        }
    }

    /// <summary>The Lucky Day bonus on a monster kill's EXP and Talent EXP (the one-line Progression hook).</summary>
    private (int Experience, int TalentExperience) ApplyZodiacLuckyDayBonus(
        int experience, int talentExperience, DateTimeOffset now)
    {
        if (_character is not { } character ||
            !FortuneTellerPolicy.IsLuckyActive(character.ZodiacLuckyStatus, character.ZodiacLuckyExpiresAt, now) ||
            !FortuneTellerPolicy.IsEnabled())
        {
            return (experience, talentExperience);
        }

        var percent = FortuneTellerPolicy.LuckyBonusPercent();
        var boostedExperience = FortuneTellerPolicy.ApplyLuckyBonus(
            experience, percent, Godswar.Server.Application.Rewards.MonsterDeathRewardCommandEnvelope.MaximumAwardedExperience);
        var boostedTalent = FortuneTellerPolicy.ApplyLuckyBonus(
            talentExperience, percent, Godswar.Server.Application.Rewards.MonsterDeathRewardCommandEnvelope.MaximumAwardedTalentExperience);
        Console.WriteLine(
            $"[fortune] lucky-day bonus character={character.Name} percent={percent} " +
            $"exp={experience}->{boostedExperience} talent-exp={talentExperience}->{boostedTalent}");
        return (boostedExperience, boostedTalent);
    }
}
