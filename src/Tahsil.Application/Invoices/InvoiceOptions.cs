using System.Text.RegularExpressions;

namespace Tahsil.Application.Invoices;

/// <summary>The billing time zone and other settings of the billing engine.</summary>
public sealed class BillingOptions
{
    public const string SectionName = "Tahsil:Billing";

    /// <summary>
    /// The IANA time zone of calendar arithmetic, invoice numbering years and printed dates (MNY §7); Turkish merchants
    /// set <c>Europe/Istanbul</c>.
    /// </summary>
    public string TimeZone { get; set; } = "UTC";

    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    /// <summary>How long a cycle invoice stays a draft that items can join before it finalizes itself (INV-5); may be zero (INV-6).</summary>
    public TimeSpan DraftPeriod { get; set; } = TimeSpan.FromHours(1);

    internal bool HasKnownTimeZone() => TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out _);
}

public sealed partial class InvoiceOptions
{
    public const string SectionName = "Tahsil:Invoices";

    /// <summary>The first part of invoice numbers, as in <c>INV-2026-000001</c> (INV-9).</summary>
    public string NumberPrefix { get; set; } = "INV";

    /// <summary>Days from finalization to the due date of <c>send_invoice</c> invoices without their own.</summary>
    public int DefaultDaysUntilDue { get; set; } = 14;

    /// <summary>The version of the hosted invoice links' key (INV-24); a new version invalidates every link.</summary>
    public int LinkKeyVersion { get; set; } = 1;

    private static readonly TimeSpan[] DefaultReminders = [TimeSpan.FromDays(-3), TimeSpan.Zero, TimeSpan.FromDays(3), TimeSpan.FromDays(7)];

    /// <summary>
    /// When invoices sent to customers are reminded of, as offsets from the start of their due day (INV-12): 3 days
    /// before, on the day, and 3 and 7 days after when not set. Starts empty, since binding adds to a list's defaults.
    /// </summary>
    public List<TimeSpan> Reminders { get; set; } = [];

    internal IReadOnlyList<TimeSpan> ReminderOffsets => Reminders.Count > 0 ? Reminders : DefaultReminders;

    /// <summary>Bank accounts per currency for transfers, shown on hosted invoice pages (INV-26).</summary>
    public Dictionary<string, BankTransferOptions> BankTransfer { get; set; } = [];

    /// <summary>The title of hosted invoice pages per locale and status (<c>open</c>, <c>paid</c>…), replacing the defaults of INV-27.</summary>
    public Dictionary<string, Dictionary<string, string>> DocumentTitle { get; set; } = [];

    internal bool HasValidPrefix() => PrefixPattern().IsMatch(NumberPrefix);

    internal bool HasOrderedReminders() => ReminderOffsets.SequenceEqual(ReminderOffsets.Order()) && ReminderOffsets.Distinct().Count() == ReminderOffsets.Count;

    // Numbers are at most 32 characters: the prefix, the year and a sequence of six or more digits.
    [GeneratedRegex("^[A-Z0-9]{1,12}$")]
    private static partial Regex PrefixPattern();
}

/// <summary>A bank account customers can pay invoices to (INV-26).</summary>
public sealed class BankTransferOptions
{
    public string? Bank { get; set; }

    public string? AccountHolder { get; set; }

    public string? Iban { get; set; }
}

/// <summary>The row locks of billing (DOM-6, INV-8), taken in the caller's transaction.</summary>
public interface IBillingLocks
{
    /// <summary>Locks an invoice until the transaction ends; load it after this, so it is read under the lock.</summary>
    Task LockInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken);

    /// <summary>Locks a refund until the transaction ends; load it after this.</summary>
    Task LockRefundAsync(Guid refundId, CancellationToken cancellationToken);

    /// <summary>Locks a payment until the transaction ends; load it after this.</summary>
    Task LockPaymentAsync(Guid paymentId, CancellationToken cancellationToken);

    /// <summary>Locks a checkout session until the transaction ends; load it after this.</summary>
    Task LockCheckoutSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Locks a subscription until the transaction ends; load it after this. Paths that also lock one of its invoices
    /// lock the invoice first.
    /// </summary>
    Task LockSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken);

    /// <summary>Creates the customer's balance in the currency if it is missing, and locks it until the transaction ends.</summary>
    Task LockBalanceAsync(Guid customerId, string currency, CancellationToken cancellationToken);
}

/// <summary>Gapless invoice numbers (INV-9).</summary>
public interface IInvoiceNumbers
{
    /// <summary>
    /// The next sequence number of a year, taken in the caller's transaction: the year's row stays locked until it
    /// ends, and a rollback gives the number back, so numbers never repeat or skip.
    /// </summary>
    Task<long> NextAsync(int year, CancellationToken cancellationToken);
}
