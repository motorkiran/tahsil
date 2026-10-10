using System.Collections.Concurrent;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Server.Tests.Connections;

/// <summary>An adapter for connection tests: its descriptor is configurable, and only its connection test does anything.</summary>
internal sealed class FakeProvider(string key, params string[] currencies) : IPaymentProvider
{
    public const string BadApiKey = "sk_test_bad_0000000000";

    public ConcurrentQueue<ProviderContext> Contexts { get; } = new();

    public ProviderDescriptor Descriptor { get; init; } = new()
    {
        Key = key,
        DisplayName = key,
        Credentials =
        [
            new FieldDefinition { Key = "apiKey", Label = "API key", Type = FieldType.Secret, Pattern = "^sk_" },
            new FieldDefinition { Key = "secretKey", Label = "Secret key", Type = FieldType.Secret },
            new FieldDefinition { Key = "webhookSecret", Label = "Webhook secret", Type = FieldType.Secret, Required = false },
        ],
        Settings =
        [
            new FieldDefinition { Key = "region", Label = "Region", Type = FieldType.Choice, Choices = ["tr", "eu"], Default = "tr" },
            new FieldDefinition { Key = "timeoutSeconds", Label = "Timeout", Type = FieldType.Number, Required = false },
        ],
        Currencies = new HashSet<string>(currencies),
        MinimumAmounts = new Dictionary<string, long> { ["TRY"] = 100 },
        Capabilities = new ProviderCapabilities
        {
            Entry = new HashSet<CardEntry> { CardEntry.Redirect, CardEntry.DirectForm },
            SavesCards = CardSaving.DuringPayment,
            Setup = CardSetup.VerificationCharge,
            ChargesSavedCards = true,
            Refunds = RefundSupport.Partial,
            Voids = VoidSupport.SameDayFullCancel,
            FindByReference = ReferenceSearch.Any,
            Notifications = NotificationSupport.Verifiable,
        },
        CustomerData = CustomerDataRequirements.None,
        ContentSecurity = new Dictionary<ProviderEnvironment, ContentSecuritySources>(),
    };

    /// <summary>A simulator stand-in: no credentials or settings, every currency of the simulator connection.</summary>
    public static FakeProvider Simulator() => new("simulator")
    {
        Descriptor = new FakeProvider("simulator").Descriptor with
        {
            Credentials = [],
            Settings = [],
            Currencies = new HashSet<string> { "TRY", "USD", "EUR", "GBP" },
            Environments = new HashSet<ProviderEnvironment> { ProviderEnvironment.Sandbox },
        },
    };

    public Task<ConnectionTestResult> TestConnectionAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        Contexts.Enqueue(context);
        return Task.FromResult(context.Credentials.GetValueOrDefault("apiKey") switch
        {
            BadApiKey => new ConnectionTestResult(false, "invalid_credentials", "The provider rejected the API key."),
            "sk_test_throws_000000000" => throw new InvalidOperationException("A bug in the adapter."),
            _ => ConnectionTestResult.Success,
        });
    }

    public Task<StartResult> StartAsync(ProviderContext context, StartRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<PaymentOutcome> CompleteAsync(ProviderContext context, CompleteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<PaymentOutcome> ChargeSavedCardAsync(ProviderContext context, ChargeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<PaymentLookup> FindPaymentAsync(ProviderContext context, PaymentQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<RefundOutcome> RefundAsync(ProviderContext context, RefundRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<RefundLookup> FindRefundAsync(ProviderContext context, RefundQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<RemoveCardResult> RemoveSavedCardAsync(ProviderContext context, SavedCardReference card, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<NotificationResult> HandleNotificationAsync(ProviderContext context, IncomingNotification notification, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
