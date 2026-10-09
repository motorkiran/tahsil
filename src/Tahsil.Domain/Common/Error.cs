using System.Diagnostics.CodeAnalysis;

namespace Tahsil.Domain.Common;

/// <summary>An expected failure: a stable, machine-readable code plus a message for people.</summary>
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "The domain is not consumed from Visual Basic.")]
public sealed record Error(string Code, string Message, ErrorType Type)
{
    public static Error Validation(string code, string message) => new(code, message, ErrorType.Validation);

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

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
    Forbidden,

    /// <summary>The caller's precondition (<c>If-Match</c>) does not hold.</summary>
    PreconditionFailed,

    RateLimited,

    /// <summary>A dependency is unavailable, such as the database or a disabled provider connection.</summary>
    Unavailable,

    Failure,
}
