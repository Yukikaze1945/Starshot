using System;
using System.Diagnostics;
using System.Threading;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("RegionToolbarUiTest")]

namespace Starshot.Features.Screenshot;

// Opt-in isolated-test observer. Disabled in normal operation. Never keeps a buffer/task alive.
internal readonly record struct HdrAnalysisDiagnosticEvent(string Stage, int Generation, long Timestamp,
    double Milliseconds, WeakReference? Reference, int Operation, bool IsTask);

internal static class HdrAnalysisDiagnostics
{
    internal static Action<HdrAnalysisDiagnosticEvent>? Listener;
    private static int _operation;
    internal static bool Enabled => Listener is not null;
    internal static void Track(string kind, object value, int generation = 0)
    {
        var listener = Listener;
        if (listener is not null) listener(new("object:" + kind, generation, Stopwatch.GetTimestamp(), 0, new WeakReference(value), 0, false));
    }
    internal static IDisposable? Measure(string stage, int generation = 0, bool task = false)
        => Listener is null ? null : new Timing(stage, generation, task);
    internal static void Mark(string stage, int generation)
        => Listener?.Invoke(new(stage, generation, Stopwatch.GetTimestamp(), 0, null, 0, false));
    private sealed class Timing : IDisposable
    {
        private readonly string _stage;
        private readonly int _generation, _operation;
        private readonly bool _task;
        private readonly long _start = Stopwatch.GetTimestamp();
        public Timing(string stage, int generation, bool task)
        {
            _stage = stage; _generation = generation; _task = task;
            _operation = Interlocked.Increment(ref HdrAnalysisDiagnostics._operation);
            Listener?.Invoke(new(stage + ".begin", generation, _start, 0, null, _operation, task));
        }
        public void Dispose() => Listener?.Invoke(new(_stage + ".end", _generation, Stopwatch.GetTimestamp(),
            Stopwatch.GetElapsedTime(_start).TotalMilliseconds, null, _operation, _task));
    }
}
