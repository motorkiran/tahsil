using System.Diagnostics.CodeAnalysis;

namespace Tahsil.Domain.Common;

/// <summary>An expected failure: a stable, machine-readable code plus a message for people.</summary>
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "The domain is not consumed from Visual Basic.")]
public sealed record Error(string Code, string Message, ErrorType Type)
{
    /// <summary>The request field a validation error is about, reported in the problem's <c>errors</c>.</summary>
    public string? Field { get; init; }

    /// <summary>Further members of the problem, such as the ID of the record a conflict is with.</summary>
    public IReadOnlyDictionary<string, object?>? Extensions { get; init; }

    public static Error Validation(string code, string message, string? field = null) => new(code, message, ErrorType.Validation) { Field = field };

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    public static Error Unauthorized(string code, string message) => new(code, message, ErrorType.Unauthorized);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

    public static Error Unprocessable(string code, string message) => new(code, message, ErrorType.Unprocessable);

    public static Error PreconditionFailed(string code, string message) => new(code, message, ErrorType.PreconditionFailed);

    public static Error RateLimited(string code, string message) => new(code, message, ErrorType.RateLimited);

    public static Error Unavailable(string code, string message) => new(code, message, ErrorType.Unavailable);

    /// <summary>
    /// Something that should not happen. The message is for the log only: callers get a generic answer with a trace ID.
    /// </summary>
    public static Error Failure(string code, string message) => new(code, message, ErrorType.Failure);
}

public enum ErrorType
{
    Validation,
    NotFound,
    Conflict,

    /// <summary>The caller is not authenticated, or with credentials that do not work here.</summary>
    Unauthorized,

    Forbidden,

    /// <summary>The request is well-formed but contradicts an earlier one, such as an idempotency key reused for another request.</summary>
    Unprocessable,

    /// <summary>The caller's precondition (<c>If-Match</c>) does not hold.</summary>
    PreconditionFailed,

    RateLimited,

    /// <summary>A dependency is unavailable, such as the database or a disabled provider connection.</summary>
    Unavailable,

    Failure,
}
