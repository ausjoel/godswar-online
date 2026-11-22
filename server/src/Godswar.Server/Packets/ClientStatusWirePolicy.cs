using Godswar.Server.Application.Finalise2;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

/// <summary>
/// Decides which composed statuses reach the client's buff bar in opcode 10167.
/// </summary>
/// <remarks>
/// <para>
/// Statuses 1500-1507 exist only on this server (<c>ExperienceBoostState.cs</c>):
/// the five donator tiers, the faction area EXP bonus and the Battle Pass. The
/// stock client's <c>Status.ini</c> has no row for any of them, so it drew an
/// icon with no picture, no name and no text. They are account settings rather
/// than buffs a player started, so they are only sent when
/// <c>status.account_bonus_icons</c> is on, which wants client 1.5.1 (it adds
/// the seven rows). The bonus itself is applied on the server and never depends
/// on the icon.
/// </para>
/// <para>
/// The client keeps a status timer in 32-bit milliseconds. A remaining time
/// above 4,294,967 seconds (49.7 days) wrapped: a Battle Pass that ends in 2040
/// showed "608 hours". Anything that long is sent as the client's permanent
/// value instead; the server still ends it at its real time.
/// </para>
/// </remarks>
internal static class ClientStatusWirePolicy
{
    /// <summary>The client's own "no timer" value.</summary>
    public const uint PermanentSeconds = uint.MaxValue;

    /// <summary>The longest countdown the client's millisecond timer holds.</summary>
    public const uint MaximumCountdownSeconds = uint.MaxValue / 1000;

    public static bool IsAccountBonusStatus(uint statusId) => statusId is
        ExperienceStatusIds.KijinPatron or
        ExperienceStatusIds.OniPatron or
        ExperienceStatusIds.DemonLordSeed or
        ExperienceStatusIds.TrueDemonLord or
        ExperienceStatusIds.FactionAreaExperience or
        ExperienceStatusIds.OctagramPatron or
        ExperienceStatusIds.PremiumBattlePass;

    public static IReadOnlyList<ClientStatusEffect> Apply(
        IReadOnlyList<ClientStatusEffect> effects) =>
        Apply(effects, Finalise2Rates.On(Finalise2RateKeys.AccountBonusIcons));

    public static IReadOnlyList<ClientStatusEffect> Apply(
        IReadOnlyList<ClientStatusEffect> effects,
        bool showAccountBonusIcons)
    {
        ArgumentNullException.ThrowIfNull(effects);
        List<ClientStatusEffect>? changed = null;
        for (var index = 0; index < effects.Count; index++)
        {
            var effect = effects[index];
            var hidden = !showAccountBonusIcons &&
                IsAccountBonusStatus(effect.StatusId);
            var wraps = effect.RemainingSeconds > MaximumCountdownSeconds &&
                effect.RemainingSeconds != PermanentSeconds;
            if (!hidden && !wraps)
            {
                changed?.Add(effect);
                continue;
            }

            changed ??= [.. effects.Take(index)];
            if (!hidden)
            {
                changed.Add(effect with { RemainingSeconds = PermanentSeconds });
            }
        }

        return changed ?? effects;
    }
}
