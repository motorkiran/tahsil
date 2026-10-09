using Tahsil.Infrastructure.Persistence;

namespace Tahsil.Infrastructure.Security;

/// <summary>
/// A place where values encrypted with the master key are stored. Features register theirs, so that
/// <c>reencrypt</c> can move every value to the current master key.
/// </summary>
internal interface IEncryptedStore
{
    string Name { get; }

    /// <summary>
    /// Re-encrypts up to <paramref name="batchSize"/> values that the current master key did not encrypt, in the
    /// caller's transaction, and returns how many it rewrote.
    /// </summary>
    Task<int> ReencryptBatchAsync(TahsilDbContext context, ISecretProtector protector, int batchSize, CancellationToken cancellationToken);
}
