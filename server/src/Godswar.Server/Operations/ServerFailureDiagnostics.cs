using System.Diagnostics;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Operations.Observability;

namespace Godswar.Server.Operations;

internal static class ServerFailureDiagnostics
{
    public static void RecordRuntime(BoundedStructuredLogger logger, Exception error) =>
        Record(logger, error, OperationalLogEvent.RuntimeFailure,
            OperationalLogEvent.RuntimeFailureFrame);

    // Preserve bounded method identities without exception messages, SQL,
    // connection details, argument values, or local file paths.
    internal static void Record(BoundedStructuredLogger logger, Exception error,
        OperationalLogEvent failureEvent, OperationalLogEvent frameEvent)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(error);
        var details = PostgresStartupFailureDetails.FromException(error);
        var cause = details.Cause;
        logger.TryWrite(failureEvent, OperationalLogLevel.Critical,
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
            logger.TryWrite(frameEvent, OperationalLogLevel.Critical,
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
        // Async state-machine names start with '<'. Keep their method name
        // after sanitization instead of replacing every async frame with unknown.
        if (code.Length > 0 && !char.IsAsciiLetterOrDigit(code[0]))
            code = "m" + code[..Math.Min(code.Length, SafeTelemetryCode.MaximumLength - 1)];
        return SafeTelemetryCode.IsSafe(code) ? code : "unknown";
    }
}
