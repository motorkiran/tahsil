using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Connections;
using Tahsil.Application.Invoices;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Payments;
using Tahsil.Providers.Abstractions;
using PaymentStatus = Tahsil.Domain.Payments.PaymentStatus;
using ProviderRefundStatus = Tahsil.Providers.Abstractions.RefundStatus;
using RefundKind = Tahsil.Domain.Payments.RefundKind;
using RefundStatus = Tahsil.Domain.Payments.RefundStatus;

namespace Tahsil.Application.Payments;

public sealed class RefundOptions
{
    public const string SectionName = "Tahsil:Refunds";

    /// <summary>
    /// What one API key or administrator may refund per day in each currency, in minor units (SEC-13); no limit for
    /// currencies not listed.
    /// </summary>
    public Dictionary<string, long> DailyLimitPerActor { get; set; } = [];

    internal bool IsValid() => DailyLimitPerActor.Values.All(limit => limit > 0);
}

public static class RefundErrors
{
    public static readonly Error NotFound = Error.NotFound("refund.not_found", "The refund does not exist.");
    public static readonly Error PaymentNotRefundable = Error.Conflict("refund.payment_not_refundable", "Only succeeded payments with something left to refund can be refunded.");
    public static readonly Error AmountTooLarge = Error.Validation("refund.amount_too_large", "The refund is more than what is left of the payment.", "amount");
    public static readonly Error PartialNotSupported = Error.Validation("refund.partial_not_supported", "The payment's provider refunds only in full.", "amount");
    public static readonly Error NotAllowedYet = Error.Conflict(
        "refund.not_allowed_yet", "The provider refunds part of a payment only from the day after it; refund all of it, or try again tomorrow.");
    public static readonly Error LimitExceeded = Error.Forbidden("refund.limit_exceeded", "The refund is above what this key or administrator may refund today.");
}

/// <summary>
/// Refunds (PAY-18 to PAY-20): written ahead under the payment's lock, so refunds never exceed the payment (DOM-19),
/// sent to the provider outside any transaction, and settled by the provider's answer or by reconciliation.
/// </summary>
public sealed class Refunds(
    ITahsilDbContext context,
    IBillingLocks locks,
    IAuditLog audit,
    IIdempotency idempotency,
    IRequestContext request,
    ProviderRegistry registry,
    ProviderAccess providers,
    RefundOutcomes outcomes,
    IOptions<PaymentOptions> paymentOptions,
    IOptions<RefundOptions> refundOptions,
    IOptions<BillingOptions> billing,
    BillingClock clock)
{
    public async Task<Result<Refund>> CreateAsync(Guid paymentId, long? amount, RefundReason reason, string? note, CancellationToken cancellationToken)
    {
        var written = await WriteAheadAsync(paymentId, amount, reason, note, cancellationToken);
        if (written.IsFailure)
        {
            return written.Error;
        }

        var (refund, payment, connection) = written.Value;
        if (providers.For(connection) is not { } call)
        {
            return await outcomes.ApplyAsync(refund.Id, new RefundOutcome { Status = ProviderRefundStatus.Failed, FailureCode = "provider_unavailable" }, CancellationToken.None);
        }

        var outcome = await call.Provider.RefundAsync(call.Context, new RefundRequest
        {
            RefundId = refund.Id,
            Reference = refund.Reference,
            PaymentReference = payment.Reference,
            ProviderPaymentId = payment.ProviderPaymentId,
            PaymentAmount = PaymentRequests.AmountOf(payment.Amount, payment.Currency),
            PaymentSucceededAt = payment.SucceededAt!.Value,
            Amount = PaymentRequests.AmountOf(refund.Amount, refund.Currency),
            Reason = System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(refund.Reason.ToString()),
            PaymentProviderData = payment.ProviderData,
        }, CancellationToken.None);
        return await outcomes.ApplyAsync(refund.Id, outcome, CancellationToken.None);
    }

    private async Task<Result<(Refund Refund, Payment Payment, ProviderConnection Connection)>> WriteAheadAsync(
        Guid paymentId, long? amount, RefundReason reason, string? note, CancellationToken cancellationToken)
    {
        await using var transaction = await context.BeginTransactionAsync(cancellationToken);
        await locks.LockPaymentAsync(paymentId, cancellationToken);
        if (await context.Payments.AsNoTracking().SingleOrDefaultAsync(row => row.Id == paymentId, cancellationToken) is not { } payment)
        {
            return PaymentErrors.NotFound;
        }

        var refunded = await context.Refunds.Where(row => row.PaymentId == paymentId && row.Status != RefundStatus.Failed).SumAsync(row => row.Amount, cancellationToken);
        var left = payment.Amount - refunded;
        if (payment.Status != PaymentStatus.Succeeded || payment.ConnectionId is not { } connectionId || left <= 0)
        {
            return RefundErrors.PaymentNotRefundable;
        }

        var value = amount ?? left;
        if (value <= 0 || value > left)
        {
            return RefundErrors.AmountTooLarge;
        }

        var connection = await context.ProviderConnections.AsNoTracking().SingleAsync(row => row.Id == connectionId, cancellationToken);
        var now = clock.GetUtcNow();
        if (registry.Find(connection.Provider)?.CapabilitiesFor(connection.Settings) is { } capabilities && value < payment.Amount)
        {
            if (capabilities.Refunds == RefundSupport.Full)
            {
                return RefundErrors.PartialNotSupported;
            }

            if (capabilities.Refunds == RefundSupport.PartialAfterSettlementDay && SameDay(payment.SucceededAt!.Value, now))
            {
                // PAY-20: refused before any call; the provider would refuse it too.
                return RefundErrors.NotAllowedYet;
            }
        }

        var actor = request.Caller.ToActor();
        if (actor.Type != ActorType.System && refundOptions.Value.DailyLimitPerActor.TryGetValue(payment.Currency, out var limit))
        {
            var dayStart = DayStart(now);
            var today = await context.Refunds
                .Where(row => row.Actor == actor && row.Currency == payment.Currency && row.Status != RefundStatus.Failed && row.CreatedAt >= dayStart)
                .SumAsync(row => row.Amount, cancellationToken);
            if (today + value > limit)
            {
                return RefundErrors.LimitExceeded;
            }
        }

        var refund = Refund.Create(payment.Id, value, payment.Currency, reason, note, actor, now + paymentOptions.Value.ReconcileAfter, now);
        context.Refunds.Add(refund);
        idempotency.RecordCreated(ResourceTypes.Refund, refund.Id);
        audit.Record(AuditActions.RefundCreated, AuditSubject.Of(AuditSubjects.Refund, refund.Id), new Dictionary<string, object?>
        {
            ["paymentId"] = payment.Id,
            ["amount"] = refund.Amount,
            ["currency"] = refund.Currency,
            ["reason"] = System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(reason.ToString()),
        }, customerId: payment.CustomerId);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (refund, payment, connection);
    }

    private bool SameDay(DateTimeOffset left, DateTimeOffset right)
    {
        var zone = billing.Value.Zone;
        return TimeZoneInfo.ConvertTime(left, zone).Date == TimeZoneInfo.ConvertTime(right, zone).Date;
    }

    private DateTimeOffset DayStart(DateTimeOffset now)
    {
        var zone = billing.Value.Zone;
        var date = TimeZoneInfo.ConvertTime(now, zone).Date;
        return new DateTimeOffset(date, zone.GetUtcOffset(date));
    }
}

/// <summary>Applies a provider's answer about a refund, once, under the refund's and payment's locks (PAY-19).</summary>
public sealed class RefundOutcomes(ITahsilDbContext context, IBillingLocks locks, IAuditLog audit, Attention attention, BillingClock clock)
{
    public async Task<Refund> ApplyAsync(Guid refundId, RefundOutcome outcome, CancellationToken cancellationToken)
    {
        await using var transaction = await context.BeginTransactionAsync(cancellationToken);
        await locks.LockRefundAsync(refundId, cancellationToken);
        var refund = await context.Refunds.SingleAsync(row => row.Id == refundId, cancellationToken);
        await locks.LockPaymentAsync(refund.PaymentId, cancellationToken);
        var payment = await context.Payments.SingleAsync(row => row.Id == refund.PaymentId, cancellationToken);
        var now = clock.GetUtcNow();
        if (refund.Status == RefundStatus.Processing)
        {
            switch (outcome.Status)
            {
                case ProviderRefundStatus.Succeeded:
                    refund.Succeed(outcome.ProviderRefundId, outcome.Kind == Providers.Abstractions.RefundKind.Void ? RefundKind.Void : RefundKind.Refund, now);
                    payment.AddRefunded(refund.Amount, now);
                    if (payment.InvoiceId is { } invoiceId)
                    {
                        await locks.LockInvoiceAsync(invoiceId, cancellationToken);
                        (await context.Invoices.SingleAsync(row => row.Id == invoiceId, cancellationToken)).AddRefunded(refund.Amount, now);
                    }

                    audit.Record(AuditActions.RefundSucceeded, AuditSubject.Of(AuditSubjects.Refund, refund.Id), Details(refund, payment), customerId: payment.CustomerId);
                    break;
                case ProviderRefundStatus.Failed:
                    refund.Fail(outcome.FailureCode ?? "provider_error", outcome.FailureMessage, outcome.ProviderRefundId, now);
                    audit.Record(AuditActions.RefundFailed, AuditSubject.Of(AuditSubjects.Refund, refund.Id), Details(refund, payment), customerId: payment.CustomerId);
                    await attention.RaiseAsync(AttentionKind.RefundFailed, AuditSubjects.Refund, refund.Id, payment.CustomerId, new Dictionary<string, string>
                    {
                        ["paymentId"] = payment.Id.ToString(),
                        ["failureCode"] = refund.FailureCode!,
                    }, now, cancellationToken);
                    break;
                default:
                    // Pending or unknown: reconciliation asks the provider later (asynchronous providers settle this way).
                    refund.RecordProviderRefundId(outcome.ProviderRefundId, now);
                    break;
            }

            await context.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return refund;
    }

    private static Dictionary<string, object?> Details(Refund refund, Payment payment) => new()
    {
        ["paymentId"] = payment.Id,
        ["invoiceId"] = payment.InvoiceId,
        ["amount"] = refund.Amount,
        ["currency"] = refund.Currency,
        ["reason"] = System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(refund.Reason.ToString()),
        ["failureCode"] = refund.FailureCode,
    };
}
