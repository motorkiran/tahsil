namespace Tahsil.Providers.Abstractions;

/// <summary>
/// One payment provider's API behind Tahsil's few payment operations. Adapters know nothing about subscriptions,
/// invoices or dunning: they turn requests into provider calls and provider answers into outcomes. Expected failures
/// are outcomes; adapters throw only for bugs (ADP-3), and say <see cref="PaymentStatus.Unknown"/> when a call that may
/// have changed state has no clear answer (ADP-4).
/// </summary>
public interface IPaymentProvider
{
    ProviderDescriptor Descriptor { get; }

    /// <summary>
    /// What a connection with these settings can do: the descriptor's capabilities, unless the adapter lets settings
    /// restrict them (as the simulator does, to mimic a real provider). The engine decides flows from this.
    /// </summary>
    ProviderCapabilities CapabilitiesFor(IReadOnlyDictionary<string, string> settings) => Descriptor.Capabilities;

    Task<ConnectionTestResult> TestConnectionAsync(ProviderContext context, CancellationToken cancellationToken);

    /// <summary>Begins a payment (or card setup) with the customer present.</summary>
    Task<StartResult> StartAsync(ProviderContext context, StartRequest request, CancellationToken cancellationToken);

    /// <summary>Learns, from the provider, how a customer-present payment ended; for some providers this call creates the transaction.</summary>
    Task<PaymentOutcome> CompleteAsync(ProviderContext context, CompleteRequest request, CancellationToken cancellationToken);

    /// <summary>Charges a saved card without the customer (merchant-initiated, without 3D Secure).</summary>
    Task<PaymentOutcome> ChargeSavedCardAsync(ProviderContext context, ChargeRequest request, CancellationToken cancellationToken);

    Task<PaymentLookup> FindPaymentAsync(ProviderContext context, PaymentQuery query, CancellationToken cancellationToken);

    Task<RefundOutcome> RefundAsync(ProviderContext context, RefundRequest request, CancellationToken cancellationToken);

    Task<RefundLookup> FindRefundAsync(ProviderContext context, RefundQuery query, CancellationToken cancellationToken);

    Task<RemoveCardResult> RemoveSavedCardAsync(ProviderContext context, SavedCardReference card, CancellationToken cancellationToken);

    /// <summary>Verifies an incoming provider notification and says what it means and how to answer it.</summary>
    Task<NotificationResult> HandleNotificationAsync(ProviderContext context, IncomingNotification notification, CancellationToken cancellationToken);
}
