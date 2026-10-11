using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Model = Tahsil.Domain.Invoices;

namespace Tahsil.Application.Invoices;

public sealed class InvoiceMapper(ISecretEncryption encryption, InvoiceLinks links)
{
    public InvoiceResponse ToResponse(Model.Invoice invoice) => new()
    {
        Id = invoice.Id,
        HostedInvoiceUrl = links.UrlFor(invoice),
        Number = invoice.Number,
        CustomerId = invoice.CustomerId,
        SubscriptionId = invoice.SubscriptionId,
        Status = invoice.Status.Map<InvoiceStatus>(),
        BillingReason = invoice.BillingReason.Map<BillingReason>(),
        CollectionMethod = invoice.CollectionMethod.Map<CollectionMethod>(),
        DaysUntilDue = invoice.DaysUntilDue,
        Currency = invoice.Currency,
        PeriodStart = invoice.PeriodStart,
        PeriodEnd = invoice.PeriodEnd,
        Subtotal = invoice.Subtotal,
        DiscountTotal = invoice.DiscountTotal,
        TaxTotal = invoice.TaxTotal,
        TotalExcludingTax = invoice.TotalExcludingTax,
        Total = invoice.Total,
        AppliedBalance = invoice.AppliedBalance,
        AmountDue = invoice.AmountDue,
        AmountPaid = invoice.AmountPaid,
        AmountRemaining = invoice.AmountRemaining,
        AmountRefunded = invoice.AmountRefunded,
        DueDate = invoice.DueDate,
        AutoAdvance = invoice.AutoAdvance,
        FinalizeAt = invoice.FinalizeAt,
        NextPaymentAttemptAt = invoice.NextPaymentAttemptAt,
        AttemptCount = invoice.AttemptCount,
        CustomerSnapshot = invoice.CustomerSnapshot is { } snapshot
            ? new CustomerSnapshotResponse(
                snapshot.Name,
                snapshot.Email,
                snapshot.Type.Map<CustomerType>(),
                invoice.CustomerTaxId is { } taxId ? encryption.DecryptString(taxId, SecretPurposes.CustomerTaxId, invoice.Id) : null,
                snapshot.TaxOffice,
                snapshot.Address is { } address ? new CustomerAddress(address.Line1, address.Line2, address.District, address.City, address.PostalCode, address.Country) : null,
                snapshot.Phone,
                snapshot.Locale)
            : null,
        LegalInvoice = invoice.LegalInvoice is { } legal ? new LegalInvoiceData(legal.Number, legal.Date, legal.Url) : null,
        Memo = invoice.Memo,
        Metadata = invoice.Metadata,
        Lines = LinesOf(invoice),
        FinalizedAt = invoice.FinalizedAt,
        PaidAt = invoice.PaidAt,
        VoidedAt = invoice.VoidedAt,
        MarkedUncollectibleAt = invoice.MarkedUncollectibleAt,
        CreatedAt = invoice.CreatedAt,
        UpdatedAt = invoice.UpdatedAt,
    };

    public static List<InvoiceLineResponse> LinesOf(Model.Invoice invoice) =>
    [
        .. invoice.Lines.OrderBy(line => line.SortOrder).Select(line => new InvoiceLineResponse(
            line.Id,
            line.Type.Map<InvoiceLineType>(),
            line.Description,
            line.Quantity,
            line.UnitAmountDecimal,
            line.Amount,
            line.DiscountAmount,
            line.TaxAmount,
            line.Total,
            line.PriceId,
            line.PendingItemId,
            line.TaxRateId,
            line.TaxPercentage,
            line.TaxInclusive,
            line.PeriodStart,
            line.PeriodEnd)),
    ];

    /// <summary>A draft built in memory and never stored, with the balance its finalization would apply now (SUB-24).</summary>
    public static InvoicePreviewResponse Preview(Model.Invoice draft, Domain.Money.BalanceOutcome balance) => new()
    {
        BillingReason = draft.BillingReason.Map<BillingReason>(),
        Currency = draft.Currency,
        PeriodStart = draft.PeriodStart,
        PeriodEnd = draft.PeriodEnd,
        Lines = LinesOf(draft),
        Subtotal = draft.Subtotal,
        DiscountTotal = draft.DiscountTotal,
        TaxTotal = draft.TaxTotal,
        TotalExcludingTax = draft.TotalExcludingTax,
        Total = draft.Total,
        AppliedBalance = balance.AppliedBalance,
        AmountDue = balance.AmountDue,
    };

    public static InvoiceItemResponse ToResponse(Model.PendingInvoiceItem item) => new(
        item.Id,
        item.CustomerId,
        item.SubscriptionId,
        item.Currency,
        item.PriceId,
        item.Description,
        item.Quantity,
        item.UnitAmount,
        item.TaxRateId,
        item.IsDiscountable,
        item.IsProration,
        item.PeriodStart,
        item.PeriodEnd,
        item.InvoiceId,
        item.CreatedAt);

    public static BalanceTransactionResponse ToResponse(Domain.Customers.BalanceTransaction transaction) => new(
        transaction.Id,
        transaction.CustomerId,
        transaction.Currency,
        transaction.Amount,
        transaction.Type.Map<BalanceTransactionType>(),
        transaction.InvoiceId,
        transaction.Description,
        transaction.BalanceAfter,
        transaction.Actor.ToResponse(),
        transaction.CreatedAt);
}

internal static class InvoiceQueries
{
    /// <summary>
    /// The order pending items join an invoice: as they were created, and the credits of a change before its charges,
    /// since the items of one change share their creation time.
    /// </summary>
    public static IOrderedQueryable<Model.PendingInvoiceItem> InJoiningOrder(this IQueryable<Model.PendingInvoiceItem> items) =>
        items.OrderBy(item => item.CreatedAt).ThenBy(item => item.UnitAmount).ThenBy(item => item.Id);

    public static Task<Model.Invoice?> FindInvoiceAsync(this ITahsilDbContext context, Guid id, CancellationToken cancellationToken) =>
        context.Invoices.Include(invoice => invoice.Lines).SingleOrDefaultAsync(invoice => invoice.Id == id, cancellationToken);

    /// <summary>A customer's balances, by currency (CUS-19).</summary>
    public static async Task<IReadOnlyList<CustomerBalanceResponse>> BalancesOfAsync(this ITahsilDbContext context, Guid customerId, CancellationToken cancellationToken) =>
        await context.CustomerBalances.AsNoTracking()
            .Where(balance => balance.CustomerId == customerId)
            .OrderBy(balance => balance.Currency)
            .Select(balance => new CustomerBalanceResponse(balance.Currency, balance.Amount))
            .ToListAsync(cancellationToken);
}
