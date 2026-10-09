using System.ComponentModel;
using Microsoft.AspNetCore.Http.HttpResults;
using Tahsil.Application.ApiKeys;
using Tahsil.Contracts;

namespace Tahsil.Server.Api;

/// <summary>The API keys that applications call Tahsil with.</summary>
internal static class ApiKeyEndpoints
{
    public static IEndpointRouteBuilder MapApiKeyEndpoints(this IEndpointRouteBuilder api)
    {
        var keys = api.MapGroup("api-keys").WithTags("API keys");

        keys.MapGet(string.Empty, ListAsync).WithName("ListApiKeys").WithSummary("List API keys")
            .WithDescription("Lists API keys in creation order, revoked and expired ones included.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .RequirePermission(TahsilPermissions.ApiKeysRead);

        keys.MapPost(string.Empty, CreateAsync).WithName("CreateApiKey").WithSummary("Create an API key")
            .WithDescription("Creates a key with permissions the caller holds itself. The response is the only time the key's secret is shown.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(TahsilPermissions.ApiKeysWrite);

        keys.MapGet("{id:guid}", GetAsync).WithName("GetApiKey").WithSummary("Get an API key")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.ApiKeysRead);

        keys.MapPost("{id:guid}/revoke", RevokeAsync).WithName("RevokeApiKey").WithSummary("Revoke an API key")
            .WithDescription("The key stops working at once, and stays listed with the time it was revoked.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(TahsilPermissions.ApiKeysWrite);

        return api;
    }

    private static async Task<Results<Ok<Page<ApiKeyResponse>>, ProblemHttpResult>> ListAsync(
        [Description("The `nextCursor` of the previous page.")] string? cursor,
        [Description("Page size, 50 by default and at most 200.")] int? limit,
        ListApiKeysHandler handler,
        CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new ListApiKeysQuery(cursor, limit), cancellationToken)).ToOk();

    private static async Task<Results<Created<CreatedApiKeyResponse>, ProblemHttpResult>> CreateAsync(
        CreateApiKeyRequest request, CreateApiKeyHandler handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(request, cancellationToken)).ToCreated(created => $"{TahsilApi.BasePath}/api-keys/{created.ApiKey.Id}");

    private static async Task<Results<Ok<ApiKeyResponse>, ProblemHttpResult>> GetAsync(Guid id, GetApiKeyHandler handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(id, cancellationToken)).ToOk();

    private static async Task<Results<Ok<ApiKeyResponse>, ProblemHttpResult>> RevokeAsync(Guid id, RevokeApiKeyHandler handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(id, cancellationToken)).ToOk();
}
