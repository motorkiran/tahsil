using Tahsil.Domain.Common;

namespace Tahsil.Application.Common;

/// <summary>
/// The authorization check every handler starts with. Endpoints declare permissions too, but the handler's check
/// is the one that counts, whichever way the use case is reached.
/// </summary>
public interface IAuthorizer
{
    /// <summary>Requires an API key or administrator holding <paramref name="permission"/>; the system passes.</summary>
    Result Require(string permission);

    /// <summary>
    /// Requires the customer of a hosted page session to be <paramref name="customerId"/>; the system passes. Other
    /// callers use the API's own use cases instead.
    /// </summary>
    Result RequireCustomer(Guid customerId);

    /// <summary>
    /// For the use cases the customer portal shares with the API (PRT-3): requires <paramref name="permission"/>, or a
    /// customer of a hosted page session, who is then held to their own records by <see cref="Owns"/>.
    /// </summary>
    Result RequireOrCustomer(string permission);

    /// <summary>
    /// Whether the caller may touch a record of <paramref name="customerId"/>: a customer of a hosted page session only
    /// their own; callers who passed by a permission, any. A record that is not theirs does not exist for them.
    /// </summary>
    bool Owns(Guid customerId);

    /// <summary>Whether a customer acts for themselves, on a hosted page.</summary>
    bool IsCustomer { get; }
}

internal sealed class Authorizer(IRequestContext request) : IAuthorizer
{
    public Result Require(string permission) => request.Caller switch
    {
        SystemCaller => Result.Success(),
        ApiKeyCaller caller when caller.Permissions.Contains(permission) => Result.Success(),
        AdminCaller caller when caller.Permissions.Contains(permission) => Result.Success(),
        _ => AuthErrors.MissingPermission(permission),
    };

    public bool IsCustomer => request.Caller is CustomerCaller;

    public Result RequireOrCustomer(string permission) => IsCustomer ? Result.Success() : Require(permission);

    public bool Owns(Guid customerId) => request.Caller is not CustomerCaller caller || caller.CustomerId == customerId;

    public Result RequireCustomer(Guid customerId) => request.Caller switch
    {
        SystemCaller => Result.Success(),
        CustomerCaller caller when caller.CustomerId == customerId => Result.Success(),
        _ => AuthErrors.NotYourCustomer,
    };
}

public static class AuthErrors
{
    public const string ForbiddenCode = "auth.forbidden";

    public static readonly Error Unauthorized = Error.Unauthorized("auth.unauthorized", "The API key is missing or does not work.");

    public static readonly Error NotYourCustomer = Error.Forbidden(ForbiddenCode, "This session does not belong to the customer.");

    public static Error WrongMode(TahsilMode mode) => Error.Unauthorized(
        "auth.wrong_mode",
        mode == TahsilMode.Live
            ? "This installation is in Live mode and takes ths_live_ keys; this is a test key."
            : "This installation is in Test mode and takes ths_test_ keys; this is a live key.");

    public static Error MissingPermission(string permission) =>
        Error.Forbidden(ForbiddenCode, $"The caller does not have the '{permission}' permission.");
}
