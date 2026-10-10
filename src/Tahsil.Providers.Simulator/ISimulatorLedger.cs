namespace Tahsil.Providers.Simulator;

public enum SimulatorEntryKind
{
    Payment,
    Refund,
    Card,
}

/// <summary>A record of the simulated provider: a payment, a refund or a saved card, with its state as JSON.</summary>
/// <param name="Id">The record's ID.</param>
/// <param name="ConnectionId">The connection it belongs to.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Reference">Tahsil's reference of the payment or refund, or the card's token.</param>
/// <param name="TokenHash">The SHA-256 of the card page's token, for payments with the customer present.</param>
/// <param name="State">The simulator's state as JSON.</param>
/// <param name="Version">The stored version; 0 for a record that is not stored yet.</param>
/// <param name="CreatedAt">When it was created, by the billing clock.</param>
public sealed record SimulatorEntry(
    Guid Id, Guid ConnectionId, SimulatorEntryKind Kind, string Reference, byte[]? TokenHash, string State, uint Version, DateTimeOffset CreatedAt);

/// <summary>
/// Where the simulated provider keeps its records (SIM-10). The host stores them in its own database, outside the
/// caller's transaction, as a real provider's records are.
/// </summary>
public interface ISimulatorLedger
{
    Task<SimulatorEntry?> FindAsync(Guid connectionId, SimulatorEntryKind kind, string reference, CancellationToken cancellationToken);

    Task<SimulatorEntry?> FindByTokenAsync(byte[] tokenHash, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a new record (version 0) or an update of the stored version. Returns <see langword="false"/> when
    /// another write got there first: the version changed, or a record with the reference exists.
    /// </summary>
    Task<bool> SaveAsync(SimulatorEntry entry, CancellationToken cancellationToken);
}
