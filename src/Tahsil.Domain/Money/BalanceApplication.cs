namespace Tahsil.Domain.Money;

public enum BalanceTransactionType
{
    /// <summary>A negative invoice credited the balance.</summary>
    InvoiceCredit,

    /// <summary>Credit spent on, or debt added to, an invoice.</summary>
    AppliedToInvoice,

    /// <summary>An amount below the minimum charge moved to the balance as a debt.</summary>
    Carryover,

    /// <summary>A change by an operator or application.</summary>
    Adjustment,

    /// <summary>A voided invoice gave back what its finalization took from or added to the balance.</summary>
    InvoiceVoided,
}

/// <summary>A change of the customer's balance; positive is credit for the customer.</summary>
public sealed record BalanceChange(BalanceTransactionType Type, long Amount);

/// <summary>What finalizing an invoice does with the customer's balance.</summary>
public sealed record BalanceOutcome(long AppliedBalance, long AmountDue, long BalanceAfter, IReadOnlyList<BalanceChange> Changes);

/// <summary>
/// The customer's balance at finalization (MNY-22 to MNY-24): credit is spent on the invoice, a debt joins it, a negative
/// invoice becomes credit, and a due amount too small to charge waits on the balance. The applied balance is always the
/// negated sum of the changes.
/// </summary>
public static class BalanceApplication
{
    /// <param name="balance">The customer's balance in the invoice's currency before finalization.</param>
    /// <param name="total">The invoice's total.</param>
    /// <param name="minimumCharge">The least amount the collecting connection charges, when the invoice is collected automatically.</param>
    public static BalanceOutcome Apply(long balance, long total, long? minimumCharge)
    {
        var changes = new List<BalanceChange>();
        long applied;
        if (total < 0)
        {
            applied = total;
            changes.Add(new BalanceChange(BalanceTransactionType.InvoiceCredit, checked(-total)));
        }
        else if (balance > 0)
        {
            applied = Math.Min(balance, total);
            if (applied != 0)
            {
                changes.Add(new BalanceChange(BalanceTransactionType.AppliedToInvoice, -applied));
            }
        }
        else if (balance < 0)
        {
            applied = balance;
            changes.Add(new BalanceChange(BalanceTransactionType.AppliedToInvoice, checked(-balance)));
        }
        else
        {
            applied = 0;
        }

        var due = Math.Max(checked(total - applied), 0);
        if (minimumCharge is { } minimum && due > 0 && due < minimum)
        {
            changes.Add(new BalanceChange(BalanceTransactionType.Carryover, -due));
            applied = checked(applied + due);
            due = 0;
        }

        return new BalanceOutcome(applied, due, checked(balance + changes.Sum(change => change.Amount)), changes);
    }
}
