using Tahsil.Domain.Payments;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Application.Connections;

/// <summary>Builds what an adapter may use for one call: its HTTP client, logger and the connection's values (ADP-12).</summary>
public interface IProviderContextFactory
{
    ProviderContext Create(ProviderConnection connection, IReadOnlyDictionary<string, string> credentials, IReadOnlyDictionary<string, string> settings);
}

/// <summary>An adapter and the context to call it with for one connection.</summary>
public sealed record ProviderCall(IPaymentProvider Provider, ProviderContext Context);

public sealed class ProviderAccess(ProviderRegistry registry, ConnectionSecrets secrets, IProviderContextFactory contexts)
{
    /// <summary>
    /// The connection's adapter, or <see langword="null"/> when the installation no longer has it or the connection
    /// does not fit the mode. Database preparation refuses to start with such connections (DOM-8); this keeps them
    /// from moving money where it did not run, such as when migrations are a separate deployment step.
    /// </summary>
    public ProviderCall? For(ProviderConnection connection)
    {
        if (!ProviderRegistry.AllowsInMode(registry.Mode, connection.Provider, connection.Environment) || registry.Find(connection.Provider) is not { } provider)
        {
            return null;
        }

        // Adapters see their settings' defaults as values.
        var settings = new Dictionary<string, string>(connection.Settings, StringComparer.Ordinal);
        foreach (var field in ConnectionFields.SettingsOf(provider.Descriptor))
        {
            if (field.Default is { } value)
            {
                settings.TryAdd(field.Key, value);
            }
        }

        return new ProviderCall(provider, contexts.Create(connection, secrets.Decrypt(connection), settings));
    }
}
