namespace Tahsil.Domain.Common;

/// <summary>
/// The root of a consistency boundary. Its row version guards the whole aggregate against concurrent writes: an
/// update based on an older version fails instead of overwriting a change it has not seen.
/// </summary>
public abstract class AggregateRoot
{
    /// <summary>The row version (PostgreSQL's <c>xmin</c>), which changes on every write and is the API's ETag.</summary>
    public uint Version { get; private set; }
}
