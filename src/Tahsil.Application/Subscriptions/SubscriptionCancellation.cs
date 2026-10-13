using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Application.Coupons;
using Tahsil.Application.Invoices;
using Tahsil.Application.Payments;
using Tahsil.Contracts;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Money;
using Tahsil.Domain.Subscriptions;
using Invoice = Tahsil.Domain.Invoices.Invoice;
using Model = Tahsil.Domain.Subscriptions;
using ModelInvoices = Tahsil.Domain.Invoices;

namespace Tahsil.Application.Subscriptions;

/// <summary>
/// Cancels a subscription (SUB-28 to SUB-31): at the period end, at a time within the period, or now with a final
/// invoice of its usage so far and its pending items and, if asked, a refund. A refund that fails does not undo the cancellation.
/// </summary>
public sealed class CancelSubscriptionHandler(
    ITahsilDbContext context,
    IAuthorizer authorizer,
    IAuditLog audit,
    IOutbox outbox,
    IBillingLocks locks,
    InvoiceLines invoiceLines,
    InvoiceFinalizer finalizer,
    SubscriptionLines lines,
    SubscriptionPreviews previews,
    Usage.SubscriptionUsage usage,
    Refunds refunds,
    Attention attention,
    Redemptions redemptions,
    SubscriptionMapper mapper,
    BillingClock clock)
{
    private static readonly string[] CancelAtChanged = ["cancelAt"];

    public async Task<Result<Versioned<SubscriptionResponse>>> HandleAsync(
        Guid id, CancelSubscriptionRequest request, IReadOnlyCollection<uint>? expectedVersions, CancellationToken cancellationToken)
    {
        if (authorizer.RequireOrCustomer(TahsilPermissions.SubscriptionsWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        // A customer's own refund in the portal follows its rules (PRT-8, PRT-10); the API needs the refund permission.
        if (request.Refund != CancellationRefund.None && !authorizer.IsCustomer && authorizer.Require(TahsilPermissions.RefundsWrite) is { IsFailure: true } cannotRefund)
        {
            return cannotRefund.Error;
        }

        // Kimlik's deletion of the subscriber cancels on its own (KML-23).
        if (request.Reason is not (Contracts.CancellationReason.RequestedByCustomer or Contracts.CancellationReason.RequestedByMerchant)
            && !(authorizer.IsSystem && request.Reason == Contracts.CancellationReason.KimlikSubscriberDeleted))
        {
            return SubscriptionErrors.Invalid("reason", "A cancellation is requested by the customer or by the merchant.");
        }

        var now = clock.GetUtcNow();
        var immediately = request.At == "now";
        DateTimeOffset? at = null;
        if (!immediately && request.At != "period_end")
        {
            if (!DateTimeOffset.TryParse(request.At, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return SubscriptionErrors.Invalid("at", "The cancellation is now, period_end or an RFC 3339 timestamp.");
            }

            at = parsed;
        }

        if (request.Refund != CancellationRefund.None && !immediately)
        {
            return SubscriptionErrors.Invalid("refund", "Refunds go with cancellations that take effect now.");
        }

        var reason = request.Reason.Map<Model.CancellationReason>();
        var feedback = request.Feedback?.Map<Model.CancellationFeedback>();
        Subscription subscription;
        List<(Guid Payment, long Amount)> toRefund = [];
        await using (var transaction = await context.BeginTransactionAsync(cancellationToken))
        {
            await locks.LockSubscriptionAsync(id, cancellationToken);
            if (await context.Subscriptions.Include(row => row.Items).SingleOrDefaultAsync(row => row.Id == id, cancellationToken) is not { } found
                || !authorizer.Owns(found.CustomerId))
            {
                return SubscriptionErrors.NotFound;
            }

            if (Concurrency.Check(found, expectedVersions) is { IsFailure: true } stale)
            {
                return stale.Error;
            }

            subscription = found;
            if (!immediately)
            {
                if (subscription.ScheduleCancellation(at ?? subscription.CurrentPeriodEnd, reason, feedback, request.Comment, now) is { IsFailure: true } refused)
                {
                    return refused.Error;
                }

                audit.Record(AuditActions.SubscriptionUpdated, AuditSubject.Of(AuditSubjects.Subscription, subscription.Id), new Dictionary<string, object?>
                {
                    ["changed"] = CancelAtChanged,
                    ["status"] = SubscriptionMapper.Wire(subscription.Status),
                    ["previousStatus"] = null,
                }, customerId: subscription.CustomerId);

                // The cancellation is confirmed by email at once, not only when it takes effect (SUB-35, PRT-9).
                Notifications.CustomerEmails.Enqueue(
                    outbox,
                    Notifications.EmailTypes.SubscriptionCanceled,
                    subscription.Id,
                    $"{subscription.Id}:scheduled:{subscription.CancelAt!.Value.ToUnixTimeSeconds()}",
                    new Dictionary<string, string> { ["scheduled"] = "true" });
            }
            else
            {
                var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == subscription.CustomerId, cancellationToken);
                toRefund = request.Refund switch
                {
                    CancellationRefund.Prorated => await ProratedRefundAsync(subscription, customer, now, cancellationToken),
                    CancellationRefund.LastPayment => await LastPaymentRefundAsync(subscription, cancellationToken),
                    CancellationRefund.AllPayments => await AllPaymentsRefundAsync(subscription, cancellationToken),
                    _ => [],
                };
                if (subscription.Cancel(now, reason, feedback, request.Comment, now) is { IsFailure: true } refused)
                {
                    return refused.Error;
                }

                if (await SettlePendingItemsAsync(subscription, customer, request.InvoiceNow, now, cancellationToken) is { IsFailure: true } failed)
                {
                    return failed.Error;
                }

                var canceled = new Dictionary<string, object?>
                {
                    ["reason"] = SubscriptionMapper.Wire(subscription.CancellationReason!.Value),
                    ["endedAt"] = subscription.EndedAt,
                };
                if (toRefund.Count > 0)
                {
                    // Known now, before the refunds are made one by one after the commit, so the email states all of it.
                    canceled["refund"] = toRefund.Sum(part => part.Amount);
                }

                audit.Record(AuditActions.SubscriptionCanceled, AuditSubject.Of(AuditSubjects.Subscription, subscription.Id), canceled, customerId: subscription.CustomerId);
            }

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        foreach (var (payment, amount) in toRefund)
        {
            await RefundAsync(subscription, payment, amount, cancellationToken);
        }

        return new Versioned<SubscriptionResponse>(await mapper.ToResponseAsync(subscription, cancellationToken), subscription.Version);
    }

    /// <summary>
    /// The unused share of the current period (SUB-31): the credit a removal of every item would get now (MNY-30), with
    /// its tax, taken from the newest payments of the period's paid invoices and capped at what they can still refund.
    /// </summary>
    private async Task<List<(Guid Payment, long Amount)>> ProratedRefundAsync(Subscription subscription, Customer customer, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (subscription.Status == Model.SubscriptionStatus.Trialing && !subscription.TrialEnded)
        {
            return [];
        }

        var period = new BillingPeriod(subscription.CurrentPeriodStart, subscription.CurrentPeriodEnd);
        var items = await lines.PricedAsync(subscription.Items.Select(item => (item.PriceId, item.Quantity)), cancellationToken);
        var credits = await lines.ProrationAsync(items, [], period, now, creditsOnly: true, customer.Locale, cancellationToken);
        if (credits.Count == 0)
        {
            return [];
        }

        var credit = await previews.InvoiceAsync(customer, subscription, ModelInvoices.BillingReason.SubscriptionCancel, period, credits, now, cancellationToken);
        var invoices = context.Invoices.Where(invoice => invoice.SubscriptionId == subscription.Id && invoice.Status == ModelInvoices.InvoiceStatus.Paid
            && invoice.PeriodStart >= period.Start && invoice.PeriodStart < period.End).Select(invoice => invoice.Id);
        return await TakeAsync(invoices, -credit.Total, cancellationToken);
    }

    /// <summary>
    /// What a cancellation now would refund with <paramref name="refund"/>, without cancelling: for the choices the
    /// portal shows (PRT-8, PRT-10).
    /// </summary>
    public async Task<long> RefundPreviewAsync(Guid subscriptionId, CancellationRefund refund, CancellationToken cancellationToken)
    {
        var subscription = await context.Subscriptions.AsNoTracking().Include(row => row.Items).SingleAsync(row => row.Id == subscriptionId, cancellationToken);
        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == subscription.CustomerId, cancellationToken);
        var taken = refund switch
        {
            CancellationRefund.Prorated => await ProratedRefundAsync(subscription, customer, clock.GetUtcNow(), cancellationToken),
            CancellationRefund.LastPayment => await LastPaymentRefundAsync(subscription, cancellationToken),
            CancellationRefund.AllPayments => await AllPaymentsRefundAsync(subscription, cancellationToken),
            _ => [],
        };
        return taken.Sum(part => part.Amount);
    }

    /// <summary>Every paid invoice's payments, in full (PRT-10).</summary>
    private async Task<List<(Guid Payment, long Amount)>> AllPaymentsRefundAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        var paid = context.Invoices.Where(invoice => invoice.SubscriptionId == subscription.Id && invoice.Status == ModelInvoices.InvoiceStatus.Paid && invoice.Total > 0)
            .Select(invoice => invoice.Id);
        return await TakeAsync(paid, long.MaxValue, cancellationToken);
    }

    /// <summary>The latest paid invoice's payments, in full (SUB-31).</summary>
    private async Task<List<(Guid Payment, long Amount)>> LastPaymentRefundAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        var latest = context.Invoices.Where(invoice => invoice.SubscriptionId == subscription.Id && invoice.Status == ModelInvoices.InvoiceStatus.Paid && invoice.Total > 0)
            .OrderByDescending(invoice => invoice.PaidAt).Select(invoice => invoice.Id).Take(1);
        return await TakeAsync(latest, long.MaxValue, cancellationToken);
    }

    /// <summary>Up to <paramref name="amount"/> from what the invoices' succeeded payments can still refund, newest payment first.</summary>
    private async Task<List<(Guid Payment, long Amount)>> TakeAsync(IQueryable<Guid> invoices, long amount, CancellationToken cancellationToken)
    {
        var payments = await context.Payments.AsNoTracking()
            .Where(payment => payment.InvoiceId != null && invoices.Contains(payment.InvoiceId.Value) && payment.Status == Domain.Payments.PaymentStatus.Succeeded)
            .OrderByDescending(payment => payment.SucceededAt)
            .Select(payment => new
            {
                payment.Id,
                Left = payment.Amount - context.Refunds.Where(refund => refund.PaymentId == payment.Id && refund.Status != Domain.Payments.RefundStatus.Failed).Sum(refund => (long?)refund.Amount) ?? 0,
            })
            .ToListAsync(cancellationToken);
        var taken = new List<(Guid, long)>();
        foreach (var payment in payments)
        {
            var take = Math.Min(payment.Left, amount);
            if (take > 0)
            {
                taken.Add((payment.Id, take));
                amount -= take;
            }
        }

        return taken;
    }

    /// <summary>
    /// The usage so far and the subscription's pending items go on a final invoice, finalized and collected like a
    /// renewal; without <paramref name="invoiceNow"/>, the usage is not billed and the items are deleted (SUB-29).
    /// </summary>
    private async Task<Result> SettlePendingItemsAsync(Subscription subscription, Customer customer, bool invoiceNow, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pending = await context.PendingInvoiceItems
            .Where(item => item.SubscriptionId == subscription.Id && item.InvoiceId == null)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);
        if (!invoiceNow)
        {
            context.PendingInvoiceItems.RemoveRange(pending);
            return Result.Success();
        }

        var used = await usage.InvoiceAsync(subscription, subscription.Items, now, customer.Locale, cancellationToken);
        if (pending.Count == 0 && used.Count == 0)
        {
            return Result.Success();
        }

        var invoice = Invoice.CreateDraft(
            customer.Id, subscription.Currency, ModelInvoices.BillingReason.SubscriptionCancel, subscription.CollectionMethod, subscription.DaysUntilDue, memo: null, autoAdvance: false, Metadata.Empty, now, subscription.Id,
            discount: await redemptions.OfSubscriptionAsync(subscription.DiscountId, cancellationToken)).Value;
        invoice.AddLines([.. used, .. await invoiceLines.FromPendingItemsAsync(pending, customer.Locale, cancellationToken)], customer.IsTaxExempt, now);
        foreach (var item in pending)
        {
            item.IncludeIn(invoice.Id, now);
        }

        context.Invoices.Add(invoice);
        subscription.SetLatestInvoice(invoice.Id, now);
        return await finalizer.FinalizeAsync(invoice, cancellationToken);
    }

    /// <summary>A refund refused before reaching the provider becomes an attention item; one the provider declines is reported by the refund itself.</summary>
    private async Task RefundAsync(Subscription subscription, Guid payment, long amount, CancellationToken cancellationToken)
    {
        var refunded = await refunds.CreateAsync(payment, amount == long.MaxValue ? null : amount, Domain.Payments.RefundReason.SubscriptionCanceled, $"Subscription {subscription.Id} canceled", cancellationToken);
        if (refunded.IsFailure)
        {
            await attention.RaiseAsync(AttentionKind.RefundFailed, AuditSubjects.Payment, payment, subscription.CustomerId, new Dictionary<string, string>
            {
                ["subscriptionId"] = subscription.Id.ToString(),
                ["amount"] = amount == long.MaxValue ? "all" : amount.ToString(CultureInfo.InvariantCulture),
                ["code"] = refunded.Error.Code,
            }, clock.GetUtcNow(), cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }
    }
}

/// <summary>Takes back a scheduled cancellation before it takes effect (SUB-33).</summary>
public sealed class ResumeSubscriptionHandler(
    ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, IBillingLocks locks, SubscriptionMapper mapper, BillingClock clock)
{
    private static readonly string[] CancelAtChanged = ["cancelAt"];

    public async Task<Result<Versioned<SubscriptionResponse>>> HandleAsync(Guid id, IReadOnlyCollection<uint>? expectedVersions, CancellationToken cancellationToken)
    {
        if (authorizer.RequireOrCustomer(TahsilPermissions.SubscriptionsWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        await using var transaction = await context.BeginTransactionAsync(cancellationToken);
        await locks.LockSubscriptionAsync(id, cancellationToken);
        if (await context.Subscriptions.Include(row => row.Items).SingleOrDefaultAsync(row => row.Id == id, cancellationToken) is not { } subscription
            || !authorizer.Owns(subscription.CustomerId))
        {
            return SubscriptionErrors.NotFound;
        }

        if (Concurrency.Check(subscription, expectedVersions) is { IsFailure: true } stale)
        {
            return stale.Error;
        }

        if (subscription.Resume(clock.GetUtcNow()) is { IsFailure: true } refused)
        {
            return refused.Error;
        }

        audit.Record(AuditActions.SubscriptionUpdated, AuditSubject.Of(AuditSubjects.Subscription, subscription.Id), new Dictionary<string, object?>
        {
            ["changed"] = CancelAtChanged,
            ["status"] = SubscriptionMapper.Wire(subscription.Status),
            ["previousStatus"] = null,
        }, customerId: subscription.CustomerId);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new Versioned<SubscriptionResponse>(await mapper.ToResponseAsync(subscription, cancellationToken), subscription.Version);
    }
}
