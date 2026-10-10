using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Money;

namespace Tahsil.Application.Invoices;

/// <summary>An operator's or application's change of a customer balance, which must say why (CUS-18).</summary>
public sealed class AdjustBalanceHandler(
    ITahsilDbContext context, IAuthorizer authorizer, IAuditLog audit, IIdempotency idempotency, IBillingLocks locks, IRequestContext request, BillingClock clock)
{
    public async Task<Result<BalanceTransactionResponse>> HandleAsync(Guid customerId, CreateBalanceTransactionRequest change, CancellationToken cancellationToken)
    {
        // Balances change what customers pay, so they take the invoices permission.
        if (authorizer.Require(TahsilPermissions.InvoicesWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (!await context.Customers.AnyAsync(customer => customer.Id == customerId && customer.AnonymizedAt == null, cancellationToken))
        {
            return CustomerErrors.NotFound;
        }

        if (!Currency.TryGet(change.Currency, out _))
        {
            return Error.Validation("currency.unsupported", "The currency is not a supported ISO 4217 code.", "currency");
        }

        await using var transaction = await context.BeginTransactionAsync(cancellationToken);
        await locks.LockBalanceAsync(customerId, change.Currency, cancellationToken);
        var balance = await context.CustomerBalances.SingleAsync(row => row.CustomerId == customerId && row.Currency == change.Currency, cancellationToken);
        var adjusted = balance.Adjust(change.Amount, change.Description, request.Caller.ToActor(), clock.GetUtcNow());
        if (adjusted.IsFailure)
        {
            return adjusted.Error;
        }

        var recorded = adjusted.Value;
        context.BalanceTransactions.Add(recorded);
        idempotency.RecordCreated(ResourceTypes.BalanceTransaction, recorded.Id);
        audit.Record(AuditActions.CustomerBalanceAdjusted, AuditSubject.Of(AuditSubjects.Customer, customerId), new Dictionary<string, object?>
        {
            ["currency"] = recorded.Currency,
            ["amount"] = recorded.Amount,
            ["balanceAfter"] = recorded.BalanceAfter,
        }, customerId: customerId);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return InvoiceMapper.ToResponse(recorded);
    }
}

public sealed class GetBalanceTransactionHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    public async Task<Result<BalanceTransactionResponse>> HandleAsync(Guid customerId, Guid id, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CustomersRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        return await context.BalanceTransactions.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id && row.CustomerId == customerId, cancellationToken) is { } transaction
            ? InvoiceMapper.ToResponse(transaction)
            : BalanceTransactionErrors.NotFound;
    }
}

public sealed record ListBalanceTransactionsQuery(string? Currency = null, string? Cursor = null, int? Limit = null);

/// <summary>A customer's balance history, newest first (CUS-19).</summary>
public sealed class ListBalanceTransactionsHandler(ITahsilDbContext context, IAuthorizer authorizer)
{
    private static readonly Keyset<BalanceTransaction, Guid> Newest = new("newest", row => row.Id, row => row.Id, Descending: true);

    public async Task<Result<Page<BalanceTransactionResponse>>> HandleAsync(Guid customerId, ListBalanceTransactionsQuery query, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.CustomersRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (!Paging.IsValidLimit(query.Limit ?? Paging.DefaultLimit))
        {
            return CommonErrors.InvalidLimit();
        }

        if (!await context.Customers.AnyAsync(customer => customer.Id == customerId, cancellationToken))
        {
            return CustomerErrors.NotFound;
        }

        var transactions = context.BalanceTransactions.AsNoTracking().Where(row => row.CustomerId == customerId);
        if (query.Currency is { } currency)
        {
            transactions = transactions.Where(row => row.Currency == currency);
        }

        var page = await Newest.ReadAsync(transactions, query.Cursor, query.Limit ?? Paging.DefaultLimit, cancellationToken);
        return page is null ? CommonErrors.InvalidCursor : new Page<BalanceTransactionResponse>([.. page.Items.Select(InvoiceMapper.ToResponse)], page.NextCursor);
    }
}

/// <summary>Customers with invoices or balance history are anonymized instead of deleted.</summary>
public sealed class BillingHistory(ITahsilDbContext context) : Customers.ICustomerHistory
{
    public async Task<bool> HasHistoryAsync(Guid customerId, CancellationToken cancellationToken) =>
        await context.Invoices.AnyAsync(invoice => invoice.CustomerId == customerId, cancellationToken)
        || await context.BalanceTransactions.AnyAsync(row => row.CustomerId == customerId, cancellationToken);
}

public static class BalanceTransactionErrors
{
    public static readonly Error NotFound = Error.NotFound("balance_transaction.not_found", "The balance transaction does not exist.");
}
