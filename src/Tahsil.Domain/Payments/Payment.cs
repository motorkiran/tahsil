using Tahsil.Domain.Common;

namespace Tahsil.Domain.Payments;

public enum PaymentStatus
{
    Processing,
    RequiresAction,
    Succeeded,
    Failed,
    Canceled,
}

public enum PaymentInitiator
{
    /// <summary>The customer pays on a page (on session).</summary>
    Customer,

    /// <summary>Tahsil charges a saved card (off session).</summary>
    Merchant,

    /// <summary>An operator recorded money received outside Tahsil.</summary>
    Operator,
}

public enum PaymentMethodKind
{
    Card,
    BankTransfer,
    Cash,
    Other,
}

public enum PaymentPurpose
{
    Charge,

    /// <summary>Saves a card without collecting money; the amount is 0.</summary>
    Setup,
}

/// <summary>A failure as stored: the normalized code and category, a message safe for the customer, and the provider's own words.</summary>
public sealed record PaymentFailure(string Code, string Category, string Message, string? ProviderCode = null, string? ProviderMessage = null);

/// <summary>
/// One attempt to collect money for an invoice or checkout (DOM §9). It is written before the provider is called
/// (write-ahead), so no call is ever made without a record to reconcile; its reference is sent to the provider (PAY-7).
/// </summary>
public sealed class Payment : AggregateRoot
{
    public const int FailureMessageMaxLength = 500;

    private Payment()
    {
    }

    public Guid Id { get; private init; }

    /// <summary>The ID in 32 lowercase hexadecimal digits, unique across payments and refunds.</summary>
    public string Reference { get; private init; } = string.Empty;

    /// <summary>The invoice it pays; a checkout's payment gets the invoice the checkout creates when it completes.</summary>
    public Guid? InvoiceId { get; private set; }

    public Guid? CheckoutSessionId { get; private init; }

    public Guid CustomerId { get; private init; }

    public Guid? ConnectionId { get; private init; }

    public Guid? PaymentMethodId { get; private set; }

    public long Amount { get; private init; }

    public string Currency { get; private init; } = string.Empty;

    public PaymentPurpose Purpose { get; private init; }

    public PaymentStatus Status { get; private set; }

    public PaymentInitiator Initiator { get; private init; }

    public PaymentMethodKind Method { get; private init; }

    /// <summary>Whether a customer-present payment saves the card it is paid with.</summary>
    public bool SaveCard { get; private init; }

    /// <summary>The customer's consent to future charges, recorded when they chose to save the card (CUS-11).</summary>
    public Guid? ConsentRecordId { get; private init; }

    public string? ProviderPaymentId { get; private set; }

    /// <summary>Non-secret provider details, given back to the adapter on later calls.</summary>
    public IReadOnlyDictionary<string, string> ProviderData { get; private set; } = Metadata.Empty;

    public bool? ThreeDSecure { get; private set; }

    public string? FailureCode { get; private set; }

    public string? FailureCategory { get; private set; }

    public string? FailureMessage { get; private set; }

    public string? ProviderFailureCode { get; private set; }

    public string? ProviderFailureMessage { get; private set; }

    public long AmountRefunded { get; private set; }

    /// <summary>For customer-present payments: when the customer's attempt is abandoned.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    public DateTimeOffset? ReconcileAt { get; private set; }

    public int ReconcileAttempts { get; private set; }

    public Actor Actor { get; private init; } = Actor.System;

    public DateTimeOffset? SucceededAt { get; private set; }

    public DateTimeOffset? FailedAt { get; private set; }

    public DateTimeOffset? CanceledAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsInFlight => Status is PaymentStatus.Processing or PaymentStatus.RequiresAction;

    /// <summary>Written ahead of a customer-present payment (PAY-8): reconciliation looks at it when the customer's time is up.</summary>
    public static Payment ForCustomer(
        Guid customerId,
        Guid? invoiceId,
        Guid? checkoutSessionId,
        Guid connectionId,
        long amount,
        string currency,
        PaymentPurpose purpose,
        bool saveCard,
        Guid? consentRecordId,
        DateTimeOffset expiresAt,
        Actor actor,
        DateTimeOffset now)
    {
        var payment = New(
            customerId, invoiceId, checkoutSessionId, connectionId, null, amount, currency, purpose, PaymentInitiator.Customer, saveCard || purpose == PaymentPurpose.Setup, consentRecordId, actor, now);
        payment.ExpiresAt = expiresAt;
        payment.ReconcileAt = expiresAt;
        return payment;
    }

    /// <summary>Written ahead of a charge of a saved card (ENG-13), reconciled soon in case its answer never arrives.</summary>
    public static Payment ForMerchant(
        Guid customerId, Guid invoiceId, Guid connectionId, Guid paymentMethodId, long amount, string currency, DateTimeOffset reconcileAt, Actor actor, DateTimeOffset now)
    {
        var payment = New(customerId, invoiceId, null, connectionId, paymentMethodId, amount, currency, PaymentPurpose.Charge, PaymentInitiator.Merchant, false, null, actor, now);
        payment.ReconcileAt = reconcileAt;
        return payment;
    }

    /// <summary>
    /// An automatic charge that could not be made at all, such as without a usable card (ENG-13): recorded as failed
    /// before any provider call, so dunning sees the attempt.
    /// </summary>
    public static Payment FailedBeforeCharge(Guid customerId, Guid invoiceId, long amount, string currency, PaymentFailure failure, Actor actor, DateTimeOffset now)
    {
        var payment = New(customerId, invoiceId, null, null, null, amount, currency, PaymentPurpose.Charge, PaymentInitiator.Merchant, false, null, actor, now);
        payment.Fail(failure, null, null, now);
        return payment;
    }

    /// <summary>Claims the next reconciliation (ENG-15): one more attempt, and the time of the one after it.</summary>
    public int ClaimReconciliation(DateTimeOffset next, DateTimeOffset now)
    {
        ReconcileAttempts++;
        ReconcileAt = next;
        UpdatedAt = now;
        return ReconcileAttempts;
    }

    /// <summary>Automatic reconciliation gives up; an operator resolves the payment (ENG-16, ENG-17).</summary>
    public void StopReconciling(DateTimeOffset now)
    {
        ReconcileAt = null;
        UpdatedAt = now;
    }

    /// <summary>The provider needs the customer, such as for 3D Secure or a hosted page.</summary>
    public void RequireAction(IReadOnlyDictionary<string, string> providerData, DateTimeOffset now)
    {
        EnsureInFlight();
        Status = PaymentStatus.RequiresAction;
        ProviderData = providerData;
        UpdatedAt = now;
    }

    /// <summary>A verified success; from a final failure this is a late success (PAY-14).</summary>
    public void Succeed(string? providerPaymentId, IReadOnlyDictionary<string, string> providerData, bool? threeDSecure, DateTimeOffset now)
    {
        if (Status == PaymentStatus.Succeeded)
        {
            throw new InvalidOperationException($"The payment {Id} already succeeded.");
        }

        Status = PaymentStatus.Succeeded;
        ProviderPaymentId = providerPaymentId ?? ProviderPaymentId;
        ProviderData = providerData.Count == 0 ? ProviderData : providerData;
        ThreeDSecure = threeDSecure ?? ThreeDSecure;
        SucceededAt = now;
        ReconcileAt = null;
        ExpiresAt = null;
        UpdatedAt = now;
    }

    public void Fail(PaymentFailure failure, string? providerPaymentId, bool? threeDSecure, DateTimeOffset now)
    {
        EnsureInFlight();
        Status = PaymentStatus.Failed;
        FailureCode = failure.Code;
        FailureCategory = failure.Category;
        FailureMessage = Truncate(failure.Message, FailureMessageMaxLength);
        ProviderFailureCode = Truncate(failure.ProviderCode, 100);
        ProviderFailureMessage = Truncate(failure.ProviderMessage, FailureMessageMaxLength);
        ProviderPaymentId = providerPaymentId ?? ProviderPaymentId;
        ThreeDSecure = threeDSecure ?? ThreeDSecure;
        FailedAt = now;
        ReconcileAt = null;
        ExpiresAt = null;
        UpdatedAt = now;
    }

    /// <summary>The customer's time ran out and the provider has no success for the reference.</summary>
    public void Cancel(DateTimeOffset now)
    {
        if (Status != PaymentStatus.RequiresAction)
        {
            throw new InvalidOperationException($"Only a payment waiting for the customer can be canceled; {Id} is {Status}.");
        }

        Status = PaymentStatus.Canceled;
        CanceledAt = now;
        ReconcileAt = null;
        ExpiresAt = null;
        UpdatedAt = now;
    }

    /// <summary>A refund of the payment succeeded (DOM-18): the status stays, the amount refunded grows.</summary>
    public void AddRefunded(long amount, DateTimeOffset now)
    {
        AmountRefunded = checked(AmountRefunded + amount);
        UpdatedAt = now;
    }

    /// <summary>Links a checkout's payment to the invoice its completion created (CHK-23).</summary>
    public void AttachInvoice(Guid invoiceId, DateTimeOffset now)
    {
        if (InvoiceId is not null)
        {
            throw new InvalidOperationException("The payment already pays an invoice.");
        }

        InvoiceId = invoiceId;
        UpdatedAt = now;
    }

    public void AttachPaymentMethod(Guid paymentMethodId, DateTimeOffset now)
    {
        PaymentMethodId = paymentMethodId;
        UpdatedAt = now;
    }

    private static Payment New(
        Guid customerId,
        Guid? invoiceId,
        Guid? checkoutSessionId,
        Guid? connectionId,
        Guid? paymentMethodId,
        long amount,
        string currency,
        PaymentPurpose purpose,
        PaymentInitiator initiator,
        bool saveCard,
        Guid? consentRecordId,
        Actor actor,
        DateTimeOffset now)
    {
        if (invoiceId is null && checkoutSessionId is null)
        {
            throw new ArgumentException("A payment collects an invoice or a checkout (DOM-16).", nameof(invoiceId));
        }

        if (purpose == PaymentPurpose.Setup ? amount != 0 : amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "A charge is positive; a setup is 0.");
        }

        var id = Guid.CreateVersion7(now);
        return new Payment
        {
            Id = id,
            Reference = id.ToString("N"),
            CustomerId = customerId,
            InvoiceId = invoiceId,
            CheckoutSessionId = checkoutSessionId,
            ConnectionId = connectionId,
            PaymentMethodId = paymentMethodId,
            Amount = amount,
            Currency = currency,
            Purpose = purpose,
            Status = PaymentStatus.Processing,
            Initiator = initiator,
            Method = PaymentMethodKind.Card,
            SaveCard = saveCard,
            ConsentRecordId = consentRecordId,
            Actor = actor,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private void EnsureInFlight()
    {
        if (!IsInFlight)
        {
            throw new InvalidOperationException($"The payment {Id} is {Status}, not in flight.");
        }
    }

    private static string? Truncate(string? value, int length) => value is { } text && text.Length > length ? text[..length] : value;
}
