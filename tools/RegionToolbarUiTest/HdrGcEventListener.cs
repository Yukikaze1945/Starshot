using System.Diagnostics.Tracing;

namespace Starshot.ToolbarValidation;

// Test-only runtime GC events. No allocation-tick stream, stack walking or object ownership.
internal sealed class HdrGcEventListener : EventListener
{
    private readonly object _gate = new();
    private readonly List<object> _events = new();
    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Informational, (EventKeywords)1);
    }
    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventId is not (1 or 2 or 3 or 8 or 9) || _gate is null) return;
        lock (_gate) _events.Add(new { name = e.EventName, id = e.EventId, timestamp_utc = e.TimeStamp.ToUniversalTime(),
            payload_names = e.PayloadNames?.ToArray(), payload = e.Payload?.ToArray() });
    }
    internal object[] Snapshot() { lock (_gate) return _events.ToArray(); }
}
