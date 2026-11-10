namespace Godswar.Server.State;

/// <summary>
/// Project cadence for the summoned pet's satiety and lifetime drain. The
/// installed client ships no decay rate of its own; only the item text that
/// describes recovery exists, so this policy is the single authority for the
/// drain. Recall resets accrual, so a recalled or offline pet never decays.
/// </summary>
internal static class PetCareDecayPolicy
{
    /// <summary>
    /// Dedicated timer cadence. The drain has its own timer on purpose: the
    /// Owner Merge drain and recharge loops stop whenever their own condition
    /// ends, so neither can carry a care clock.
    /// </summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    /// <summary>Ten summoned minutes cost two satiety and two lifetime.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    public const int SatietyPointsPerInterval = 2;
    public const int LifetimePointsPerInterval = 2;

    public const int MinimumSatiety = 0;
    public const int MaximumSatiety = 100;
    public const int MinimumAmity = 0;
    public const int MaximumAmity = 100;
    public const int MinimumLifetime = 0;
    public const int MaximumLifetime = 1500;

    public static int Decay(int current, int points) =>
        Math.Max(MinimumSatiety, current - points);

    public static bool CanBeSummoned(int satiety, int lifetime) =>
        satiety > MinimumSatiety && lifetime > MinimumLifetime;
}
