using Microsoft.EntityFrameworkCore;
using Tahsil.Application.Invoices;
using Tahsil.Infrastructure.Persistence;

namespace Tahsil.Infrastructure.Billing;

/// <summary>Row locks and invoice numbers in PostgreSQL, in the transaction of the scope's database context.</summary>
internal sealed class BillingLocks(TahsilDbContext context) : IBillingLocks, IInvoiceNumbers
{
    public async Task LockInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken) =>
        await context.Database.ExecuteSqlAsync($"SELECT 1 FROM tahsil.invoices WHERE id = {invoiceId} FOR UPDATE", cancellationToken);

    public async Task LockCouponAsync(Guid couponId, CancellationToken cancellationToken) =>
        await context.Database.ExecuteSqlAsync($"SELECT 1 FROM tahsil.coupons WHERE id = {couponId} FOR UPDATE", cancellationToken);

    public async Task LockPromotionCodeAsync(Guid promotionCodeId, CancellationToken cancellationToken) =>
        await context.Database.ExecuteSqlAsync($"SELECT 1 FROM tahsil.promotion_codes WHERE id = {promotionCodeId} FOR UPDATE", cancellationToken);

    public async Task LockRefundAsync(Guid refundId, CancellationToken cancellationToken) =>
        await context.Database.ExecuteSqlAsync($"SELECT 1 FROM tahsil.refunds WHERE id = {refundId} FOR UPDATE", cancellationToken);

    public async Task LockPaymentAsync(Guid paymentId, CancellationToken cancellationToken) =>
        await context.Database.ExecuteSqlAsync($"SELECT 1 FROM tahsil.payments WHERE id = {paymentId} FOR UPDATE", cancellationToken);

    public async Task LockCheckoutSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        await context.Database.ExecuteSqlAsync($"SELECT 1 FROM tahsil.checkout_sessions WHERE id = {sessionId} FOR UPDATE", cancellationToken);

    public async Task LockSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken) =>
        await context.Database.ExecuteSqlAsync($"SELECT 1 FROM tahsil.subscriptions WHERE id = {subscriptionId} FOR UPDATE", cancellationToken);

    public async Task LockBalanceAsync(Guid customerId, string currency, CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlAsync(
            $"INSERT INTO tahsil.customer_balances (customer_id, currency, amount, updated_at) VALUES ({customerId}, {currency}, 0, now()) ON CONFLICT DO NOTHING",
            cancellationToken);
        await context.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM tahsil.customer_balances WHERE customer_id = {customerId} AND currency = {currency} FOR UPDATE", cancellationToken);
    }

    public async Task<long> NextAsync(int year, CancellationToken cancellationToken)
    {
        var numbers = await context.Database.SqlQuery<long>($"""
            INSERT INTO tahsil.invoice_number_sequences (year, next_value) VALUES ({(short)year}, 2)
            ON CONFLICT (year) DO UPDATE SET next_value = invoice_number_sequences.next_value + 1
            RETURNING next_value - 1 AS "Value"
            """).ToListAsync(cancellationToken);
        return numbers.Single();
    }
}
