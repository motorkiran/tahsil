using System.Collections.Concurrent;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Server.Tests.Payments;

/// <summary>A provider whose answers the test scripts: charges answer as told, and lookups answer from a queue.</summary>
internal sealed class ScriptedProvider : IPaymentProvider
{
    public const string Key = "scripted";

    public ConcurrentQueue<PaymentLookup> Lookups { get; } = new();

    public ConcurrentQueue<PaymentQuery> Queries { get; } = new();

    public PaymentOutcome ChargeOutcome { get; set; } = PaymentOutcome.Unknown("Timed out.");

    public ReferenceSearch FindByReference { get; set; } = ReferenceSearch.Any;

    public RefundOutcome RefundOutcome { get; set; } = new() { Status = RefundStatus.Succeeded, ProviderRefundId = "scripted-refund" };

    public ProviderDescriptor Descriptor => new()
    {
        Key = Key,
        DisplayName = "Scripted",
        Credentials = [],
        Settings = [],
        Currencies = new HashSet<string> { "TRY" },
        MinimumAmounts = new Dictionary<string, long>(),
        Capabilities = new ProviderCapabilities
        {
            Entry = new HashSet<CardEntry> { CardEntry.Redirect },
            SavesCards = CardSaving.DuringPayment,
            Setup = CardSetup.Native,
            ChargesSavedCards = true,
            Refunds = RefundSupport.Partial,
            Voids = VoidSupport.None,
            FindByReference = FindByReference,
            Notifications = NotificationSupport.HintsOnly,
        },
        CustomerData = CustomerDataRequirements.None,
        ContentSecurity = new Dictionary<ProviderEnvironment, ContentSecuritySources>(),
    };

    public Task<ConnectionTestResult> TestConnectionAsync(ProviderContext context, CancellationToken cancellationToken) => Task.FromResult(ConnectionTestResult.Success);

    public Task<StartResult> StartAsync(ProviderContext context, StartRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new StartResult { Presentation = new Presentation.Redirect(new Uri($"https://scripted.test/pay/{request.Reference}")) });

    public Task<PaymentOutcome> CompleteAsync(ProviderContext context, CompleteRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(PaymentOutcome.Unknown());

    public Task<PaymentOutcome> ChargeSavedCardAsync(ProviderContext context, ChargeRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(ChargeOutcome);

    public Task<PaymentLookup> FindPaymentAsync(ProviderContext context, PaymentQuery query, CancellationToken cancellationToken)
    {
        Queries.Enqueue(query);
        return Task.FromResult(Lookups.TryDequeue(out var lookup) ? lookup : new PaymentLookup.Unavailable("Nothing scripted."));
    }

    public Task<RefundOutcome> RefundAsync(ProviderContext context, RefundRequest request, CancellationToken cancellationToken) => Task.FromResult(RefundOutcome);

    public Task<RefundLookup> FindRefundAsync(ProviderContext context, RefundQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<RemoveCardResult> RemoveSavedCardAsync(ProviderContext context, SavedCardReference card, CancellationToken cancellationToken) =>
        Task.FromResult(new RemoveCardResult(true));

    public Task<NotificationResult> HandleNotificationAsync(ProviderContext context, IncomingNotification notification, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
