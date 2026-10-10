using Microsoft.Extensions.Logging;
using Tahsil.Application.Connections;
using Tahsil.Domain.Payments;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Infrastructure.Providers;

/// <summary>
/// Gives each adapter its own named HTTP client, without retrying handlers (ADP-5), and a logger under its own
/// category, so operators can turn on Debug logging for one provider.
/// </summary>
internal sealed class ProviderContextFactory(IHttpClientFactory clients, ILoggerFactory loggers, TimeProvider time, ConnectionMapper mapper)
    : IProviderContextFactory
{
    public static string ClientName(string provider) => $"Tahsil.Providers.{provider}";

    public ProviderContext Create(ProviderConnection connection, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings) => new()
    {
        ConnectionId = connection.Id,
        Environment = connection.Environment == ConnectionEnvironment.Production ? ProviderEnvironment.Production : ProviderEnvironment.Sandbox,
        Credentials = credentials,
        Settings = settings,
        Http = clients.CreateClient(ClientName(connection.Provider)),
        Time = time,
        Logger = loggers.CreateLogger(ClientName(connection.Provider)),
        NotificationUrl = mapper.NotificationUrl(connection.Id),
    };
}
