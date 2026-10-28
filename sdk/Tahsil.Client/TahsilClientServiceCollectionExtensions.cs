using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Tahsil.Client;

public static class TahsilClientDefaults
{
    /// <summary>The <see cref="IHttpClientFactory"/> name of the client that calls the Tahsil API.</summary>
    public const string HttpClientName = "Tahsil";
}

public static class TahsilClientServiceCollectionExtensions
{
    /// <summary>Registers <see cref="ITahsilClient"/>, which calls the Tahsil API with the configured key; the options are checked at startup.</summary>
    /// <returns>The builder of the API's HTTP client, to add resilience or other handlers.</returns>
    public static IHttpClientBuilder AddTahsilClient(this IServiceCollection services, Action<TahsilClientOptions> configure)
    {
        services.AddOptions<TahsilClientOptions>()
            .Configure(configure)
            .Validate(options => options.IsValid(), "Tahsil client: BaseUrl must be an absolute URL, ApiKey is required, and MaxRetries is 0 to 10.")
            .ValidateOnStart();

        return services.AddHttpClient<ITahsilClient, TahsilClient>(TahsilClientDefaults.HttpClientName, (provider, http) =>
        {
            var options = provider.GetRequiredService<IOptions<TahsilClientOptions>>().Value;
            http.BaseAddress = options.BaseUrl;
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        });
    }
}
