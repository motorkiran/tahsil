using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Money;

namespace Tahsil.Domain.Invoices;

/// <summary>The customer's billing details at finalization (CUS-6); the tax ID is kept encrypted beside it.</summary>
public sealed record CustomerSnapshot(string? Name, string? Email, CustomerType Type, string? TaxOffice, CustomerAddress? Address, string? Phone, string Locale);

/// <summary>The legal e-Arşiv or e-Fatura document the application issued for the invoice (INV-23).</summary>
public sealed record LegalInvoice(string Number, DateOnly? Date, string? Url);

/// <summary>A line as it joins a draft: what it is, its amount before discounts, and its tax rate.</summary>
public sealed record InvoiceLineDraft(
    InvoiceLineType Type,
    string Description,
    decimal Quantity,
    long Amount,
    decimal? UnitAmountDecimal = null,
    Guid? PriceId = null,
    Guid? PendingItemId = null,
    Guid? TaxRateId = null,
    TaxRateSnapshot? Tax = null,
    bool IsDiscountable = true,
    DateTimeOffset? PeriodStart = null,
    DateTimeOffset? PeriodEnd = null);

/// <summary>What finalization decided (INV-8): the number, the snapshot and what the balance did.</summary>
/// <param name="Number">The invoice number (INV-9).</param>
/// <param name="Snapshot">The customer's billing details.</param>
/// <param name="EncryptTaxId">Encrypts the customer's tax ID for this invoice, when there is one.</param>
/// <param name="Balance">The balance application (MNY-22, MNY-23).</param>
/// <param name="DueDate">For <see cref="CollectionMethod.SendInvoice"/>: the end of the due day.</param>
public sealed record InvoiceFinalization(string Number, CustomerSnapshot Snapshot, Func<Guid, byte[]>? EncryptTaxId, BalanceOutcome Balance, DateTimeOffset? DueDate);

/// <summary>
/// What a customer owes, why, with discounts and tax (INV §2). A draft's lines and terms change freely and its totals
/// follow them (INV-4); finalization freezes everything but the amounts paid and refunded (DOM-14), and <c>paid</c>
/// and <c>void</c> are terminal (DOM-15).
/// </summary>
public sealed class Invoice : AggregateRoot
{
    public const int NumberMaxLength = 32;
    public const int MemoMaxLength = 1000;
    public const int MaxDaysUntilDue = 365;

    private readonly List<InvoiceLine> _lines = [];

    private Invoice()
    {
    }

    public Guid Id { get; private init; }

    public string? Number { get; private set; }

    public Guid CustomerId { get; private init; }

    public Guid? SubscriptionId { get; private init; }

    public InvoiceStatus Status { get; private set; }

    public BillingReason BillingReason { get; private init; }

    public CollectionMethod CollectionMethod { get; private set; }

    public int? DaysUntilDue { get; private set; }

    public string Currency { get; private init; } = string.Empty;

    public DateTimeOffset? PeriodStart { get; private set; }

    public DateTimeOffset? PeriodEnd { get; private set; }

    public long Subtotal { get; private set; }

    public long DiscountTotal { get; private set; }

    public long TaxTotal { get; private set; }

    public long TotalExcludingTax { get; private set; }

    public long Total { get; private set; }

    /// <summary>The balance applied at finalization: positive when credit was spent, negative when a debt joined.</summary>
    public long AppliedBalance { get; private set; }

    public long AmountDue { get; private set; }

    public long AmountPaid { get; private set; }

    public long AmountRemaining { get; private set; }

    public long AmountRefunded { get; private set; }

    public DateTimeOffset? DueDate { get; private set; }

    public bool AutoAdvance { get; private set; }

    public DateTimeOffset? FinalizeAt { get; private set; }

    public DateTimeOffset? NextPaymentAttemptAt { get; private set; }

    public int AttemptCount { get; private set; }

    public CustomerSnapshot? CustomerSnapshot { get; private set; }

    /// <summary>The snapshot's tax ID, encrypted for this invoice.</summary>
    public byte[]? CustomerTaxId { get; private set; }

    public string? LegalInvoiceNumber { get; private set; }

    public DateOnly? LegalInvoiceDate { get; private set; }

    public string? LegalInvoiceUrl { get; private set; }

    public LegalInvoice? LegalInvoice => LegalInvoiceNumber is { } number ? new LegalInvoice(number, LegalInvoiceDate, LegalInvoiceUrl) : null;

    public string? Memo { get; private set; }

    public IReadOnlyDictionary<string, string> Metadata { get; private set; } = Common.Metadata.Empty;

    public DateTimeOffset? FinalizedAt { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    public DateTimeOffset? VoidedAt { get; private set; }

    public DateTimeOffset? MarkedUncollectibleAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<InvoiceLine> Lines => _lines;

    public bool IsDraft => Status == InvoiceStatus.Draft;

    public static Result<Invoice> CreateDraft(
        Guid customerId,
        string currency,
        BillingReason billingReason,
        CollectionMethod collectionMethod,
        int? daysUntilDue,
        string? memo,
        bool autoAdvance,
        IReadOnlyDictionary<string, string> metadata,
        DateTimeOffset now)
    {
        if (ValidateTerms(memo, daysUntilDue) is { } invalid)
        {
            return invalid;
        }

        return new Invoice
        {
            Id = Guid.CreateVersion7(now),
            CustomerId = customerId,
            Currency = currency,
            Status = InvoiceStatus.Draft,
            BillingReason = billingReason,
            CollectionMethod = collectionMethod,
            DaysUntilDue = collectionMethod == CollectionMethod.SendInvoice ? daysUntilDue : null,
            Memo = Text.Normalize(memo),
            AutoAdvance = autoAdvance,
            Metadata = metadata,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Adds lines to a draft and recalculates it.</summary>
    public Result<IReadOnlyList<InvoiceLine>> AddLines(IReadOnlyList<InvoiceLineDraft> drafts, bool taxExempt, DateTimeOffset now)
    {
        if (!IsDraft)
        {
            return InvoiceErrors.NotDraft;
        }

        var order = _lines.Count == 0 ? 0 : _lines.Max(line => line.SortOrder) + 1;
        var added = drafts.Select(draft => InvoiceLine.Create(Id, draft, order++, now)).ToList();
        _lines.AddRange(added);
        Recalculate(taxExempt, now);
        return added;
    }

    /// <summary>Removes a line from a draft; returns the pending item it came from, which is free again.</summary>
    public Result<Guid?> RemoveLine(Guid lineId, bool taxExempt, DateTimeOffset now)
    {
        if (!IsDraft)
        {
            return InvoiceErrors.NotDraft;
        }

        if (_lines.FirstOrDefault(line => line.Id == lineId) is not { } line)
        {
            return InvoiceErrors.LineNotFound;
        }

        _lines.Remove(line);
        Recalculate(taxExempt, now);
        return line.PendingItemId;
    }

    public Result UpdateDraft(CollectionMethod collectionMethod, int? daysUntilDue, string? memo, bool autoAdvance, DateTimeOffset now)
    {
        if (!IsDraft)
        {
            return InvoiceErrors.NotDraft;
        }

        if (ValidateTerms(memo, daysUntilDue) is { } invalid)
        {
            return invalid;
        }

        CollectionMethod = collectionMethod;
        DaysUntilDue = collectionMethod == CollectionMethod.SendInvoice ? daysUntilDue : null;
        Memo = Text.Normalize(memo);
        AutoAdvance = autoAdvance;
        UpdatedAt = now;
        return Result.Success();
    }

    public void UpdateMetadata(IReadOnlyDictionary<string, string> metadata, DateTimeOffset now)
    {
        Metadata = metadata;
        UpdatedAt = now;
    }

    /// <summary>Records or corrects the legal document of a finalized invoice that was not voided (INV-23).</summary>
    public Result SetLegalInvoice(LegalInvoice? legalInvoice, DateTimeOffset now)
    {
        if (Status is not (InvoiceStatus.Open or InvoiceStatus.Paid or InvoiceStatus.Uncollectible))
        {
            return InvoiceErrors.InvalidState(Status, "given a legal invoice");
        }

        if (legalInvoice is not null
            && (!Text.FitsRequired(legalInvoice.Number, 64)
                || (legalInvoice.Url is { } url && (url.Length > 2000 || !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps))))
        {
            return InvoiceErrors.InvalidLegalInvoice;
        }

        LegalInvoiceNumber = legalInvoice?.Number.Trim();
        LegalInvoiceDate = legalInvoice?.Date;
        LegalInvoiceUrl = legalInvoice?.Url;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// The calculation pipeline of a draft (INV-4, steps 1 to 4): line amounts are given, discounts come with coupons,
    /// then tax per line and the totals as sums of the rounded lines (MNY-21).
    /// </summary>
    public void Recalculate(bool taxExempt, DateTimeOffset now)
    {
        if (!IsDraft)
        {
            throw new InvalidOperationException("A finalized invoice never changes its amounts (DOM-14).");
        }

        var amounts = _lines.Select(line => line.Calculate(taxExempt)).ToList();
        var totals = InvoiceTotals.Of(amounts);
        Subtotal = totals.Subtotal;
        DiscountTotal = totals.DiscountTotal;
        TaxTotal = totals.TaxTotal;
        TotalExcludingTax = totals.TotalExcludingTax;
        Total = totals.Total;
        AmountDue = Math.Max(Total, 0);
        AmountRemaining = AmountDue;
        UpdatedAt = now;
    }

    /// <summary>Checks that a draft can be finalized: it has lines, and a manual invoice's total is not zero (INV-8).</summary>
    public Result CanFinalize()
    {
        if (!IsDraft)
        {
            return InvoiceErrors.InvalidState(Status, "finalized");
        }

        if (_lines.Count == 0)
        {
            return InvoiceErrors.Empty;
        }

        return Total == 0 && BillingReason is not (BillingReason.SubscriptionCreate or BillingReason.SubscriptionCycle or BillingReason.SubscriptionUpdate or BillingReason.SubscriptionCancel)
            ? InvoiceErrors.ZeroTotal
            : Result.Success();
    }

    /// <summary>Freezes the draft (INV-8, steps 3 to 7); the caller recalculated it and holds the locks.</summary>
    public Result Finalize(InvoiceFinalization finalization, DateTimeOffset now)
    {
        if (CanFinalize() is { IsFailure: true } invalid)
        {
            return invalid;
        }

        Number = finalization.Number;
        CustomerSnapshot = finalization.Snapshot;
        CustomerTaxId = finalization.EncryptTaxId?.Invoke(Id);
        AppliedBalance = finalization.Balance.AppliedBalance;
        AmountDue = finalization.Balance.AmountDue;
        AmountRemaining = checked(AmountDue - AmountPaid);
        FinalizedAt = now;
        FinalizeAt = null;
        if (AmountDue == 0)
        {
            Status = InvoiceStatus.Paid;
            PaidAt = now;
        }
        else
        {
            Status = InvoiceStatus.Open;
            NextPaymentAttemptAt = CollectionMethod == CollectionMethod.ChargeAutomatically ? now : null;
            DueDate = CollectionMethod == CollectionMethod.SendInvoice ? finalization.DueDate : null;
        }

        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Voids an open or uncollectible invoice without payments (INV-16, INV-18). Returns what goes back to the
    /// customer's balance: the opposite of what finalization did to it.
    /// </summary>
    public Result<long> Void(DateTimeOffset now)
    {
        if (Status is not (InvoiceStatus.Open or InvoiceStatus.Uncollectible))
        {
            return InvoiceErrors.InvalidState(Status, "voided");
        }

        if (AmountPaid != 0)
        {
            return InvoiceErrors.HasPayments;
        }

        Status = InvoiceStatus.Void;
        VoidedAt = now;
        NextPaymentAttemptAt = null;
        UpdatedAt = now;
        return AppliedBalance;
    }

    /// <summary>Whether a payment of this amount is still needed, so a success can be applied to the invoice.</summary>
    public bool NeedsPayment(long amount) => Status is InvoiceStatus.Open or InvoiceStatus.Uncollectible && amount > 0 && amount <= AmountRemaining;

    /// <summary>A payment reduced what is left to pay; at zero the invoice is paid (INV-15).</summary>
    public Result ApplyPayment(long amount, DateTimeOffset now)
    {
        if (!NeedsPayment(amount))
        {
            return InvoiceErrors.Overpayment;
        }

        AmountPaid = checked(AmountPaid + amount);
        AmountRemaining = checked(AmountRemaining - amount);
        if (AmountRemaining == 0)
        {
            Status = InvoiceStatus.Paid;
            PaidAt = now;
            NextPaymentAttemptAt = null;
        }

        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Stops collecting an open invoice; the debt stays payable (INV-17).</summary>
    public Result MarkUncollectible(DateTimeOffset now)
    {
        if (Status != InvoiceStatus.Open)
        {
            return InvoiceErrors.InvalidState(Status, "marked uncollectible");
        }

        Status = InvoiceStatus.Uncollectible;
        MarkedUncollectibleAt = now;
        NextPaymentAttemptAt = null;
        UpdatedAt = now;
        return Result.Success();
    }

    private static Error? ValidateTerms(string? memo, int? daysUntilDue) =>
        !Text.FitsOptional(memo, MemoMaxLength) ? InvoiceErrors.InvalidMemo
        : daysUntilDue is < 0 or > MaxDaysUntilDue ? InvoiceErrors.InvalidDaysUntilDue
        : null;
}

public sealed class InvoiceLine
{
    public const int DescriptionMaxLength = 500;

    private InvoiceLine()
    {
    }

    public Guid Id { get; private init; }

    public Guid InvoiceId { get; private init; }

    public InvoiceLineType Type { get; private init; }

    public Guid? PriceId { get; private init; }

    public Guid? PendingItemId { get; private init; }

    public string Description { get; private init; } = string.Empty;

    public decimal Quantity { get; private init; }

    public decimal? UnitAmountDecimal { get; private init; }

    /// <summary>Before discounts; negative for credits.</summary>
    public long Amount { get; private init; }

    public bool IsDiscountable { get; private init; }

    public long DiscountAmount { get; private set; }

    public Guid? TaxRateId { get; private init; }

    public decimal? TaxPercentage { get; private init; }

    public bool? TaxInclusive { get; private init; }

    public long TaxAmount { get; private set; }

    /// <summary><c>amount − discount + exclusive tax</c>.</summary>
    public long Total { get; private set; }

    public DateTimeOffset? PeriodStart { get; private init; }

    public DateTimeOffset? PeriodEnd { get; private init; }

    public int SortOrder { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    internal static InvoiceLine Create(Guid invoiceId, InvoiceLineDraft draft, int sortOrder, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        InvoiceId = invoiceId,
        Type = draft.Type,
        PriceId = draft.PriceId,
        PendingItemId = draft.PendingItemId,
        Description = draft.Description.Trim(),
        Quantity = draft.Quantity,
        UnitAmountDecimal = draft.UnitAmountDecimal,
        Amount = draft.Amount,
        IsDiscountable = draft.IsDiscountable,
        TaxRateId = draft.TaxRateId,
        TaxPercentage = draft.Tax?.Percentage,
        TaxInclusive = draft.Tax?.Inclusive,
        PeriodStart = draft.PeriodStart,
        PeriodEnd = draft.PeriodEnd,
        SortOrder = sortOrder,
        CreatedAt = now,
    };

    internal LineAmounts Calculate(bool taxExempt)
    {
        var rate = TaxPercentage is { } percentage ? new TaxRateSnapshot(percentage, TaxInclusive == true) : null;
        var tax = LineArithmetic.Tax(Amount, DiscountAmount, rate, taxExempt);
        TaxAmount = tax.Tax;
        Total = tax.Total;
        return new LineAmounts(Amount, DiscountAmount, tax, rate?.Inclusive == true && !taxExempt);
    }
}
