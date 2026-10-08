using System;
using System.Collections.Generic;
using System.Threading;

namespace Starshot.Features.Codec;

internal static class StaticVideoJobs
{
    private static readonly object Sync = new();
    private static readonly HashSet<CancellationTokenSource> Active = new();
    internal static IDisposable Register(CancellationTokenSource cancel)
    {
        lock (Sync) Active.Add(cancel);
        return new Registration(cancel);
    }
    internal static void CancelAll()
    {
        lock (Sync) foreach (var cancel in Active) cancel.Cancel();
    }
    private sealed class Registration(CancellationTokenSource cancel) : IDisposable
    {
        public void Dispose() { lock (Sync) Active.Remove(cancel); }
    }
}
