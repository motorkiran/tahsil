using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Connections;
using Tahsil.Application.Invoices;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Payments;
using Tahsil.Providers.Abstractions;
using ProviderRefundStatus = Tahsil.Providers.Abstractions.RefundStatus;
using RefundStatus = Tahsil.Domain.Payments.RefundStatus;

namespace Tahsil.Application.Payments;

/// <summary>
/// Settles refunds still processing by asking the provider about their reference, on the payments' backoff schedule
/// (PAY-19, ENG-15): asynchronous refunds succeed or fail this way.
/// </summary>
public sealed class RefundReconciliation(
    ITahsilDbContext context,
    IBillingLocks locks,
    ProviderRegistry registry,
    ProviderAccess providers,
    RefundOutcomes outcomes,
    Attention attention,
    IOptions<PaymentOptions> options,
    BillingClock clock)
{
    private static readonly TimeSpan AttentionAfter = TimeSpan.FromDays(7);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(30);

    public async Task ReconcileAsync(Guid refundId, CancellationToken cancellationToken)
    {
        if (await ClaimAsync(refundId, cancellationToken) is not var (refund, payment, connection))
        {
            return;
        }

        var now = clock.GetUtcNow();
        var call = providers.For(connection);
        if (call is null || registry.Find(connection.Provider)?.CapabilitiesFor(connection.Settings).FindByReference == ReferenceSearch.None)
        {
            await RaiseAsync(refund.Id, AttentionKind.ReconciliationImpossible, stop: true, cancellationToken);
            return;
        }

        var lookup = await call.Provider.FindRefundAsync(
            call.Context, new RefundQuery(refund.Reference, payment.Reference, refund.ProviderRefundId, payment.ProviderPaymentId, refund.CreatedAt), CancellationToken.None);
        switch (lookup)
        {
            case RefundLookup.Found { Outcome.Status: ProviderRefundStatus.Succeeded or ProviderRefundStatus.Failed } found:
                await outcomes.ApplyAsync(refund.Id, found.Outcome, CancellationToken.None);
                return;
            case RefundLookup.NotFound when now - refund.CreatedAt >= options.Value.NotFoundAfter:
                await outcomes.ApplyAsync(refund.Id, new RefundOutcome { Status = ProviderRefundStatus.Failed, FailureCode = "not_submitted" }, CancellationToken.None);
                return;
        }

        if (now - refund.CreatedAt >= AttentionAfter)
        {
            await RaiseAsync(refund.Id, AttentionKind.ReconciliationOverdue, stop: now - refund.CreatedAt >= GiveUpAfter, cancellationToken);
        }
    }

    private async Task<(Refund Refund, Payment Payment, ProviderConnection Connection)?> ClaimAsync(Guid refundId, CancellationToken cancellationToken)
    {
        await using var transaction = await context.BeginTransactionAsync(cancellationToken);
        await locks.LockRefundAsync(refundId, cancellationToken);
        var refund = await context.Refunds.SingleOrDefaultAsync(row => row.Id == refundId, cancellationToken);
        if (refund is not { Status: RefundStatus.Processing })
        {
            return null;
        }

        var payment = await context.Payments.AsNoTracking().SingleAsync(row => row.Id == refund.PaymentId, cancellationToken);
        var connection = await context.ProviderConnections.AsNoTracking().SingleAsync(row => row.Id == payment.ConnectionId, cancellationToken);
        var now = clock.GetUtcNow();
        refund.ClaimReconciliation(now + PaymentReconciliation.WaitBefore(refund.ReconcileAttempts + 2), now);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (refund, payment, connection);
    }

    private async Task RaiseAsync(Guid refundId, AttentionKind kind, bool stop, CancellationToken cancellationToken)
    {
        await using var transaction = await context.BeginTransactionAsync(cancellationToken);
        await locks.LockRefundAsync(refundId, cancellationToken);
        var refund = await context.Refunds.SingleAsync(row => row.Id == refundId, cancellationToken);
        if (refund.Status != RefundStatus.Processing)
        {
            return;
        }

        var payment = await context.Payments.AsNoTracking().SingleAsync(row => row.Id == refund.PaymentId, cancellationToken);
        var now = clock.GetUtcNow();
        await attention.RaiseAsync(kind, AuditSubjects.Refund, refund.Id, payment.CustomerId, new Dictionary<string, string> { ["reference"] = refund.Reference }, now, cancellationToken);
        if (stop)
        {
            refund.StopReconciling(now);
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

/// <summary>A payment succeeded after nothing needed its money (PAY-14); it is refunded in full after the outcome is recorded.</summary>
[OutboxMessage("payments.refund_late_success")]
public sealed record RefundLateSuccess(Guid PaymentId);

public sealed partial class LateSuccessRefunder(ITahsilDbContext context, Refunds refunds, ILogger<LateSuccessRefunder> logger)
    : IOutboxMessageHandler<RefundLateSuccess>
{
    public async Task HandleAsync(RefundLateSuccess message, CancellationToken cancellationToken)
    {
        // Delivery is at least once: a late-success refund that exists, or a payment with nothing left, ends here.
        if (await context.Refunds.AnyAsync(row => row.PaymentId == message.PaymentId && row.Reason == RefundReason.LateSuccess && row.Status != RefundStatus.Failed, cancellationToken))
        {
            return;
        }

        var refunded = await refunds.CreateAsync(message.PaymentId, null, RefundReason.LateSuccess, "Refunded automatically: the payment succeeded after its invoice no longer needed it.", cancellationToken);
        if (refunded.IsFailure)
        {
            LogNotRefunded(logger, message.PaymentId, refunded.Error.Code);
        }
    }

    [LoggerMessage(LogLevel.Warning, "The late success of payment {PaymentId} was not refunded automatically: {Code}; its attention item stays open")]
    private static partial void LogNotRefunded(ILogger logger, Guid paymentId, string code);
}
