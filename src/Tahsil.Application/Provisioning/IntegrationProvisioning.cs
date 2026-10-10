using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Application.Connections;
using Tahsil.Application.Webhooks;
using Tahsil.Contracts;
using Tahsil.Domain.Common;

namespace Tahsil.Application.Provisioning;

/// <summary>
/// Applies the wiring sections of a provisioning document: provider connections, the currencies routed to them, and
/// webhook endpoints. Like the catalog, each item goes through the API's handlers, with the caller's permissions.
/// </summary>
public sealed class IntegrationProvisioning(
    ITahsilDbContext context,
    ProviderRegistry registry,
    ConnectionSecrets connectionSecrets,
    ISecretEncryption encryption,
    CreateConnectionHandler createConnection,
    UpdateConnectionHandler updateConnection,
    UpdateRoutingHandler updateRouting,
    CreateWebhookEndpointHandler createEndpoint,
    UpdateWebhookEndpointHandler updateEndpoint,
    RotateWebhookSecretHandler rotateSecret)
{
    public async Task<Result<ProvisioningItemResult>> ApplyConnectionAsync(ProvisionedConnection declared, CancellationToken cancellationToken)
    {
        var existing = await context.ProviderConnections.AsNoTracking().SingleOrDefaultAsync(connection => connection.Key == declared.Key, cancellationToken);
        if (existing is null)
        {
            var created = await createConnection.HandleAsync(
                new CreateConnectionRequest
                {
                    Key = declared.Key,
                    Provider = declared.Provider,
                    Name = declared.Name,
                    Environment = declared.Environment,
                    Credentials = declared.Credentials,
                    Settings = declared.Settings,
                    Enabled = declared.Enabled,
                },
                cancellationToken);
            return created.IsFailure ? created.Error : ProvisioningItemResult.Created;
        }

        if (existing.Provider != declared.Provider)
        {
            return ProvisioningErrors.FixedProperty("provider");
        }

        if (existing.Environment.Map<ConnectionEnvironment>() != declared.Environment)
        {
            return ProvisioningErrors.FixedProperty("environment");
        }

        if (registry.Find(existing.Provider) is not { } provider)
        {
            return Domain.Payments.ConnectionErrors.UnknownProvider;
        }

        var settings = ConnectionFields.Settings(provider.Descriptor, existing.Settings, declared.Settings);
        if (settings.IsFailure)
        {
            return settings.Error;
        }

        var current = connectionSecrets.Decrypt(existing);
        var credentialsChange = (declared.Credentials ?? new Dictionary<string, string>())
            .Any(field => string.IsNullOrWhiteSpace(field.Value) ? current.ContainsKey(field.Key) : current.GetValueOrDefault(field.Key) != field.Value.Trim());
        var settingsChange = !Same(existing.Settings, settings.Value);
        if (existing.Name == declared.Name.Trim() && existing.IsEnabled == declared.Enabled && !credentialsChange && !settingsChange)
        {
            return ProvisioningItemResult.Unchanged;
        }

        var updated = await updateConnection.HandleAsync(
            declared.Key,
            new UpdateConnectionRequest
            {
                Name = declared.Name,
                Credentials = credentialsChange ? declared.Credentials : null,
                Settings = settingsChange ? declared.Settings : null,
                Enabled = declared.Enabled,
            },
            null,
            cancellationToken);
        return updated.IsFailure ? updated.Error : ProvisioningItemResult.Updated;
    }

    /// <summary>
    /// Routes the declared connections' currencies to them; currencies no connection declares keep their routing.
    /// Returns the keys of the connections whose currencies moved.
    /// </summary>
    public async Task<Result<IReadOnlySet<string>>> ApplyRoutingAsync(IReadOnlyList<ProvisionedConnection> declared, CancellationToken cancellationToken)
    {
        var current = await context.ReadRoutingAsync(cancellationToken);
        var routing = new Dictionary<string, string>(current, StringComparer.Ordinal);
        var moved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var connection in declared)
        {
            foreach (var currency in (connection.Currencies ?? []).Select(currency => currency.Trim().ToUpperInvariant()))
            {
                if (current.GetValueOrDefault(currency) != connection.Key)
                {
                    moved.Add(connection.Key);
                }

                routing[currency] = connection.Key;
            }
        }

        if (moved.Count == 0)
        {
            return moved;
        }

        var updated = await updateRouting.HandleAsync(routing, cancellationToken);
        return updated.IsFailure ? updated.Error : moved;
    }

    public async Task<Result<ProvisioningItemResult>> ApplyEndpointAsync(ProvisionedWebhookEndpoint declared, CancellationToken cancellationToken)
    {
        var existing = await context.WebhookEndpoints.AsNoTracking().SingleOrDefaultAsync(endpoint => endpoint.Key == declared.Key, cancellationToken);
        if (existing is null)
        {
            var created = await createEndpoint.HandleAsync(
                new CreateWebhookEndpointRequest
                {
                    Key = declared.Key,
                    Url = declared.Url,
                    Description = declared.Description,
                    EventTypes = declared.EventTypes,
                    Secret = declared.Secret,
                    Enabled = declared.Enabled,
                },
                cancellationToken);
            return created.IsFailure ? created.Error : ProvisioningItemResult.Created;
        }

        var result = ProvisioningItemResult.Unchanged;
        var eventTypes = (declared.EventTypes ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        if (existing.Url != declared.Url
            || existing.Description != Declared.Text(declared.Description)
            || !existing.EventTypes.SequenceEqual(eventTypes)
            || existing.Enabled != declared.Enabled)
        {
            var updated = await updateEndpoint.HandleAsync(
                existing.Id,
                new UpdateWebhookEndpointRequest { Url = declared.Url, Description = declared.Description, EventTypes = declared.EventTypes ?? [], Enabled = declared.Enabled },
                null,
                cancellationToken);
            if (updated.IsFailure)
            {
                return updated.Error;
            }

            result = ProvisioningItemResult.Updated;
        }

        if (declared.Secret is { } secret && encryption.DecryptString(existing.Secret, SecretPurposes.WebhookEndpointSecret, existing.Id) != secret)
        {
            var rotated = await rotateSecret.HandleAsync(existing.Id, secret, cancellationToken);
            if (rotated.IsFailure)
            {
                return rotated.Error;
            }

            result = ProvisioningItemResult.Updated;
        }

        return result;
    }

    private static bool Same(IReadOnlyDictionary<string, string> stored, IReadOnlyDictionary<string, string> declared) =>
        stored.Count == declared.Count && stored.All(pair => declared.TryGetValue(pair.Key, out var value) && value == pair.Value);
}
