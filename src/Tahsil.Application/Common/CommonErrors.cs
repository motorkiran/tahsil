using Tahsil.Domain.Common;

namespace Tahsil.Application.Common;

/// <summary>Errors shared by every feature; feature errors live in their own catalogs.</summary>
public static class CommonErrors
{
    /// <summary>The code of every malformed request, whether a parameter, the body or a cursor could not be read.</summary>
    public const string InvalidRequestCode = "request.invalid";

    public static readonly Error InvalidCursor = Error.Validation(InvalidRequestCode, "The cursor is not valid for this list.");

    public static readonly Error ConcurrencyConflict = Error.Conflict(
        "concurrency.conflict",
        "Another request changed the resource at the same time. Read it again and retry.");

    public static readonly Error PreconditionFailed = Error.PreconditionFailed(
        "concurrency.precondition_failed",
        "The resource changed since the version in If-Match. Read it again and retry.");

    public static Error InvalidParameter(string name) => Error.Validation(InvalidRequestCode, $"The '{name}' parameter is not valid.");

    public static Error InvalidLimit() =>
        Error.Validation(InvalidRequestCode, $"The 'limit' parameter must be between 1 and {Paging.MaxLimit}.");
}
