namespace Tahsil.Server.Api;

/// <summary>
/// Endpoint metadata naming the permission an operation requires. The handler checks it again; this declaration
/// documents it in OpenAPI and lets the guard tests prove no endpoint forgot one.
/// </summary>
internal sealed record RequiredPermission(string Permission);

internal static class RequiredPermissionExtensions
{
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequiredPermission(permission));
}
