using Tahsil.Domain.Common;
using Tahsil.Domain.Coupons;
using Tahsil.Domain.Invoices;

namespace Tahsil.Domain.Tests.Coupons;

public sealed class DiscountTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Pro = Guid.CreateVersion7();
    private static readonly Guid Addon = Guid.CreateVersion7();

    [Fact]
    [Trait("Requirement", "MNY-18")]
    [Trait("Requirement", "CPN-8")]
    public void APercentage_DiscountsTheDiscountableLinesOfItsProducts_WithTheirSigns()
    {
        var invoice = Draft(Discounted(percentOff: 15m, products: [Pro]));

        invoice.AddLines(
        [
            Line(29_900, Pro),
            Line(-15_000, Pro),
            Line(10_000, Addon),
            Line(5_000, Pro) with { IsDiscountable = false },
            Line(2_000, product: null),
        ], taxExempt: false, Now);

        invoice.Lines.Select(line => line.DiscountAmount).ShouldBe([4_485L, -2_250L, 0L, 0L, 0L]);
        (invoice.Subtotal, invoice.DiscountTotal, invoice.Total).ShouldBe((31_900L, 2_235L, 29_665L));
    }

    [Theory]
    [Trait("Requirement", "MNY-19")]
    [Trait("Requirement", "MNY-20")]
    [InlineData("TRY", 1_000L, new[] { 333L, 667L, 0L })]
    [InlineData("TRY", 50_000L, new[] { 10_000L, 20_000L, 0L })]
    [InlineData("USD", 1_000L, new[] { 0L, 0L, 0L })]
    public void AnAmount_IsSharedByThePositiveLines_InItsCurrencyOnly(string currency, long amountOff, long[] discounts)
    {
        var invoice = Draft(Discounted(amountOff: amountOff, currency: currency));

        invoice.AddLines([Line(10_000, Pro), Line(20_000, Pro), Line(-5_000, Pro)], taxExempt: false, Now);

        invoice.Lines.Select(line => line.DiscountAmount).ShouldBe(discounts);
    }

    [Fact]
    [Trait("Requirement", "CPN-14")]
    public void ADraftWithoutItsDiscount_IsRecalculated()
    {
        var invoice = Draft(Discounted(percentOff: 50m));
        invoice.AddLines([Line(10_000, Pro)], taxExempt: false, Now);

        invoice.SetDiscount(null, taxExempt: false, Now).IsSuccess.ShouldBeTrue();

        (invoice.DiscountTotal, invoice.Total, invoice.Lines[0].DiscountAmount).ShouldBe((0L, 10_000L, 0L));
    }

    [Theory]
    [Trait("Requirement", "CPN-12")]
    [InlineData(CouponDuration.Once, null, new[] { BillingReason.SubscriptionUpdate }, null)]
    [InlineData(CouponDuration.Repeating, 3, new[] { BillingReason.SubscriptionCreate, BillingReason.SubscriptionUpdate, BillingReason.SubscriptionCycle }, 1)]
    [InlineData(CouponDuration.Repeating, 2, new[] { BillingReason.SubscriptionCreate, BillingReason.SubscriptionCycle }, null)]
    [InlineData(CouponDuration.Forever, null, new[] { BillingReason.SubscriptionCreate, BillingReason.SubscriptionCycle, BillingReason.SubscriptionCycle }, -1)]
    public void ASubscriptionsDiscount_LastsAsItsCouponSays(CouponDuration duration, int? cycles, BillingReason[] finalized, int? cyclesLeft)
    {
        var coupon = Coupon.Create(new CouponDraft("launch", "Launch", 50m, null, null, duration, cycles, null, null, null, null), Now).Value;
        var discount = Discount.ForSubscription(coupon, null, Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

        foreach (var reason in finalized)
        {
            discount.RecordFinalized(coupon, reason, Now.AddDays(1));
        }

        // -1 stands for a discount that never ends.
        (discount.IsActive, discount.CyclesRemaining).ShouldBe(cyclesLeft switch
        {
            null => (false, duration == CouponDuration.Repeating ? 0 : null),
            -1 => (true, null),
            _ => (true, cyclesLeft),
        });
    }

    [Fact]
    [Trait("Requirement", "CPN-7")]
    public void ARedemption_IsCountedOnlyWhileTheCouponAndCodeAllowIt()
    {
        var coupon = Coupon.Create(new CouponDraft("launch", "Launch", 50m, null, null, CouponDuration.Once, null, null, 1, Now.AddDays(10), null), Now).Value;
        var code = PromotionCode.Create(coupon.Id, new PromotionCodeDraft("LAUNCH", FirstTimeOnly: true, MinimumAmount: 50_000, MinimumAmountCurrency: "TRY"), Now).Value;
        var customer = Guid.CreateVersion7();

        Code(code.Redeem(customer, hasPaidInvoice: true, 60_000, "TRY", Now)).ShouldBe("promotion_code.not_eligible");
        Code(code.Redeem(customer, hasPaidInvoice: false, 40_000, "TRY", Now)).ShouldBe("promotion_code.minimum_not_met");
        Code(code.Redeem(customer, hasPaidInvoice: false, 60_000, "USD", Now)).ShouldBe("promotion_code.minimum_not_met");
        Code(coupon.Redeem(Now.AddDays(10))).ShouldBe("coupon.expired");
        code.Redeem(customer, hasPaidInvoice: false, 60_000, "TRY", Now).IsSuccess.ShouldBeTrue();
        coupon.Redeem(Now).IsSuccess.ShouldBeTrue();
        Code(coupon.Redeem(Now)).ShouldBe("coupon.max_redemptions_reached");
        code.SetActive(false, Now);
        Code(code.Redeem(customer, hasPaidInvoice: false, 60_000, "TRY", Now)).ShouldBe("promotion_code.inactive");
        (coupon.TimesRedeemed, code.TimesRedeemed).ShouldBe((1, 1));
    }

    private static string? Code(Result result) => result.Error?.Code;

    private static Invoice Draft(InvoiceDiscount discount) =>
        Invoice.CreateDraft(Guid.CreateVersion7(), "TRY", BillingReason.Manual, CollectionMethod.ChargeAutomatically, null, null, false, Metadata.Empty, Now, discount: discount).Value;

    private static InvoiceDiscount Discounted(decimal? percentOff = null, long? amountOff = null, string? currency = null, IReadOnlyList<Guid>? products = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), null, "Launch", percentOff, amountOff, currency, products);

    private static InvoiceLineDraft Line(long amount, Guid? product) =>
        new(InvoiceLineType.Item, "Line", 1, amount, PriceId: product is null ? null : Guid.CreateVersion7(), ProductId: product);
}
