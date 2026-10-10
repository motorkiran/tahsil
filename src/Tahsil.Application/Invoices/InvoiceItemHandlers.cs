using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Money;
using Model = Tahsil.Domain.Invoices;

namespace Tahsil.Application.Invoices;

/// <summary>Adds a charge or credit that waits for the customer's next manual invoice (INV §8).</summary>
public sealed class CreateInvoiceItemHandler(ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, IIdempotency idempotency, InvoiceLines lines, BillingClock clock)
{
    public async Task<Result<Versioned<InvoiceItemResponse>>> HandleAsync(CreateInvoiceItemRequest request, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.InvoicesWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        var customer = await InvoiceCustomers.FindAsync(context, request.Customer, cancellationToken);
        if (customer.IsFailure)
        {
            return customer.Error;
        }

        if (!Currency.TryGet(request.Currency, out _))
        {
            return Error.Validation("currency.unsupported", "The currency is not a supported ISO 4217 code.", "currency");
        }

        Guid? priceId = null;
        if (request.Price is { } priceKey)
        {
            var price = await lines.SellablePriceAsync(priceKey, request.Currency, "price", cancellationToken);
            if (price.IsFailure)
            {
                return price.Error;
            }

            priceId = price.Value.Id;
        }

        var quantity = InvoiceLines.Quantity(request.Quantity, whole: priceId is not null, "quantity");
        if (quantity.IsFailure)
        {
            return quantity.Error;
        }

        Guid? taxRateId = null;
        if (request.TaxRate is { } taxRateKey)
        {
            if (priceId is not null)
            {
                return Model.InvoiceErrors.InvalidLine("taxRate", "An item with a price takes its tax rate from the price.");
            }

            var rate = await lines.UsableTaxRateAsync(taxRateKey, "taxRate", cancellationToken);
            if (rate.IsFailure)
            {
                return rate.Error;
            }

            taxRateId = rate.Value.Id;
        }

        var created = Model.PendingInvoiceItem.Create(
            customer.Value.Id, request.Currency, priceId, request.Description, quantity.Value, request.UnitAmount, taxRateId, request.Discountable, clock.GetUtcNow());
        if (created.IsFailure)
        {
            return created.Error;
        }

        var item = created.Value;
        context.PendingInvoiceItems.Add(item);
        idempotency.RecordCreated(ResourceTypes.InvoiceItem, item.Id);
        audit.Record(AuditActions.InvoiceItemCreated, AuditSubject.Of(AuditSubjects.InvoiceItem, item.Id), new Dictionary<string, object?>
        {
            ["currency"] = item.Currency,
            ["priceId"] = item.PriceId,
            ["unitAmount"] = item.UnitAmount,
            ["quantity"] = item.Quantity,
        }, customerId: item.CustomerId);
        await context.SaveChangesAsync(cancellationToken);
        return new Versioned<InvoiceItemResponse>(InvoiceMapper.ToResponse(item), item.Version);
    }
}

public sealed class GetInvoiceItemHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    public async Task<Result<Versioned<InvoiceItemResponse>>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.InvoicesRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        return await context.PendingInvoiceItems.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken) is { } item
            ? new Versioned<InvoiceItemResponse>(InvoiceMapper.ToResponse(item), item.Version)
            : InvoiceItemErrors.NotFound;
    }
}

public sealed record ListInvoiceItemsQuery(Guid? Customer = null, bool? Pending = null, string? Cursor = null, int? Limit = null);

public sealed class ListInvoiceItemsHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    private static readonly Keyset<Model.PendingInvoiceItem, Guid> Newest = new("newest", item => item.Id, item => item.Id, Descending: true);

    public async Task<Result<Page<InvoiceItemResponse>>> HandleAsync(ListInvoiceItemsQuery query, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.InvoicesRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (!Paging.IsValidLimit(query.Limit ?? Paging.DefaultLimit))
        {
            return CommonErrors.InvalidLimit();
        }

        var items = context.PendingInvoiceItems.AsNoTracking();
        if (query.Customer is { } customer)
        {
            items = items.Where(item => item.CustomerId == customer);
        }

        if (query.Pending is { } pending)
        {
            items = pending ? items.Where(item => item.InvoiceId == null) : items.Where(item => item.InvoiceId != null);
        }

        var page = await Newest.ReadAsync(items, query.Cursor, query.Limit ?? Paging.DefaultLimit, cancellationToken);
        return page is null ? CommonErrors.InvalidCursor : new Page<InvoiceItemResponse>([.. page.Items.Select(InvoiceMapper.ToResponse)], page.NextCursor);
    }
}

/// <summary>Deletes an item no invoice has taken yet.</summary>
public sealed class DeleteInvoiceItemHandler(ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit)
{
    public async Task<Result> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.InvoicesWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.PendingInvoiceItems.SingleOrDefaultAsync(item => item.Id == id, cancellationToken) is not { } item)
        {
            return InvoiceItemErrors.NotFound;
        }

        if (!item.IsPending)
        {
            return InvoiceItemErrors.Invoiced;
        }

        context.PendingInvoiceItems.Remove(item);
        audit.Record(AuditActions.InvoiceItemDeleted, AuditSubject.Of(AuditSubjects.InvoiceItem, item.Id), customerId: item.CustomerId);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return InvoiceItemErrors.Invoiced;
        }

        return Result.Success();
    }
}

public static class InvoiceItemErrors
{
    public static readonly Error NotFound = Error.NotFound("invoice_item.not_found", "The invoice item does not exist.");
    public static readonly Error Invoiced = Error.Conflict("invoice_item.invoiced", "An invoice includes the item; remove its line from the draft first.");
}
