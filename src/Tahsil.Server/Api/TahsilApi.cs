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
        services.ConfigureHttpJsonOptions(options => TahsilJson.Configure(options.SerializerOptions));
        services.AddValidation();

        services.AddOpenApi(DocumentName, options =>
        {
            options.ShouldInclude = description => description.RelativePath?.StartsWith(BasePath[1..], StringComparison.Ordinal) == true;
            options.AddDocumentTransformer<TahsilApiDocumentTransformer>();
        });

        return services;
    }

    public static IEndpointRouteBuilder MapTahsilApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGroup(BasePath)
            .AddEndpointFilter<ParameterBindingFilter>();

        endpoints.MapOpenApi();
        endpoints.MapScalarApiReference(options => options.WithTitle("Tahsil API"));

        return endpoints;
    }
}
