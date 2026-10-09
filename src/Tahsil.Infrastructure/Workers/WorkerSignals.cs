using System.Collections.Concurrent;
using Tahsil.Application.Common;

namespace Tahsil.Infrastructure.Workers;

/// <summary>Wakes the workers of this instance by name, so they need not wait for their next poll.</summary>
internal sealed class WorkerSignals : IWorkerSignals, IDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _signals = new(StringComparer.Ordinal);

    public void Notify(string worker)
    {
        try
        {
            Signal(worker).Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signaled and not yet observed.
        }
    }

    /// <summary>Waits for a signal or the timeout; returns whether a signal arrived.</summary>
    public Task<bool> WaitAsync(string worker, TimeSpan timeout, CancellationToken cancellationToken) =>
        Signal(worker).WaitAsync(timeout, cancellationToken);

    public void Dispose()
    {
        foreach (var signal in _signals.Values)
        {
            signal.Dispose();
        }
    }

    private SemaphoreSlim Signal(string worker) => _signals.GetOrAdd(worker, static _ => new SemaphoreSlim(0, 1));
}
