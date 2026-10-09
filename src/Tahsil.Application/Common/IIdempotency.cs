using Tahsil.Domain.Common;

namespace Tahsil.Application.Common;

/// <summary>
/// Makes a retried request safe after a crash. A handler that creates an aggregate calls <see cref="RecordCreated"/>
/// before it saves, so the request's idempotency record names the new resource in the same transaction. A retry
/// that finds the resource recorded returns it instead of creating a second one. Outside of an idempotent request,
/// this does nothing.
/// </summary>
public interface IIdempotency
{
    void RecordCreated(string resourceType, Guid id);

    /// <summary>
    /// Records nothing for the rest of the request, for use cases that create several resources and are safe to run
    /// again, such as applying a provisioning document.
    /// </summary>
    void Suppress();
}

public static class IdempotencyErrors
{
    public static readonly Error InvalidKey = Error.Validation(
        CommonErrors.InvalidRequestCode, "The Idempotency-Key header must be 1 to 255 printable ASCII characters.");

    public static readonly Error KeyReused = Error.Unprocessable(
        "idempotency.key_reused", "This Idempotency-Key was used for a different request; use a new key for a new request.");

    public static readonly Error InProgress = Error.Conflict(
        "idempotency.in_progress", "A request with this Idempotency-Key is still being processed; retry in a moment.");
}
