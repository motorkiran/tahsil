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
