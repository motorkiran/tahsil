using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Tahsil.Server.Api;

internal sealed class TahsilApiDocumentTransformer : IOpenApiDocumentTransformer
{
    public const string SecuritySchemeName = "ApiKey";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "Tahsil API",
            Version = TahsilApi.DocumentName,
            Description = "The Tahsil API manages the catalog, customers, checkout sessions, subscriptions, invoices, payments "
                + "and refunds of a Tahsil installation. Calls take an API key as a bearer token, carrying the permission each "
                + "operation names; `ths_test_` keys reach a Test mode installation and `ths_live_` keys a Live one. Errors are "
                + "RFC 9457 problem details with a stable, machine-readable `code` and a `traceId`.",
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
        document.Components.SecuritySchemes[SecuritySchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            Description = "An API key created in the admin panel or through the API, such as `ths_test_…`.",
        };

        return Task.CompletedTask;
    }
}
