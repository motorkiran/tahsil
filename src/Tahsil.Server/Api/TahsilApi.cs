using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Scalar.AspNetCore;
using Tahsil.Contracts.Serialization;

namespace Tahsil.Server.Api;

/// <summary>The Tahsil API: REST over JSON under <see cref="BasePath"/>, called by applications with API keys.</summary>
internal static class TahsilApi
{
    public const string BasePath = "/api/v1";

    public const string DocumentName = "v1";

    public static IServiceCollection AddTahsilApi(this IServiceCollection services)
    {
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, configureOptions: null);
        services.AddAuthorization();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ApiAuthorizationResultHandler>();

        services.AddScoped<IdempotencyFilter>();
        services.AddApiRateLimiting();

        services.ConfigureHttpJsonOptions(options => TahsilJson.Configure(options.SerializerOptions));
        services.AddValidation();

        services.AddOpenApi(DocumentName, options =>
        {
            options.ShouldInclude = description => description.RelativePath?.StartsWith(BasePath[1..], StringComparison.Ordinal) == true;
            options.AddDocumentTransformer<TahsilApiDocumentTransformer>();
            options.AddOperationTransformer<PermissionOperationTransformer>();
            options.AddOperationTransformer<IdempotencyOperationTransformer>();
        });

        return services;
    }

    /// <summary>
    /// Keeps the body of API writes readable after binding, for the idempotency fingerprint. Must run before
    /// endpoints.
    /// </summary>
    public static IApplicationBuilder UseRequestBuffering(this IApplicationBuilder app) => app.Use((context, next) =>
    {
        if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments(BasePath))
        {
            context.Request.EnableBuffering();
        }

        return next(context);
    });

    public static IEndpointRouteBuilder MapTahsilApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGroup(BasePath)
            .AddEndpointFilter<ParameterBindingFilter>()
            .AddEndpointFilter<IdempotencyFilter>()
            .RequireRateLimiting(ApiRateLimiting.PolicyName)
            .MapApiKeyEndpoints();

        endpoints.MapOpenApi();
        endpoints.MapScalarApiReference(options => options.WithTitle("Tahsil API"));

        return endpoints;
    }
}
