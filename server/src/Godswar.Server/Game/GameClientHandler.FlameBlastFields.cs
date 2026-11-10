using Godswar.Server.Application.Characters;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly TimeProvider _flameBlastTimeProvider;
    private readonly object _flameBlastSync = new();
    private readonly CancellationTokenSource _flameBlastLifetime = new();
    private readonly Dictionary<ulong, Task> _flameBlastTasks = [];
    private readonly List<(ulong FieldId, int Ordinal, long ElapsedTicks)> _flameBlastPulseLog = [];
    private static long _nextFlameBlastFieldId;
    private long _flameBlastPulsesApplied;
    private Task? _flameBlastStopTask;

    // Completed recurring passes for this session. A pass counts only when it
    // was applied against a still-current field source, so tests can observe
    // scheduler progress without racing monster health or the packet socket.
    internal long FlameBlastPulsesApplied => Interlocked.Read(ref _flameBlastPulsesApplied);

    // Applied passes in order, with the field they belong to and the elapsed
    // field time. Tests use this to prove that two casts own two independent
    // schedules instead of refreshing or restarting each other.
    internal IReadOnlyList<(ulong FieldId, int Ordinal, long ElapsedTicks)> FlameBlastPulseLog
    {
        get
        {
            lock (_flameBlastPulseLog) return _flameBlastPulseLog.ToArray();
        }
    }

    private FlameBlastSource? CaptureFlameBlastFieldSource(
        GameCharacter character, SkillCombatDefinition combat) =>
        FlameBlastPulsePolicy.AppliesTo(combat) &&
        _registry.TryCaptureFlameBlastSource(_session, character, out var source)
            ? source : null;

    private void StartFlameBlastField(FlameBlastSource? source,
        SkillCombatDefinition combat, float centerX, float centerZ)
    {
        if (source is not { } captured || !_registry.IsCurrentFlameBlastSource(captured)) return;
        lock (_flameBlastSync)
        {
            if (_flameBlastStopTask is not null) return;
            var fieldId = checked((ulong)Interlocked.Increment(ref _nextFlameBlastFieldId));
            var startedAt = _flameBlastTimeProvider.GetTimestamp();
            // Every accepted cast owns its own field with its own timer. A
            // recast places a second independent field: it never refreshes,
            // restarts or merges with a field that is already ticking.
            var task = RunFlameBlastFieldAsync(captured, combat, centerX, centerZ,
                fieldId, startedAt, _flameBlastLifetime.Token);
            if (!task.IsCompleted) _flameBlastTasks.Add(fieldId, task);
        }
    }

    private async Task RunFlameBlastFieldAsync(FlameBlastSource source,
        SkillCombatDefinition combat, float centerX, float centerZ,
        ulong fieldId, long startedAt, CancellationToken cancellationToken)
    {
        try
        {
            // The accepted cast already committed ordinal zero, so one further
            // pass lands per interval for the remaining lifetime. Each field
            // reserves no cast, mana, cooldown or intonation of its own.
            for (var ordinal = 1; ordinal < FlameBlastPulsePolicy.TotalPulses; ordinal++)
            {
                var due = FlameBlastPulsePolicy.Interval * ordinal;
                var delay = due - _flameBlastTimeProvider.GetElapsedTime(startedAt);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, _flameBlastTimeProvider, cancellationToken);

                await _characterStateGate.WaitAsync(cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var elapsed = _flameBlastTimeProvider.GetElapsedTime(startedAt);
                    if (!_registry.IsCurrentFlameBlastSource(source)) return;
                    // A stalled handler must not burst old damage after the
                    // field's scheduled window. Each ordinal is visited once,
                    // and a field whose whole window has passed is finished.
                    if (elapsed >= due + FlameBlastPulsePolicy.Interval) break;
                    if (!await ApplyFlameBlastPulseAsync(source, combat, centerX, centerZ,
                            fieldId, ordinal, _flameBlastTimeProvider.GetUtcNow(),
                            cancellationToken)) return;
                    Interlocked.Increment(ref _flameBlastPulsesApplied);
                    lock (_flameBlastPulseLog)
                        _flameBlastPulseLog.Add((fieldId, ordinal, elapsed.Ticks));
                }
                finally
                {
                    _characterStateGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[skill] Flame Blast field failed field={fieldId}: {ex.Message}");
            _session.Disconnect();
        }
        finally
        {
            lock (_flameBlastSync) _flameBlastTasks.Remove(fieldId);
        }
    }

    private Task StopFlameBlastFieldsAsync()
    {
        TaskCompletionSource stopped;
        Task[] tasks;
        lock (_flameBlastSync)
        {
            // Join every field before session teardown can dispose its gate.
            if (_flameBlastStopTask is not null) return _flameBlastStopTask;
            stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _flameBlastStopTask = stopped.Task;
            tasks = _flameBlastTasks.Values.ToArray();
        }
        // Cancellation callbacks and task completion run outside the task lock.
        _ = StopFlameBlastFieldsCoreAsync(tasks, stopped);
        return stopped.Task;
    }

    private async Task StopFlameBlastFieldsCoreAsync(Task[] tasks, TaskCompletionSource stopped)
    {
        try
        {
            _flameBlastLifetime.Cancel();
            await Task.WhenAll(tasks);
            _flameBlastLifetime.Dispose();
            stopped.SetResult();
        }
        catch (Exception ex)
        {
            _flameBlastLifetime.Dispose();
            stopped.SetException(ex);
        }
    }
}
