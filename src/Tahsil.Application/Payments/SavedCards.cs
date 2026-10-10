using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Payments;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Application.Payments;

/// <summary>
/// Turns the cards providers save into payment methods (CUS-10 to CUS-13): one record per token and connection,
/// the customer's first card as the default, and the provider's customer reference kept for later payments.
/// </summary>
public sealed partial class SavedCards(
    ITahsilDbContext context, ISecretEncryption encryption, IAuditLog audit, IOptions<BillingOptions> billing, ILogger<SavedCards> logger)
{
    private static readonly JsonSerializerOptions TokenJson = new(JsonSerializerDefaults.Web);

    public async Task<PaymentMethod?> SaveAsync(Payment payment, SavedCard card, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (payment.ConsentRecordId is not { } consent || payment.ConnectionId is not { } connectionId)
        {
            // A card without the customer's consent to future charges is never kept (CUS-11).
            LogSavedWithoutConsent(logger, payment.Id);
            return null;
        }

        return await KeepAsync(payment.CustomerId, connectionId, card, consent, now, cancellationToken);
    }

    /// <summary>Keeps a card the provider saved, with the consent the customer gave for it: one record per token and connection.</summary>
    public async Task<PaymentMethod?> KeepAsync(Guid customerId, Guid connectionId, SavedCard card, Guid consent, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var token = Canonical(card.ProviderCustomer, card.Token);
        var fingerprint = SHA256.HashData(token);
        var details = CardDetails.Normalize(card.Brand, card.Funding, card.Bin, card.Last4, card.ExpMonth, card.ExpYear, card.Issuer, card.CardFamily, card.Country);
        var expiresAt = ExpiresAt(details);
        var method = await context.PaymentMethods.SingleOrDefaultAsync(row => row.ConnectionId == connectionId && row.TokenFingerprint == fingerprint, cancellationToken);
        if (method is not null && method.CustomerId != customerId)
        {
            // CUS-12: the provider gave one customer's token for another; something is mixed up at the provider.
            LogTokenConflict(logger, customerId, method.Id);
            return null;
        }

        var attached = method is null || !method.IsChargeable;
        if (method is null)
        {
            method = PaymentMethod.Create(
                customerId, connectionId, details, expiresAt, id => encryption.Encrypt(token, SecretPurposes.PaymentMethodToken, id), fingerprint, consent, now);
            context.PaymentMethods.Add(method);
        }
        else
        {
            method.Refresh(details, expiresAt, consent, now);
        }

        if (attached)
        {
            audit.Record(AuditActions.PaymentMethodAttached, AuditSubject.Of(AuditSubjects.PaymentMethod, method.Id), new Dictionary<string, object?>
            {
                ["brand"] = method.Brand,
                ["last4"] = method.Last4,
                ["expMonth"] = method.ExpMonth,
                ["expYear"] = method.ExpYear,
                ["connectionId"] = connectionId,
            }, customerId: customerId);
        }

        var customer = await context.Customers.SingleAsync(row => row.Id == customerId, cancellationToken);
        if (customer.DefaultPaymentMethodId is null)
        {
            customer.SetDefaultPaymentMethod(method.Id, now);
        }

        if (card.ProviderCustomer is { } reference)
        {
            await RememberProviderCustomerAsync(customerId, connectionId, reference, now, cancellationToken);
        }

        return method;
    }

    /// <summary>The card as the adapter knows it, for charging it.</summary>
    public SavedCardReference ReferenceOf(PaymentMethod method)
    {
        var token = JsonSerializer.Deserialize<StoredToken>(encryption.Decrypt(method.Token, SecretPurposes.PaymentMethodToken, method.Id), TokenJson)!;
        return new SavedCardReference(token.ProviderCustomer, token.Token);
    }

    /// <summary>Keeps the provider's customer for the customer and connection, as the adapter returned it.</summary>
    public async Task RememberProviderCustomerAsync(Guid customerId, Guid connectionId, string reference, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!await context.ProviderCustomers.AnyAsync(row => row.CustomerId == customerId && row.ConnectionId == connectionId, cancellationToken)
            && !context.ProviderCustomers.Local.Any(row => row.CustomerId == customerId && row.ConnectionId == connectionId))
        {
            context.ProviderCustomers.Add(ProviderCustomer.Create(customerId, connectionId, reference, now));
        }
    }

    /// <summary>The end of the expiry month in the billing time zone; cards without an expiry never expire on their own.</summary>
    private DateTimeOffset? ExpiresAt(CardDetails card)
    {
        if (card is not { ExpMonth: { } month, ExpYear: { } year })
        {
            return null;
        }

        var zone = billing.Value.Zone;
        var nextMonth = new DateTime(year, month, 1).AddMonths(1);
        return new DateTimeOffset(nextMonth, zone.GetUtcOffset(nextMonth)).AddTicks(-TimeSpan.TicksPerMicrosecond);
    }

    private static byte[] Canonical(string? providerCustomer, string token) => JsonSerializer.SerializeToUtf8Bytes(new StoredToken(providerCustomer, token), TokenJson);

    [LoggerMessage(LogLevel.Warning, "Payment {PaymentId} saved a card without the customer's consent to future charges; it is not kept")]
    private static partial void LogSavedWithoutConsent(ILogger logger, Guid paymentId);

    [LoggerMessage(LogLevel.Error, "A provider returned the token of payment method {PaymentMethodId} for customer {CustomerId}, but it belongs to another customer; the card is not saved")]
    private static partial void LogTokenConflict(ILogger logger, Guid customerId, Guid paymentMethodId);

    private sealed record StoredToken(string? ProviderCustomer, string Token);
}
