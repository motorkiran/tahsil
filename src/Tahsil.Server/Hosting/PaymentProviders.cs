using Tahsil.Infrastructure.Providers;
using Tahsil.Providers.Abstractions;
using Tahsil.Providers.Iyzico;
using Tahsil.Providers.PayTR;
using Tahsil.Providers.Stripe;

namespace Tahsil.Server.Hosting;

/// <summary>The real payment providers' adapters, each with an HTTP client of its own (ADP-5, FND-39).</summary>
internal static class PaymentProviders
{
    public static IServiceCollection AddPaymentProviders(this IServiceCollection services) =>
        services.AddProvider<IyzicoProvider>(IyzicoProvider.Key).AddProvider<PayTRProvider>(PayTRProvider.Key).AddProvider<StripeProvider>(StripeProvider.Key);

    /// <summary>
    /// The adapter and its client: no redirects (a provider API never redirects a payment call), and no client-wide
    /// timeout, since each call has its operation's own (ProviderHttp).
    /// </summary>
    private static IServiceCollection AddProvider<TProvider>(this IServiceCollection services, string key)
        where TProvider : class, IPaymentProvider
    {
        services.AddSingleton<IPaymentProvider, TProvider>();
        services.AddHttpClient(ProviderContextFactory.ClientName(key))
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
        return services;
    }
}
