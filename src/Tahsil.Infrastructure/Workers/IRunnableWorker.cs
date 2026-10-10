namespace Tahsil.Infrastructure.Workers;

/// <summary>A worker whose batches can be run on demand, such as by tests that drive the workers themselves.</summary>
internal interface IRunnableWorker
{
    string Name { get; }

    /// <summary>Processes what is due now, up to one batch; returns how many items it took.</summary>
    Task<int> RunBatchAsync(CancellationToken cancellationToken);
}
