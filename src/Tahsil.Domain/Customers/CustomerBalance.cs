using Tahsil.Domain.Common;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Money;

namespace Tahsil.Domain.Customers;

/// <summary>
/// A customer's balance in one currency (CUS-17): positive is credit to spend, negative is owed and joins the next
/// invoice. Every change writes exactly one transaction with the balance after it (DOM-6).
/// </summary>
public sealed class CustomerBalance
{
    public const int DescriptionMaxLength = 500;

    private CustomerBalance()
    {
    }

    public Guid CustomerId { get; private init; }

    public string Currency { get; private init; } = string.Empty;

    public long Amount { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static CustomerBalance Create(Guid customerId, string currency, DateTimeOffset now) =>
        new() { CustomerId = customerId, Currency = currency, UpdatedAt = now };

    /// <summary>An operator's or application's adjustment, which must say why (CUS-18).</summary>
    public Result<BalanceTransaction> Adjust(long amount, string? description, Actor actor, DateTimeOffset now)
    {
        if (amount == 0)
        {
            return BalanceErrors.InvalidAmount;
        }

        return Text.FitsRequired(description, DescriptionMaxLength)
            ? Change(amount, BalanceTransactionType.Adjustment, null, description, actor, now)
            : BalanceErrors.InvalidDescription;
    }

    public BalanceTransaction Change(long amount, BalanceTransactionType type, Guid? invoiceId, string? description, Actor actor, DateTimeOffset now)
    {
        Amount = checked(Amount + amount);
        UpdatedAt = now;
        return new BalanceTransaction
        {
            Id = Guid.CreateVersion7(now),
            CustomerId = CustomerId,
            Currency = Currency,
            Amount = amount,
            Type = type,
            InvoiceId = invoiceId,
            Description = Text.Normalize(description),
            BalanceAfter = Amount,
            Actor = actor,
            CreatedAt = now,
        };
    }
}

/// <summary>One change of a customer balance, in its append-only history.</summary>
public sealed class BalanceTransaction
{
    public Guid Id { get; init; }

    public Guid CustomerId { get; init; }

    public string Currency { get; init; } = string.Empty;

    /// <summary>The signed change; positive is credit for the customer.</summary>
    public long Amount { get; init; }

    public BalanceTransactionType Type { get; init; }

    public Guid? InvoiceId { get; init; }

    public string? Description { get; init; }

    public long BalanceAfter { get; init; }

    public Actor Actor { get; init; } = Actor.System;

    public DateTimeOffset CreatedAt { get; init; }
}
