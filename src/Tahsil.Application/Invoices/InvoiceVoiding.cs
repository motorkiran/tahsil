using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Common;
using Tahsil.Domain.Auditing;
using Tahsil.Domain.Common;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Money;

namespace Tahsil.Application.Invoices;

/// <summary>Voids invoices (INV-16, INV-18) for the API, the admin panel and the billing engine.</summary>
public sealed class InvoiceVoiding(ITahsilDbContext context, IBillingLocks locks, IAuditLog audit)
{
    /// <summary>
    /// Voids an invoice loaded under its lock, in the caller's transaction, and gives back to the customer's balance
    /// what finalization applied from it.
    /// </summary>
    public async Task<Result> VoidAsync(Invoice invoice, Actor actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var voided = invoice.Void(now);
        if (voided.IsFailure)
        {
            return voided.Error;
        }

        if (voided.Value != 0)
        {
            await locks.LockBalanceAsync(invoice.CustomerId, invoice.Currency, cancellationToken);
            var balance = await context.CustomerBalances.SingleAsync(row => row.CustomerId == invoice.CustomerId && row.Currency == invoice.Currency, cancellationToken);
            context.BalanceTransactions.Add(balance.Change(voided.Value, BalanceTransactionType.InvoiceVoided, invoice.Id, null, actor, now));
        }

        audit.Record(AuditActions.InvoiceVoided, AuditSubject.Of(AuditSubjects.Invoice, invoice.Id), InvoiceFinalizer.Details(invoice), customerId: invoice.CustomerId);
        return Result.Success();
    }
}
