using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Coupons;
using Tahsil.Contracts;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Money;
using Model = Tahsil.Domain.Invoices;

namespace Tahsil.Application.Invoices;

/// <summary>Creates a manual draft (INV §8) from lines and, if asked, the customer's pending items.</summary>
public sealed class CreateInvoiceHandler(
    ITahsilDbContext context,
    IAuthorizer authorizer,
    IAuditLog audit,
    IIdempotency idempotency,
    InvoiceLines lines,
    InvoiceMapper mapper,
    Redemptions redemptions,
    IOptions<InvoiceOptions> options,
    BillingClock clock)
{
    public async Task<Result<Versioned<InvoiceResponse>>> HandleAsync(CreateInvoiceRequest request, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.InvoicesWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        var found = await InvoiceCustomers.FindAsync(context, request.Customer, cancellationToken);
        if (found.IsFailure)
        {
            return found.Error;
        }

        var customer = found.Value;
        if (!Currency.TryGet(request.Currency, out _))
        {
            return Error.Validation("currency.unsupported", "The currency is not a supported ISO 4217 code.", "currency");
        }

        var metadata = Metadata.Validate(request.Metadata);
        if (metadata.IsFailure)
        {
            return metadata.Error;
        }

        var drafts = await lines.FromRequestsAsync(request.Lines ?? [], request.Currency, customer.Locale, cancellationToken);
        if (drafts.IsFailure)
        {
            return drafts.Error;
        }

        Model.InvoiceDiscount? discount = null;
        if (request.Coupon is { } coupon)
        {
            var chosen = await redemptions.ForDraftAsync(coupon, request.Currency, cancellationToken);
            if (chosen.IsFailure)
            {
                return chosen.Error;
            }

            discount = chosen.Value;
        }

        var now = clock.GetUtcNow();
        var collection = request.CollectionMethod.Map<Model.CollectionMethod>();
        var created = Model.Invoice.CreateDraft(
            customer.Id,
            request.Currency,
            Model.BillingReason.Manual,
            collection,
            collection == Model.CollectionMethod.SendInvoice ? request.DaysUntilDue ?? options.Value.DefaultDaysUntilDue : null,
            request.Memo,
            autoAdvance: false,
            metadata.Value,
            now,
            discount: discount);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var invoice = created.Value;
        if (request.IncludePendingItems)
        {
            var items = await context.PendingInvoiceItems
                .Where(item => item.CustomerId == customer.Id && item.SubscriptionId == null && item.InvoiceId == null && item.Currency == request.Currency)
                .InJoiningOrder()
                .ToListAsync(cancellationToken);
            drafts.Value.AddRange(await lines.FromPendingItemsAsync(items, customer.Locale, cancellationToken));
            foreach (var item in items)
            {
                item.IncludeIn(invoice.Id, now);
            }
        }

        invoice.AddLines(drafts.Value, customer.IsTaxExempt, now);
        context.Invoices.Add(invoice);
        idempotency.RecordCreated(ResourceTypes.Invoice, invoice.Id);
        audit.Record(AuditActions.InvoiceDrafted, AuditSubject.Of(AuditSubjects.Invoice, invoice.Id), InvoiceFinalizer.Details(invoice), customerId: customer.Id);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another draft took the same pending items first.
            return CommonErrors.ConcurrencyConflict;
        }

        return new Versioned<InvoiceResponse>(mapper.ToResponse(invoice), invoice.Version);
    }
}

public sealed class AddInvoiceLinesHandler(ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, InvoiceLines lines, InvoiceMapper mapper, BillingClock clock)
{
    public async Task<Result<Versioned<InvoiceResponse>>> HandleAsync(Guid id, AddInvoiceLinesRequest request, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.InvoicesWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.FindInvoiceAsync(id, cancellationToken) is not { } invoice)
        {
            return Model.InvoiceErrors.NotFound;
        }

        if (!invoice.IsDraft)
        {
            return Model.InvoiceErrors.NotDraft;
        }

        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == invoice.CustomerId, cancellationToken);
        var drafts = await lines.FromRequestsAsync(request.Lines, invoice.Currency, customer.Locale, cancellationToken);
        if (drafts.IsFailure)
        {
            return drafts.Error;
        }

        var added = invoice.AddLines(drafts.Value, customer.IsTaxExempt, clock.GetUtcNow());
        if (added.IsFailure)
        {
            return added.Error;
        }

        audit.Record(AuditActions.InvoiceUpdated, AuditSubject.Of(AuditSubjects.Invoice, invoice.Id), new Dictionary<string, object?>
        {
            ["fields"] = InvoiceSaving.LinesField,
            ["addedLines"] = added.Value.Select(line => line.Id).ToArray(),
        }, customerId: invoice.CustomerId);
        return await InvoiceSaving.SaveAsync(context, invoice, mapper, cancellationToken);
    }
}

public sealed class RemoveInvoiceLineHandler(ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, InvoiceMapper mapper, BillingClock clock)
{
    public async Task<Result<Versioned<InvoiceResponse>>> HandleAsync(Guid id, Guid lineId, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.InvoicesWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.FindInvoiceAsync(id, cancellationToken) is not { } invoice)
        {
            return Model.InvoiceErrors.NotFound;
        }

        var customer = await context.Customers.AsNoTracking().SingleAsync(row => row.Id == invoice.CustomerId, cancellationToken);
        var now = clock.GetUtcNow();
        var removed = invoice.RemoveLine(lineId, customer.IsTaxExempt, now);
        if (removed.IsFailure)
        {
            return removed.Error;
        }

        if (removed.Value is { } pendingItemId)
        {
            (await context.PendingInvoiceItems.SingleAsync(item => item.Id == pendingItemId, cancellationToken)).Release(now);
        }

        audit.Record(AuditActions.InvoiceUpdated, AuditSubject.Of(AuditSubjects.Invoice, invoice.Id), new Dictionary<string, object?>
        {
            ["fields"] = InvoiceSaving.LinesField,
            ["removedLine"] = lineId,
        }, customerId: invoice.CustomerId);
        return await InvoiceSaving.SaveAsync(context, invoice, mapper, cancellationToken);
    }
}

/// <summary>Drafts change their terms; finalized invoices only their legal invoice and metadata (INV §8, INV-23).</summary>
public sealed class UpdateInvoiceHandler(
    ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, InvoiceMapper mapper, Redemptions redemptions, BillingClock clock)
{
    public async Task<Result<Versioned<InvoiceResponse>>> HandleAsync(
        Guid id, UpdateInvoiceRequest request, IReadOnlyCollection<uint>? expectedVersions, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.InvoicesWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.FindInvoiceAsync(id, cancellationToken) is not { } invoice)
        {
            return Model.InvoiceErrors.NotFound;
        }

        if (Concurrency.Check(invoice, expectedVersions) is { IsFailure: true } stale)
        {
            return stale.Error;
        }

        var now = clock.GetUtcNow();
        var termsChange = request.CollectionMethod is not null || request.HasDaysUntilDue || request.HasMemo;
        if (termsChange)
        {
            var collection = request.CollectionMethod?.Map<Model.CollectionMethod>() ?? invoice.CollectionMethod;
            var updated = invoice.UpdateDraft(
                collection,
                request.HasDaysUntilDue ? request.DaysUntilDue : invoice.DaysUntilDue,
                request.HasMemo ? request.Memo : invoice.Memo,
                invoice.AutoAdvance,
                now);
            if (updated.IsFailure)
            {
                return updated.Error;
            }
        }

        if (request.HasCoupon)
        {
            if (invoice.BillingReason != Model.BillingReason.Manual)
            {
                return Model.InvoiceErrors.CouponOnSubscriptionInvoice;
            }

            Model.InvoiceDiscount? discount = null;
            if (request.Coupon is { } coupon)
            {
                var chosen = await redemptions.ForDraftAsync(coupon, invoice.Currency, cancellationToken);
                if (chosen.IsFailure)
                {
                    return chosen.Error;
                }

                discount = chosen.Value;
            }

            var taxExempt = await context.Customers.Where(row => row.Id == invoice.CustomerId).Select(row => row.IsTaxExempt).SingleAsync(cancellationToken);
            if (invoice.SetDiscount(discount, taxExempt, now) is { IsFailure: true } notDraft)
            {
                return notDraft.Error;
            }
        }

        if (request.HasLegalInvoice)
        {
            var legal = request.LegalInvoice is { } data ? new Model.LegalInvoice(data.Number ?? string.Empty, data.Date, data.Url) : null;
            if (invoice.SetLegalInvoice(legal, now) is { IsFailure: true } invalid)
            {
                return invalid.Error;
            }
        }

        if (request.Metadata is not null)
        {
            var metadata = Metadata.Validate(request.Metadata);
            if (metadata.IsFailure)
            {
                return metadata.Error;
            }

            invoice.UpdateMetadata(metadata.Value, now);
        }

        audit.Record(AuditActions.InvoiceUpdated, AuditSubject.Of(AuditSubjects.Invoice, invoice.Id), Changes.Of(
            ("collectionMethod", request.CollectionMethod is not null),
            ("daysUntilDue", request.HasDaysUntilDue),
            ("memo", request.HasMemo),
            ("coupon", request.HasCoupon),
            ("legalInvoice", request.HasLegalInvoice),
            ("metadata", request.Metadata is not null)), customerId: invoice.CustomerId);
        return await InvoiceSaving.SaveAsync(context, invoice, mapper, cancellationToken);
    }
}

/// <summary>Deletes a draft without payments; its pending items wait for another invoice.</summary>
public sealed class DeleteInvoiceHandler(ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, IBillingLocks locks, BillingClock clock)
{
    public async Task<Result> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.InvoicesWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        await using var transaction = await context.BeginTransactionAsync(cancellationToken);
        await locks.LockInvoiceAsync(id, cancellationToken);
        if (await context.FindInvoiceAsync(id, cancellationToken) is not { } invoice)
        {
            return Model.InvoiceErrors.NotFound;
        }

        if (!invoice.IsDraft)
        {
            return Model.InvoiceErrors.InvalidState(invoice.Status, "deleted");
        }

        if (invoice.AmountPaid != 0)
        {
            return Model.InvoiceErrors.HasPayments;
        }

        var now = clock.GetUtcNow();
        foreach (var item in await context.PendingInvoiceItems.Where(item => item.InvoiceId == id).ToListAsync(cancellationToken))
        {
            item.Release(now);
        }

        context.Invoices.Remove(invoice);
        audit.Record(AuditActions.InvoiceDeleted, AuditSubject.Of(AuditSubjects.Invoice, invoice.Id), customerId: invoice.CustomerId);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed class FinalizeInvoiceHandler(
    ITahsilDbContext context, IAuthorizer authorizer, IBillingLocks locks, InvoiceFinalizer finalizer, IWorkerSignals signals, InvoiceMapper mapper)
{
    public async Task<Result<Versioned<InvoiceResponse>>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.InvoicesWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        await using var transaction = await context.BeginTransactionAsync(cancellationToken);
        await locks.LockInvoiceAsync(id, cancellationToken);
        if (await context.FindInvoiceAsync(id, cancellationToken) is not { } invoice)
        {
            return Model.InvoiceErrors.NotFound;
        }

        if (await finalizer.FinalizeAsync(invoice, cancellationToken) is { IsFailure: true } failed)
        {
            return failed.Error;
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (invoice.NextPaymentAttemptAt is not null)
        {
            // ENG-11: an invoice to charge now wakes the payment worker.
            signals.Notify(Payments.WorkerNames.PaymentCollection);
        }

        return new Versioned<InvoiceResponse>(mapper.ToResponse(invoice), invoice.Version);
    }
}

/// <summary>Voids an invoice without payments and gives the balance back what finalization took or added (INV-16).</summary>
public sealed class VoidInvoiceHandler(
    ITahsilDbContext context, IAuthorizer authorizer, IBillingLocks locks, IRequestContext request, InvoiceVoiding voiding, InvoiceMapper mapper, BillingClock clock)
{
    public async Task<Result<Versioned<InvoiceResponse>>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.InvoicesWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        await using var transaction = await context.BeginTransactionAsync(cancellationToken);
        await locks.LockInvoiceAsync(id, cancellationToken);
        if (await context.FindInvoiceAsync(id, cancellationToken) is not { } invoice)
        {
            return Model.InvoiceErrors.NotFound;
        }

        if (await voiding.VoidAsync(invoice, request.Caller.ToActor(), clock.GetUtcNow(), cancellationToken) is { IsFailure: true } failed)
        {
            return failed.Error;
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new Versioned<InvoiceResponse>(mapper.ToResponse(invoice), invoice.Version);
    }
}

/// <summary>Emails an invoice sent for payment again (INV-22), at most once every ten minutes.</summary>
public sealed class SendInvoiceHandler(ITahsilDbContext context, IAuthorizer authorizer, IOutbox outbox, InvoiceMapper mapper, BillingClock clock)
{
    public static readonly Error TooSoon = Error.RateLimited("invoice.send_rate_limited", "The invoice was emailed in the last ten minutes; try again later.");

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    public async Task<Result<Versioned<InvoiceResponse>>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.InvoicesWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.FindInvoiceAsync(id, cancellationToken) is not { } invoice)
        {
            return Model.InvoiceErrors.NotFound;
        }

        if (invoice.Status is not (Model.InvoiceStatus.Open or Model.InvoiceStatus.Uncollectible) || invoice.CollectionMethod != Model.CollectionMethod.SendInvoice)
        {
            return Error.Conflict("invoice.invalid_state", "Only finalized, unpaid invoices sent for payment are emailed.");
        }

        var now = clock.GetUtcNow();
        var sent = $"{Notifications.EmailTypes.InvoiceIssued}:{invoice.Id}";
        if (await context.NotificationLogs.AnyAsync(log => log.OccurrenceKey.StartsWith(sent) && log.CreatedAt > now - Interval, cancellationToken))
        {
            return TooSoon;
        }

        Notifications.CustomerEmails.Enqueue(outbox, Notifications.EmailTypes.InvoiceIssued, invoice.Id, $"{invoice.Id}:{now.ToUnixTimeMilliseconds()}");
        await context.SaveChangesAsync(cancellationToken);
        return new Versioned<InvoiceResponse>(mapper.ToResponse(invoice), invoice.Version);
    }
}

public sealed class MarkInvoiceUncollectibleHandler(
    ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, IBillingLocks locks, InvoiceMapper mapper, BillingClock clock)
{
    public async Task<Result<Versioned<InvoiceResponse>>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.InvoicesWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        await using var transaction = await context.BeginTransactionAsync(cancellationToken);
        await locks.LockInvoiceAsync(id, cancellationToken);
        if (await context.FindInvoiceAsync(id, cancellationToken) is not { } invoice)
        {
            return Model.InvoiceErrors.NotFound;
        }

        if (invoice.MarkUncollectible(clock.GetUtcNow()) is { IsFailure: true } invalid)
        {
            return invalid.Error;
        }

        audit.Record(AuditActions.InvoiceMarkedUncollectible, AuditSubject.Of(AuditSubjects.Invoice, invoice.Id), InvoiceFinalizer.Details(invoice), customerId: invoice.CustomerId);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new Versioned<InvoiceResponse>(mapper.ToResponse(invoice), invoice.Version);
    }
}

public sealed class GetInvoiceHandler(ITahsilDbContext context, IAuthorizer authorizer, InvoiceMapper mapper)
{
    public async Task<Result<Versioned<InvoiceResponse>>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.InvoicesRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        return await context.Invoices.AsNoTracking().Include(invoice => invoice.Lines).SingleOrDefaultAsync(invoice => invoice.Id == id, cancellationToken) is { } invoice
            ? new Versioned<InvoiceResponse>(mapper.ToResponse(invoice), invoice.Version)
            : Model.InvoiceErrors.NotFound;
    }
}

/// <summary>
/// Invoice filters; <c>InDunning</c> keeps unpaid invoices whose dunning started and has not ended, <c>Overdue</c> those
/// sent for payment whose due date passed unpaid (INV-13).
/// </summary>
public sealed record ListInvoicesQuery(
    Guid? Customer = null,
    InvoiceStatus? Status = null,
    string? Number = null,
    string? Cursor = null,
    int? Limit = null,
    Guid? Subscription = null,
    bool InDunning = false,
    bool Overdue = false);

public sealed class ListInvoicesHandler(ITahsilDbContext context, IAuthorizer authorizer, InvoiceMapper mapper, BillingClock clock)
{
    private static readonly Keyset<Model.Invoice, Guid> Newest = new("newest", invoice => invoice.Id, invoice => invoice.Id, Descending: true);

    public async Task<Result<Page<InvoiceResponse>>> HandleAsync(ListInvoicesQuery query, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.InvoicesRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (!Paging.IsValidLimit(query.Limit ?? Paging.DefaultLimit))
        {
            return CommonErrors.InvalidLimit();
        }

        var invoices = context.Invoices.AsNoTracking().Include(invoice => invoice.Lines).AsQueryable();
        if (query.Customer is { } customer)
        {
            invoices = invoices.Where(invoice => invoice.CustomerId == customer);
        }

        if (query.Subscription is { } subscription)
        {
            invoices = invoices.Where(invoice => invoice.SubscriptionId == subscription);
        }

        if (query.Status is { } status)
        {
            var modelStatus = status.Map<Model.InvoiceStatus>();
            invoices = invoices.Where(invoice => invoice.Status == modelStatus);
        }

        if (Text.Normalize(query.Number) is { } number)
        {
            invoices = invoices.Where(invoice => invoice.Number == number);
        }

        if (query.InDunning)
        {
            invoices = invoices.Where(invoice => (invoice.Status == Model.InvoiceStatus.Open || invoice.Status == Model.InvoiceStatus.Uncollectible)
                && invoice.DunningStartedAt != null && invoice.DunningEndedAt == null);
        }

        if (query.Overdue)
        {
            var now = clock.GetUtcNow();
            invoices = invoices.Where(invoice => invoice.Status == Model.InvoiceStatus.Open && invoice.CollectionMethod == Model.CollectionMethod.SendInvoice && invoice.DueDate < now);
        }

        var page = await Newest.ReadAsync(invoices, query.Cursor, query.Limit ?? Paging.DefaultLimit, cancellationToken);
        return page is null ? CommonErrors.InvalidCursor : new Page<InvoiceResponse>([.. page.Items.Select(mapper.ToResponse)], page.NextCursor);
    }
}

internal static class InvoiceSaving
{
    public static readonly string[] LinesField = ["lines"];

    public static async Task<Result<Versioned<InvoiceResponse>>> SaveAsync(ITahsilDbContext context, Model.Invoice invoice, InvoiceMapper mapper, CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return CommonErrors.ConcurrencyConflict;
        }

        return new Versioned<InvoiceResponse>(mapper.ToResponse(invoice), invoice.Version);
    }
}

/// <summary>The customer an invoice or invoice item is for: one that exists and was not deleted.</summary>
internal static class InvoiceCustomers
{
    public static async Task<Result<Customer>> FindAsync(ITahsilDbContext context, Guid id, CancellationToken cancellationToken) =>
        await context.Customers.AsNoTracking().SingleOrDefaultAsync(customer => customer.Id == id, cancellationToken) switch
        {
            null => Error.Validation("customer.not_found", "No customer has this ID.", "customer"),
            { AnonymizedAt: not null } => Error.Validation("customer.not_found", "The customer was deleted.", "customer"),
            var customer => customer,
        };
}
