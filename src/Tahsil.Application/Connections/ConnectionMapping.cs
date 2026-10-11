using System.Text.Json;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Domain.Payments;
using Tahsil.Providers.Abstractions;
using Model = Tahsil.Domain.Payments;

namespace Tahsil.Application.Connections;

public sealed class ConnectionMapper(ConnectionSecrets secrets, ProviderRegistry registry, IPublicUrls urls)
{
    /// <summary>Shorter secrets show no last four characters: they would give away too much of the value.</summary>
    private const int Last4MinimumLength = 12;

    public ConnectionResponse ToResponse(ProviderConnection connection, IReadOnlyList<string> currencies)
    {
        var descriptor = registry.Find(connection.Provider)?.Descriptor;
        var credentials = secrets.Decrypt(connection);
        var credentialKeys = descriptor?.Credentials.Select(field => field.Key) ?? credentials.Keys;
        var settingFields = descriptor is null ? [] : ConnectionFields.SettingsOf(descriptor);

        return new ConnectionResponse(
            connection.Id,
            connection.Key,
            connection.Provider,
            connection.Name,
            connection.Environment.Map<Contracts.ConnectionEnvironment>(),
            connection.IsEnabled,
            connection.CheckoutsPausedAt is not null,
            credentialKeys.ToDictionary(
                key => key,
                key => credentials.TryGetValue(key, out var value)
                    ? new CredentialState(true, value.Length >= Last4MinimumLength ? value[^4..] : null)
                    : new CredentialState(false, null)),
            connection.Settings.ToDictionary(
                setting => setting.Key,
                setting => ConnectionFields.ToJson(settingFields.FirstOrDefault(field => field.Key == setting.Key), setting.Value)),
            currencies,
            NotificationUrl(connection.Id),
            connection.LastTestedAt is { } testedAt
                ? new ConnectionTestResponse(connection.LastTestStatus!.Value.Map<Contracts.ConnectionTestStatus>(), connection.LastTestMessage, testedAt)
                : null,
            connection.CreatedAt,
            connection.UpdatedAt);
    }

    /// <summary>Where the provider sends notifications for the connection (PAY-23); operators register it with the provider.</summary>
    public Uri NotificationUrl(Guid connectionId) => urls.Build($"provider-notifications/{connectionId}");

    public static ProviderResponse ToResponse(ProviderDescriptor descriptor) => new(
        descriptor.Key,
        descriptor.DisplayName,
        [.. descriptor.Environments.Order().Select(environment => environment.Map<Contracts.ConnectionEnvironment>())],
        [.. descriptor.Currencies.Order(StringComparer.Ordinal)],
        descriptor.MinimumAmounts,
        [.. descriptor.Credentials.Select(ToResponse)],
        [.. ConnectionFields.SettingsOf(descriptor).Select(ToResponse)],
        new ProviderCapabilitiesResponse(
            [.. descriptor.Capabilities.Entry.Order().Select(entry => Name(entry))],
            Name(descriptor.Capabilities.SavesCards),
            Name(descriptor.Capabilities.Setup),
            descriptor.Capabilities.ChargesSavedCards,
            Name(descriptor.Capabilities.Refunds),
            descriptor.Capabilities.AsynchronousRefunds,
            Name(descriptor.Capabilities.Voids),
            Name(descriptor.Capabilities.FindByReference),
            Name(descriptor.Capabilities.Notifications),
            descriptor.Capabilities.IdempotentCharges));

    internal static ProviderEnvironment ToProvider(Model.ConnectionEnvironment environment) => environment.Map<ProviderEnvironment>();

    private static ProviderFieldResponse ToResponse(FieldDefinition field) =>
        new(field.Key, field.Label, Name(field.Type), field.Required && field.Default is null, field.Description, field.Choices, field.Default);

    private static string Name(Enum value) => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
