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

/// <summary>Documents the permission each operation requires, and the responses for callers who may not call it.</summary>
internal sealed class PermissionOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<RequiredPermission>().LastOrDefault() is not { } required)
        {
            return Task.CompletedTask;
        }

        var requirement = $"Requires the `{required.Permission}` permission.";
        operation.Description = operation.Description is null ? requirement : $"{operation.Description}\n\n{requirement}";
        operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(TahsilApiDocumentTransformer.SecuritySchemeName, context.Document)] = [] }];

        operation.Responses ??= [];
        operation.Responses.TryAdd("401", new OpenApiResponse { Description = "The API key is missing, unknown, revoked, expired or of the other mode." });
        operation.Responses.TryAdd("403", new OpenApiResponse { Description = $"The API key does not have `{required.Permission}`." });

        return Task.CompletedTask;
    }
}

/// <summary>Documents the <c>Idempotency-Key</c> header that every <c>POST</c> accepts.</summary>
internal sealed class IdempotencyOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (!string.Equals(context.Description.HttpMethod, HttpMethods.Post, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = IdempotencyFilter.HeaderName,
            In = ParameterLocation.Header,
            Description = "Makes the request safe to retry: a repeat with the same key within 24 hours gets the first response, "
                + "marked `Idempotent-Replayed: true`. 1 to 255 printable ASCII characters, such as a UUID.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 255 },
        });

        operation.Responses ??= [];
        operation.Responses.TryAdd("409", new OpenApiResponse { Description = "A request with the same idempotency key is still running (`idempotency.in_progress`)." });
        operation.Responses.TryAdd("422", new OpenApiResponse { Description = "The idempotency key was used for a different request (`idempotency.key_reused`)." });

        return Task.CompletedTask;
    }
}
