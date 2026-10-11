using Tahsil.Application.Common;
using Tahsil.Domain.Payments;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Application.Connections;

/// <summary>What a hosted page adds to its content security policy for the provider fields of a currency (FND-42).</summary>
public sealed class ProviderSources(ITahsilDbContext context, ProviderRegistry registry)
{
    public async Task<ContentSecuritySources> ForCurrencyAsync(string currency, CancellationToken cancellationToken) =>
        await context.RoutedConnectionAsync(currency, cancellationToken) is { } connection ? Of(connection) : ContentSecuritySources.None;

    public ContentSecuritySources Of(ProviderConnection connection)
    {
        var environment = connection.Environment == ConnectionEnvironment.Production ? ProviderEnvironment.Production : ProviderEnvironment.Sandbox;
        return registry.Find(connection.Provider)?.Descriptor.ContentSecurity.GetValueOrDefault(environment) ?? ContentSecuritySources.None;
    }
}
