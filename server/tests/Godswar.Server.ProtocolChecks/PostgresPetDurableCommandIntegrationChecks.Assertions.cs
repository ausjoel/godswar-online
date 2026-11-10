using Godswar.Server.Application.Pets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static void AssertCommitAndDuplicate(
        IReadOnlyList<PetDurableExecutionResult> results,
        PetDurableReceiptStatus status,
        string phase)
    {
        var outcomes = string.Join(
            ", ",
            results.Select(result =>
                $"{result.Disposition}/{result.Receipt?.Status}"));
        Check.Equal(
            1,
            results.Count(result => result.Disposition ==
                PetDurableExecutionDisposition.Committed),
            $"{phase} commits once ({outcomes})");
        Check.Equal(
            1,
            results.Count(result => result.Disposition ==
                PetDurableExecutionDisposition.Duplicate),
            $"{phase} replays once ({outcomes})");
        Check.True(
            results.All(result => result.Receipt?.Status == status) &&
            results[0].Receipt == results[1].Receipt,
            $"{phase} returns one canonical receipt ({outcomes})");
    }
}
