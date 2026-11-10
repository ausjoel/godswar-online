using Godswar.Server.Packets;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.State;

internal sealed record ElementalClientStatusOverlay(
    IReadOnlyList<ClientStatusEffect> Effects,
    string Fingerprint)
{
    public static ElementalClientStatusOverlay Empty { get; } = new(
        [],
        ElementalClientStatusProjection.EmptyFingerprint);
}

/// <summary>
/// Projects only client-audited elemental presentation. Combat authority stays
/// in <see cref="ElementalStatusState"/>; these entries carry no stat modifiers.
/// </summary>
internal static class ElementalClientStatusProjection
{
    internal const uint BurnStatusId = 40;
    internal const int BurnStatusKind = 2;
    internal const int BurnStatusPriority = 1;
    internal const string EmptyFingerprint = "elemental-client:none";

    public static ElementalClientStatusOverlay Create(
        ElementalStatusSnapshot snapshot,
        DateTimeOffset now)
    {
        var nowMilliseconds = now.ToUnixTimeMilliseconds();
        var burn = snapshot.ActiveEffects.FirstOrDefault(static effect =>
            effect.Effect == ElementalEffectKind.Burn);
        if (burn.Effect != ElementalEffectKind.Burn ||
            burn.ExpiresAtMilliseconds <= nowMilliseconds)
        {
            return ElementalClientStatusOverlay.Empty;
        }

        var remainingMilliseconds = checked(
            burn.ExpiresAtMilliseconds - nowMilliseconds);
        var roundedSeconds = checked(
            (remainingMilliseconds / 1_000L) +
            (remainingMilliseconds % 1_000L == 0 ? 0L : 1L));
        var remainingSeconds = checked((uint)Math.Clamp(
            roundedSeconds,
            1L,
            uint.MaxValue));
        return new ElementalClientStatusOverlay(
            [new ClientStatusEffect(BurnStatusId, remainingSeconds)],
            $"elemental-client:{BurnStatusId}:" +
            burn.ExpiresAtMilliseconds);
    }

    public static PlayerStatusSnapshot Merge(
        PlayerStatusSnapshot baseline,
        ElementalClientStatusOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(overlay);
        if (overlay.Effects.Count == 0)
        {
            return baseline;
        }

        var presentations = baseline.Presentations
            .Concat(overlay.Effects.Select(static effect =>
                new ClientStatusPresentation(
                    effect,
                    Beneficial: false,
                    BurnStatusPriority,
                    ClientStatusPresentationClass.DisplayOnly)))
            .ToArray();
        return PlayerStatusCapacityPolicy.Apply(baseline with
        {
            Presentations = presentations,
            Fingerprint = $"{baseline.Fingerprint}#{overlay.Fingerprint}"
        });
    }
}
