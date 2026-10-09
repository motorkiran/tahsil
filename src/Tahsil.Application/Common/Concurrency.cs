using Tahsil.Domain.Common;

namespace Tahsil.Application.Common;

/// <summary>Optimistic concurrency for callers that send the version they last read (<c>If-Match</c>).</summary>
public static class Concurrency
{
    /// <summary>
    /// Succeeds when the caller sent no version, or one of the versions it sent is the aggregate's current one.
    /// </summary>
    public static Result Check(AggregateRoot aggregate, IReadOnlyCollection<uint>? expectedVersions) =>
        expectedVersions is null || expectedVersions.Contains(aggregate.Version)
            ? Result.Success()
            : CommonErrors.PreconditionFailed;
}
