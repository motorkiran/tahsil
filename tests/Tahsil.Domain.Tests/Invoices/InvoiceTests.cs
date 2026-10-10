using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Invoices;
using Tahsil.Domain.Money;

namespace Tahsil.Domain.Tests.Invoices;

public sealed class InvoiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly TaxRateSnapshot Kdv20Inclusive = new(20, true);
    private static readonly TaxRateSnapshot Kdv20Exclusive = new(20, false);

    [Fact]
    [Trait("Requirement", "INV-4")]
    public void DraftTotals_AreSumsOfRoundedLines_WithTaxPerLine()
    {
        var invoice = Draft();

        invoice.AddLines(
            [
                new InvoiceLineDraft(InvoiceLineType.OneTime, "Pro × 1", 1, 29_900, Tax: Kdv20Inclusive),
                new InvoiceLineDraft(InvoiceLineType.Item, "Setup", 1, 10_000, Tax: Kdv20Exclusive),
                new InvoiceLineDraft(InvoiceLineType.Item, "Goodwill", 1, -1_000),
            ],
            taxExempt: false,
            Now).IsSuccess.ShouldBeTrue();

        (invoice.Subtotal, invoice.TaxTotal, invoice.Total, invoice.TotalExcludingTax).ShouldBe((38_900, 4_983 + 2_000, 40_900, 33_917));
        invoice.Lines.Select(line => (line.TaxAmount, line.Total)).ShouldBe([(4_983L, 29_900L), (2_000L, 12_000L), (0L, -1_000L)]);
        invoice.AmountDue.ShouldBe(40_900);
    }

    [Fact]
    [Trait("Requirement", "INV-4")]
    public void ExemptCustomers_PayNoTax_AndAmountsStayAsPriced()
    {
        var invoice = Draft();
        invoice.AddLines([new InvoiceLineDraft(InvoiceLineType.Item, "Setup", 1, 10_000, Tax: Kdv20Exclusive)], taxExempt: false, Now);

        invoice.Recalculate(taxExempt: true, Now);

        (invoice.TaxTotal, invoice.Total).ShouldBe((0, 10_000));
    }

    [Theory]
    [Trait("Requirement", "INV-8")]
    [InlineData(29_900, 0, InvoiceStatus.Open)]
    [InlineData(29_900, 29_900, InvoiceStatus.Paid)]
    [InlineData(-15_000, -15_000, InvoiceStatus.Paid)]
    public void Finalizing_OpensTheInvoice_OrPaysItWhenNothingIsDue(long total, long applied, InvoiceStatus status)
    {
        var invoice = Draft();
        invoice.AddLines([new InvoiceLineDraft(InvoiceLineType.Item, "Line", 1, total)], false, Now);

        var outcome = BalanceApplication.Apply(applied > 0 ? applied : 0, total, null);
        invoice.Finalize(Finalization(outcome), Now).IsSuccess.ShouldBeTrue();

        invoice.Status.ShouldBe(status);
        invoice.Number.ShouldBe("INV-2026-000001");
        invoice.AppliedBalance.ShouldBe(applied);
        invoice.NextPaymentAttemptAt.ShouldBe(status == InvoiceStatus.Open ? Now : null);
        invoice.PaidAt.ShouldBe(status == InvoiceStatus.Paid ? Now : null);
    }

    [Fact]
    [Trait("Requirement", "INV-8")]
    public void SendInvoice_GetsADueDate_InsteadOfAPaymentAttempt()
    {
        var invoice = Invoice.CreateDraft(Guid.CreateVersion7(), "TRY", BillingReason.Manual, CollectionMethod.SendInvoice, 14, null, false, Metadata.Empty, Now).Value;
        invoice.AddLines([new InvoiceLineDraft(InvoiceLineType.Item, "Line", 1, 5_000)], false, Now);
        var due = Now.AddDays(15);

        invoice.Finalize(Finalization(BalanceApplication.Apply(0, 5_000, null)) with { DueDate = due }, Now);

        (invoice.Status, invoice.DueDate, invoice.NextPaymentAttemptAt).ShouldBe((InvoiceStatus.Open, due, null));
    }

    [Fact]
    [Trait("Requirement", "INV-8")]
    public void EmptyDrafts_AndManualDraftsTotallingZero_CannotBeFinalized()
    {
        var empty = Draft();
        var zero = Draft();
        zero.AddLines([new InvoiceLineDraft(InvoiceLineType.Item, "A", 1, 100), new InvoiceLineDraft(InvoiceLineType.Item, "B", 1, -100)], false, Now);

        empty.CanFinalize().Error.ShouldBe(InvoiceErrors.Empty);
        zero.CanFinalize().Error.ShouldBe(InvoiceErrors.ZeroTotal);
    }

    [Fact]
    [Trait("Requirement", "DOM-14")]
    public void FinalizedInvoices_NeverChangeTheirLinesOrAmounts()
    {
        var invoice = Finalized(29_900);

        invoice.AddLines([new InvoiceLineDraft(InvoiceLineType.Item, "Late", 1, 100)], false, Now).Error.ShouldBe(InvoiceErrors.NotDraft);
        invoice.RemoveLine(invoice.Lines[0].Id, false, Now).Error.ShouldBe(InvoiceErrors.NotDraft);
        invoice.UpdateDraft(CollectionMethod.SendInvoice, 7, "memo", false, Now).Error.ShouldBe(InvoiceErrors.NotDraft);
        Should.Throw<InvalidOperationException>(() => invoice.Recalculate(true, Now));
        invoice.Lines.ShouldHaveSingleItem().Amount.ShouldBe(29_900);
    }

    [Fact]
    [Trait("Requirement", "DOM-15")]
    public void PaidAndVoidInvoices_AreTerminal()
    {
        var paid = Finalized(29_900, credit: 29_900);
        var open = Finalized(29_900);

        paid.Void(Now).Error!.Code.ShouldBe("invoice.invalid_state");
        paid.MarkUncollectible(Now).Error!.Code.ShouldBe("invoice.invalid_state");
        open.MarkUncollectible(Now).IsSuccess.ShouldBeTrue();
        open.Void(Now).IsSuccess.ShouldBeTrue();
        open.Void(Now).Error!.Code.ShouldBe("invoice.invalid_state");
        open.MarkUncollectible(Now).Error!.Code.ShouldBe("invoice.invalid_state");
    }

    [Theory]
    [Trait("Requirement", "INV-16")]
    [InlineData(5_000, 29_900, 5_000)]
    [InlineData(-1_200, 29_900, -1_200)]
    [InlineData(0, 29_900, 0)]
    public void Voiding_GivesBackWhatFinalizationDidToTheBalance(long balance, long total, long givenBack)
    {
        var invoice = Draft();
        invoice.AddLines([new InvoiceLineDraft(InvoiceLineType.Item, "Line", 1, total)], false, Now);
        var outcome = BalanceApplication.Apply(balance, total, null);
        invoice.Finalize(Finalization(outcome), Now);

        var reversal = invoice.Void(Now).Value;

        reversal.ShouldBe(givenBack);
        checked(outcome.BalanceAfter + reversal).ShouldBe(balance);
    }

    [Fact]
    [Trait("Requirement", "DOM-6")]
    public void EveryBalanceChange_RecordsTheBalanceAfterIt()
    {
        var balance = CustomerBalance.Create(Guid.CreateVersion7(), "TRY", Now);

        var credit = balance.Adjust(5_000, "Goodwill credit", Actor.System, Now).Value;
        var applied = balance.Change(-5_000, BalanceTransactionType.AppliedToInvoice, Guid.CreateVersion7(), null, Actor.System, Now);

        (credit.Amount, credit.BalanceAfter, credit.Type).ShouldBe((5_000, 5_000, BalanceTransactionType.Adjustment));
        (applied.BalanceAfter, balance.Amount).ShouldBe((0, 0));
        balance.Adjust(100, " ", Actor.System, Now).Error.ShouldBe(BalanceErrors.InvalidDescription);
        balance.Adjust(0, "Nothing", Actor.System, Now).Error.ShouldBe(BalanceErrors.InvalidAmount);
    }

    [Fact]
    public void PendingItems_JoinOneInvoice_AndAreFreedByDrafts()
    {
        var item = PendingInvoiceItem.Create(Guid.CreateVersion7(), "TRY", null, "Extra seats", 2, 1_500, null, true, Now).Value;
        var invoiceId = Guid.CreateVersion7();

        item.IncludeIn(invoiceId, Now);
        Should.Throw<InvalidOperationException>(() => item.IncludeIn(Guid.CreateVersion7(), Now));
        item.Release(Now);

        item.IsPending.ShouldBeTrue();
        PendingInvoiceItem.Create(Guid.CreateVersion7(), "TRY", null, null, 1, 100, null, true, Now).Error!.Code.ShouldBe("invoice.invalid_line");
        PendingInvoiceItem.Create(Guid.CreateVersion7(), "TRY", null, "Half", 0, 100, null, true, Now).Error!.Code.ShouldBe("invoice.invalid_line");
    }

    private static Invoice Draft() =>
        Invoice.CreateDraft(Guid.CreateVersion7(), "TRY", BillingReason.Manual, CollectionMethod.ChargeAutomatically, null, null, false, Metadata.Empty, Now).Value;

    private static Invoice Finalized(long total, long credit = 0)
    {
        var invoice = Draft();
        invoice.AddLines([new InvoiceLineDraft(InvoiceLineType.Item, "Line", 1, total)], false, Now);
        invoice.Finalize(Finalization(BalanceApplication.Apply(credit, total, null)), Now);
        return invoice;
    }

    private static InvoiceFinalization Finalization(BalanceOutcome outcome) =>
        new("INV-2026-000001", new CustomerSnapshot("Ayşe Yılmaz", "ayse@example.com", CustomerType.Individual, null, null, null, "tr"), null, outcome, null);
}
