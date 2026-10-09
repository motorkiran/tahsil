using Microsoft.AspNetCore.Authorization;

namespace Tahsil.Server.Api;

/// <summary>
/// Endpoint metadata naming the permission an operation requires. The handler checks it again; this declaration
/// documents it in OpenAPI and lets the guard tests prove no endpoint forgot one.
/// </summary>
internal sealed record RequiredPermission(string Permission);

internal static class RequiredPermissionExtensions
{
    /// <summary>Requires an API key holding <paramref name="permission"/>.</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        var policy = new AuthorizationPolicyBuilder(ApiKeyAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(permission))
            .Build();

        return builder.RequireAuthorization(policy).WithMetadata(new RequiredPermission(permission));
    }
}
