using System.Collections.Frozen;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Domain.Payments;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Application.Connections;

/// <summary>The provider adapters of the installation, by key. Live mode does not offer the simulator.</summary>
public sealed class ProviderRegistry
{
    public const string Simulator = "simulator";

    private readonly FrozenDictionary<string, IPaymentProvider> _providers;

    public ProviderRegistry(IEnumerable<IPaymentProvider> providers, IOptions<ServerOptions> server)
    {
        Mode = server.Value.Mode;
        _providers = providers
            .Where(provider => Mode == TahsilMode.Test || provider.Descriptor.Key != Simulator)
            .ToFrozenDictionary(provider => provider.Descriptor.Key, StringComparer.Ordinal);
    }

    public TahsilMode Mode { get; }

    public IEnumerable<IPaymentProvider> All => _providers.Values.OrderBy(provider => provider.Descriptor.Key, StringComparer.Ordinal);

    public IPaymentProvider? Find(string key) => _providers.GetValueOrDefault(key);

    /// <summary>DOM-8: Test mode takes sandbox connections (the simulator counts as one), Live mode production connections only.</summary>
    public static bool AllowsInMode(TahsilMode mode, string provider, ConnectionEnvironment environment) => mode == TahsilMode.Live
        ? provider != Simulator && environment == ConnectionEnvironment.Production
        : environment == ConnectionEnvironment.Sandbox;
}
