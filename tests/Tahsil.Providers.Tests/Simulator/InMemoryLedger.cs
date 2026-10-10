using System.Collections.Concurrent;
using Tahsil.Providers.Simulator;

namespace Tahsil.Providers.Tests.Simulator;

/// <summary>The simulator's ledger in memory, with the stored versions of the database's.</summary>
internal sealed class InMemoryLedger : ISimulatorLedger
{
    private readonly ConcurrentDictionary<(Guid, SimulatorEntryKind, string), SimulatorEntry> _entries = new();
    private readonly Lock _gate = new();

    public IEnumerable<SimulatorEntry> Entries => _entries.Values;

    public Task<SimulatorEntry?> FindAsync(Guid connectionId, SimulatorEntryKind kind, string reference, CancellationToken cancellationToken) =>
        Task.FromResult(_entries.GetValueOrDefault((connectionId, kind, reference)));

    public Task<SimulatorEntry?> FindByTokenAsync(byte[] tokenHash, CancellationToken cancellationToken) =>
        Task.FromResult(_entries.Values.FirstOrDefault(entry => entry.TokenHash?.AsSpan().SequenceEqual(tokenHash) == true));

    public Task<bool> SaveAsync(SimulatorEntry entry, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var key = (entry.ConnectionId, entry.Kind, entry.Reference);
            var stored = _entries.GetValueOrDefault(key);
            if ((stored?.Version ?? 0) != entry.Version)
            {
                return Task.FromResult(false);
            }

            _entries[key] = entry with { Version = entry.Version + 1 };
            return Task.FromResult(true);
        }
    }
}
