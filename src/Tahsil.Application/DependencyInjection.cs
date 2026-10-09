using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tahsil.Application.ApiKeys;
using Tahsil.Application.Auditing;
using Tahsil.Application.Branding;
using Tahsil.Application.Catalog;
using Tahsil.Application.Common;
using Tahsil.Application.Customers;
using Tahsil.Application.Provisioning;

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

        services.AddOptions<ProvisioningOptions>()
            .BindConfiguration(ProvisioningOptions.SectionName)
            .Validate(options => options.IsValid(), $"{ProvisioningOptions.SectionName}:FilePath does not exist.")
            .ValidateOnStart();
        services.AddScoped<ApplyProvisioningHandler>();
        services.AddScoped<ExportProvisioningHandler>();
        services.AddScoped<StartupProvisioning>();

        services.AddScoped<ApiKeyAuthenticator>();
        services.AddScoped<CreateApiKeyHandler>();
        services.AddScoped<ListApiKeysHandler>();
        services.AddScoped<GetApiKeyHandler>();
        services.AddScoped<RevokeApiKeyHandler>();
        services.AddScoped<ListAuditEventsHandler>();
        services.AddScoped<CreateTaxRateHandler>();
        services.AddScoped<UpdateTaxRateHandler>();
        services.AddScoped<GetTaxRateHandler>();
        services.AddScoped<ListTaxRatesHandler>();
        services.AddScoped<CreateMeterHandler>();
        services.AddScoped<UpdateMeterHandler>();
        services.AddScoped<GetMeterHandler>();
        services.AddScoped<ListMetersHandler>();
        services.AddScoped<CreateProductHandler>();
        services.AddScoped<UpdateProductHandler>();
        services.AddScoped<DeleteProductHandler>();
        services.AddScoped<GetProductHandler>();
        services.AddScoped<ListProductsHandler>();
        services.AddScoped<CreatePriceHandler>();
        services.AddScoped<UpdatePriceHandler>();
        services.AddScoped<GetPriceHandler>();
        services.AddScoped<ListPricesHandler>();
        services.AddScoped<CustomerMapper>();
        services.AddScoped<CreateCustomerHandler>();
        services.AddScoped<UpsertCustomerHandler>();
        services.AddScoped<UpdateCustomerHandler>();
        services.AddScoped<GetCustomerHandler>();
        services.AddScoped<ListCustomersHandler>();
        services.AddScoped<DeleteCustomerHandler>();
        services.AddScoped<ExportCustomerHandler>();

        return services;
    }
}
