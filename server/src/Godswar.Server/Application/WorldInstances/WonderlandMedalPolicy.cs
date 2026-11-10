namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// ECONOMY r10 (2026-11-03): Medals (character_base.exchange_medal, the Point Exchanger's Medal balance) paid to
/// every finisher when a Wonderland island clears.
/// </summary>
/// <remarks>
/// Retail table (IGG guide, igg-acid=200, "Stage / Boss / Points / Medals"): stage 1 Alpha Demon 50; stage 2
/// Depraved Monkeyface 60 + Capritaur Derskey 70; stage 3 Flame Rooster 80; stage 4 Outrageous Rock Spirit 90;
/// stage 5 Spartan Marshal Knocker 100 or Athenian Marshal Addis 100 (a party fights only the enemy faction's
/// marshal: the own-faction one is allied, see WonderlandMonsterPlan); stage 6 Depraved Platinum Dragon 110;
/// stage 7 Iberian Multi-headed 120; stage 8 Distraught Minotaur 135, Titan's X'mas Deer 140, Iberian Dragon King 145,
/// Scorpion King 150, Mobby 150. The guide lists Medals per boss; this server pays the island's boss sum when the
/// island clears (an island clears only when its required bosses are dead). Island 8 here has no Mobby, so it pays
/// 135 + 140 + 145 + 150 = 570 (the guide's Mobby 150 is not paid). A full run pays 1,250, enough for one
/// 1,200-Medal Point Exchanger item (Promotional Insignia III 14069, Dust of Destruction 9958, Dust of Penetration
/// 9959). Scaled by server rate instance.wonderland_medals (1 = guide, 0 = off).
/// </remarks>
internal static class WonderlandMedalPolicy
{
    public const string Revision = "wonderland-medals-v1";

    public static int Resolve(int islandNumber) => islandNumber switch
    {
        1 => 50,
        2 => 60 + 70,
        3 => 80,
        4 => 90,
        5 => 100,
        6 => 110,
        7 => 120,
        8 => 135 + 140 + 145 + 150,
        _ => 0
    };

    public static int Scaled(int islandNumber) =>
        Godswar.Server.Application.Rates.ServerRates.Current.Multiply(
            Godswar.Server.Application.Rates.ServerRateKeys.WonderlandMedals,
            Resolve(islandNumber),
            100_000);
}

internal readonly record struct WonderlandMedalAward(int AccountId, int CharacterId, int Medals, int MedalsAfter, bool NewlyAwarded);

internal interface IWonderlandMedalStore
{
    Task<IReadOnlyList<WonderlandMedalAward>> AwardAsync(
        WonderlandTitleRequest request,
        int medalsPerMember,
        CancellationToken cancellationToken = default);
}
