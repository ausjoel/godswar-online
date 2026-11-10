using System.Diagnostics;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Operations.Observability;

namespace Godswar.Server.Operations;

internal static class StartupFailureDiagnostics
{
    // Exception messages, SQL detail, connection strings, file paths, and
    // argument values are deliberately absent. Method identities retain a
    // useful bounded stack without exposing data carried by the failure.
    public static void Record(BoundedStructuredLogger logger, Exception error)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(error);
        var details = PostgresStartupFailureDetails.FromException(error);
        var cause = details.Cause;

        logger.TryWrite(
            OperationalLogEvent.StartupFailure,
            OperationalLogLevel.Critical,
            OperationalLogValue.FromCode(OperationalLogField.Component, "server"),
            OperationalLogValue.FromCode(OperationalLogField.ExceptionType,
                ToCode(cause.GetType().Name)),
            OperationalLogValue.FromCode(OperationalLogField.SqlState,
                details.SqlState is { } sqlState ? ToCode(sqlState) : "none"),
            OperationalLogValue.FromCode(OperationalLogField.MigrationId,
                SafeTelemetryCode.IsSafe(details.MigrationId)
                    ? details.MigrationId! : "none"));

        var frames = new StackTrace(cause, fNeedFileInfo: false).GetFrames();
        for (var index = 0; index < Math.Min(frames.Length, 8); index++)
        {
            var method = frames[index].GetMethod();
            logger.TryWrite(
                OperationalLogEvent.StartupFailureFrame,
                OperationalLogLevel.Critical,
                OperationalLogValue.FromNumber(OperationalLogField.Count, index),
                OperationalLogValue.FromCode(OperationalLogField.Frame,
                    ToCode($"{method?.DeclaringType?.Name}.{method?.Name}")));
        }
    }

    private static string ToCode(string value)
    {
        var code = new string(value.ToLowerInvariant()
            .Select(static character => char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or '-' ? character : '_')
            .Take(SafeTelemetryCode.MaximumLength).ToArray());
        return SafeTelemetryCode.IsSafe(code) ? code : "unknown";
    }
}
