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
using ProviderStatus = Tahsil.Providers.Abstractions.PaymentStatus;

namespace Tahsil.Application.Payments;

/// <summary>
/// Settles payments whose outcome is not known yet by asking the provider about their reference (ENG-15 to ENG-18):
/// on a backoff schedule, with operators called in after seven days or at once for providers that cannot be searched,
/// and customer-present payments canceled when the customer's time is up and the provider has no success.
/// </summary>
public sealed class PaymentReconciliation(
    ITahsilDbContext context,
    IBillingLocks locks,
    ProviderRegistry registry,
    ProviderAccess providers,
    PaymentOutcomes outcomes,
    PaymentRequests requests,
    Attention attention,
    IOptions<PaymentOptions> options,
    BillingClock clock)
{
    /// <summary>
    /// The waits before each attempt (ENG-16): 2, 5, 15 and 60 minutes, then 6 and 24 hours, then daily. The first is
    /// <see cref="PaymentOptions.ReconcileAfter"/> (or the customer's time), set when the payment is written ahead.
    /// </summary>
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60), TimeSpan.FromHours(6), TimeSpan.FromHours(24),
    ];

    private static readonly TimeSpan AttentionAfter = TimeSpan.FromDays(7);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(30);

    /// <summary>The wait before attempt <paramref name="attempt"/>, counting from 1.</summary>
    public static TimeSpan WaitBefore(int attempt) => Backoff[Math.Clamp(attempt, 1, Backoff.Length) - 1];

    public async Task ReconcileAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        if (await ClaimAsync(paymentId, cancellationToken) is not var (payment, connection))
        {
            return;
        }

        var now = clock.GetUtcNow();
        var expiring = payment.Status == PaymentStatus.RequiresAction && payment.ExpiresAt <= now;
        var call = providers.For(connection);
        var capabilities = registry.Find(connection.Provider)?.CapabilitiesFor(connection.Settings);
        if (call is null || capabilities is null || capabilities.FindByReference == ReferenceSearch.None)
        {
            // ENG-17: a provider that cannot be searched by our reference is not guessed at.
            await StopAsync(payment.Id, AttentionKind.ReconciliationImpossible, cancellationToken);
            return;
        }

        var lookup = await call.Provider.FindPaymentAsync(call.Context, await requests.QueryAsync(payment, connection.Id, CancellationToken.None), CancellationToken.None);
        switch (lookup)
        {
            case PaymentLookup.Found { Outcome.Status: ProviderStatus.Succeeded or ProviderStatus.Failed } found:
                await outcomes.ApplyAsync(payment.Id, found.Outcome, CancellationToken.None);
                return;
            case not null when expiring:
                // ENG-18: without a success by the deadline the attempt ends, even if the provider's page is still open;
                // a success after it is a late success (PAY-14).
                await outcomes.CancelAsync(payment.Id, CancellationToken.None);
                return;
            case PaymentLookup.NotFound when now - payment.CreatedAt >= options.Value.NotFoundAfter
                && (capabilities.FindByReference == ReferenceSearch.Any || payment.Initiator == PaymentInitiator.Merchant):
                // Old enough that the provider would have indexed it: it never got there. A provider that sees only
                // successes says nothing about a customer who may still be paying, so only time ends those (PAY-16).
                await outcomes.ApplyAsync(payment.Id, PaymentOutcome.Failure(FailureCode.NotSubmitted), CancellationToken.None);
                return;
        }

        // Still pending, too young to be missing, or the lookup failed: the next backoff step comes.
        if (now - payment.CreatedAt >= AttentionAfter)
        {
            await RaiseAsync(payment.Id, AttentionKind.ReconciliationOverdue, stop: now - payment.CreatedAt >= GiveUpAfter, cancellationToken);
        }
    }

    /// <summary>Takes the next attempt (ENG-15): its count goes up and the one after it is scheduled, before the provider is asked.</summary>
    private async Task<(Payment Payment, ProviderConnection Connection)?> ClaimAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        await using var transaction = await context.BeginTransactionAsync(cancellationToken);
        await locks.LockPaymentAsync(paymentId, cancellationToken);
        var payment = await context.Payments.SingleOrDefaultAsync(row => row.Id == paymentId, cancellationToken);
        if (payment is not { IsInFlight: true, ConnectionId: { } connectionId })
        {
            return null;
        }

        var now = clock.GetUtcNow();
        var attempt = payment.ReconcileAttempts + 1;
        payment.ClaimReconciliation(now + WaitBefore(attempt + 1), now);
        var connection = await context.ProviderConnections.AsNoTracking().SingleAsync(row => row.Id == connectionId, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (payment, connection);
    }

    private Task StopAsync(Guid paymentId, AttentionKind kind, CancellationToken cancellationToken) => RaiseAsync(paymentId, kind, stop: true, cancellationToken);

    private async Task RaiseAsync(Guid paymentId, AttentionKind kind, bool stop, CancellationToken cancellationToken)
    {
        await using var transaction = await context.BeginTransactionAsync(cancellationToken);
        await locks.LockPaymentAsync(paymentId, cancellationToken);
        var payment = await context.Payments.SingleAsync(row => row.Id == paymentId, cancellationToken);
        if (!payment.IsInFlight)
        {
            return;
        }

        var now = clock.GetUtcNow();
        await attention.RaiseAsync(kind, AuditSubjects.Payment, payment.Id, payment.CustomerId, new Dictionary<string, string>
        {
            ["reference"] = payment.Reference,
            ["attempts"] = payment.ReconcileAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }, now, cancellationToken);
        if (stop)
        {
            payment.StopReconciling(now);
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
