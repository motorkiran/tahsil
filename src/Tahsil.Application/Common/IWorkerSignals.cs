namespace Tahsil.Application.Common;

/// <summary>
/// Wakes a worker of this instance when a use case made something due now, such as an invoice to charge. Other
/// instances notice within their polling interval.
/// </summary>
public interface IWorkerSignals
{
    /// <summary>Wakes the worker named <paramref name="worker"/>; call after the change is committed.</summary>
    void Notify(string worker);
}
