// ROUND3 F-70 (the admin, 2026-11-09, console tab F-70): the scroll quests 646 (Sparta) and 1646 (Athens)
// "[Lv131] Summons (4)" open up to Lv200 under the SAME switch as F-123 (quest.caps_120_to_200).
// Hand-written on purpose: QuestLevelCapRaise120.Generated.cs keeps exactly the 196 ids its generator writes.
// These two are not main story: without the switch they keep the nine-level band (MinLevel-5 .. MinLevel+9 = Lv126-140);
// with it the upper bound becomes 200 (the floor stays MinLevel-5). Their scroll items 4852/4853 say PlayLv 131-200.
// Client 1.4.6 Quest.xml still has MaxLevel 120 for both (below their MinLevel 131, as stock).
namespace Godswar.Server.Domain.World.Content;

internal static class QuestLevelCapRaiseF70Scroll
{
    public const int RaisedMaximumLevel = 200;

    public static readonly IReadOnlySet<uint> QuestIds = new HashSet<uint> { 646u, 1646u };

    /// <summary>True only while the hot rate quest.caps_120_to_200 is 1 (the F-123 switch).</summary>
    public static bool Applies(uint questId) =>
        QuestIds.Contains(questId) &&
        Application.Finalise2.Finalise2Rates.On(Application.Finalise2.Finalise2RateKeys.QuestCaps120To200);
}
