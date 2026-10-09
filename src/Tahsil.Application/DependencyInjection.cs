using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tahsil.Application.ApiKeys;
using Tahsil.Application.Auditing;
using Tahsil.Application.Branding;
using Tahsil.Application.Common;

namespace Tahsil.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<ServerOptions>()
            .BindConfiguration(ServerOptions.SectionName)
            .Validate(
                options => options.HasValidPublicUrl(),
                $"{ServerOptions.SectionName}:PublicUrl must be an absolute HTTP or HTTPS URL without a query, fragment or user name.")
            .Validate(
                options => options.UsesHttpsWhenRequired(),
                $"{ServerOptions.SectionName}:PublicUrl must use HTTPS unless {ServerOptions.SectionName}:RequireHttps is false.")
            .Validate(
                options => options.RequiresHttpsInLiveMode(),
                $"{ServerOptions.SectionName}:RequireHttps cannot be false in Live mode.")
            .Validate(
                options => options.HasSupportedLocale(),
                $"{ServerOptions.SectionName}:DefaultLocale must be one of: {string.Join(", ", ServerOptions.SupportedLocales)}.")
            .ValidateOnStart();

        services.AddOptions<BrandingOptions>()
            .BindConfiguration(BrandingOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(options => options.HasReadableStylesheet(), $"{BrandingOptions.SectionName}:StylesheetPath does not exist.")
            .ValidateOnStart();

        services.AddSingleton<IPublicUrls, PublicUrls>();
        services.AddScoped<IAuthorizer, Authorizer>();

        services.AddScoped<ApiKeyAuthenticator>();
        services.AddScoped<CreateApiKeyHandler>();
        services.AddScoped<ListApiKeysHandler>();
        services.AddScoped<GetApiKeyHandler>();
        services.AddScoped<RevokeApiKeyHandler>();
        services.AddScoped<ListAuditEventsHandler>();

        return services;
    }
}
