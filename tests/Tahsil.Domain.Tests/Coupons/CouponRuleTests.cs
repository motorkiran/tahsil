using Tahsil.Domain.Coupons;

namespace Tahsil.Domain.Tests.Coupons;

public sealed class CouponRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [Trait("Requirement", "CPN-1")]
    [InlineData(50d, null, null, true)]
    [InlineData(100d, null, null, true)]
    [InlineData(12.5, null, null, true)]
    [InlineData(12.555, null, null, false)]
    [InlineData(0d, null, null, false)]
    [InlineData(100.01, null, null, false)]
    [InlineData(null, 10_000L, "TRY", true)]
    [InlineData(null, 10_000L, null, false)]
    [InlineData(null, 10_000L, "XYZ", false)]
    [InlineData(null, 0L, "TRY", false)]
    [InlineData(50d, null, "TRY", false)]
    [InlineData(50d, 10_000L, "TRY", false)]
    [InlineData(null, null, null, false)]
    public void ACoupon_TakesOffEitherAPercentageOrAnAmountInItsCurrency(double? percentOff, long? amountOff, string? currency, bool valid)
    {
        Coupon.Create(Draft() with { PercentOff = (decimal?)percentOff, AmountOff = amountOff, Currency = currency }, Now).IsSuccess.ShouldBe(valid);
    }

    [Theory]
    [Trait("Requirement", "CPN-1")]
    [InlineData(CouponDuration.Once, null, true)]
    [InlineData(CouponDuration.Forever, null, true)]
    [InlineData(CouponDuration.Repeating, 1, true)]
    [InlineData(CouponDuration.Repeating, 120, true)]
    [InlineData(CouponDuration.Repeating, 0, false)]
    [InlineData(CouponDuration.Repeating, 121, false)]
    [InlineData(CouponDuration.Repeating, null, false)]
    [InlineData(CouponDuration.Once, 3, false)]
    public void OnlyARepeatingCoupon_LastsANumberOfCycles(CouponDuration duration, int? cycles, bool valid)
    {
        Coupon.Create(Draft() with { Duration = duration, DurationInCycles = cycles }, Now).IsSuccess.ShouldBe(valid);
    }

    [Fact]
    [Trait("Requirement", "CPN-2")]
    public void ACoupon_ChangesItsNameAndLimits_ButNeverItsDiscountDurationOrProducts()
    {
        var product = Guid.CreateVersion7();
        var coupon = Coupon.Create(Draft() with { ProductIds = [product, product] }, Now).Value;

        coupon.Update("Spring", 10, Now.AddDays(30), isArchived: true, new Dictionary<string, string> { ["campaign"] = "spring" }, Now.AddDays(1)).IsSuccess.ShouldBeTrue();

        (coupon.Name, coupon.MaxRedemptions, coupon.RedeemBy, coupon.IsArchived, coupon.Metadata["campaign"]).ShouldBe(("Spring", 10, Now.AddDays(30), true, "spring"));
        (coupon.PercentOff, coupon.Duration, coupon.DurationInCycles).ShouldBe((50m, CouponDuration.Repeating, 3));
        coupon.ProductIds.ShouldBe([product]);
        typeof(Coupon).GetProperties().Where(property => property.SetMethod?.IsPublic == true).ShouldBeEmpty();
        Coupon.Create(Draft() with { ProductIds = [] }, Now).Error!.Field.ShouldBe("products");
    }

    [Theory]
    [Trait("Requirement", "CPN-2")]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(null, true)]
    public void TheRedemptionLimit_IsAtLeastOne(int? limit, bool valid)
    {
        var coupon = Coupon.Create(Draft(), Now).Value;

        coupon.Update(coupon.Name, limit, null, isArchived: false, null, Now).IsSuccess.ShouldBe(valid);
    }

    [Theory]
    [Trait("Requirement", "CPN-4")]
    [InlineData("LAUNCH50", "LAUNCH50")]
    [InlineData(" launch-50_a ", "LAUNCH-50_A")]
    [InlineData("abc", "ABC")]
    [InlineData("AB", null)]
    [InlineData("LAUNCH 50", null)]
    [InlineData("İNDİRİM", null)]
    [InlineData("A123456789012345678901234567890123456789", "A123456789012345678901234567890123456789")]
    [InlineData("A1234567890123456789012345678901234567890", null)]
    public void ACode_IsThreeToFortyLettersDigitsOrDashes_StoredInUppercase(string code, string? stored)
    {
        var created = PromotionCode.Create(Guid.CreateVersion7(), new PromotionCodeDraft(code), Now);

        (created.IsSuccess ? created.Value.Code : null).ShouldBe(stored);
    }

    [Theory]
    [Trait("Requirement", "CPN-5")]
    [InlineData(50_000L, "TRY", 1, true)]
    [InlineData(50_000L, null, null, false)]
    [InlineData(null, "TRY", null, false)]
    [InlineData(0L, "TRY", null, false)]
    [InlineData(null, null, 0, false)]
    public void ACodesMinimum_ComesWithItsCurrency_AndItsLimitIsAtLeastOne(long? minimum, string? currency, int? limit, bool valid)
    {
        PromotionCode.Create(Guid.CreateVersion7(), new PromotionCodeDraft("SPRING", MaxRedemptions: limit, MinimumAmount: minimum, MinimumAmountCurrency: currency), Now)
            .IsSuccess.ShouldBe(valid);
    }

    [Fact]
    [Trait("Requirement", "CPN-6")]
    public void ACode_OnlyTurnsOnAndOff()
    {
        var code = PromotionCode.Create(Guid.CreateVersion7(), new PromotionCodeDraft("SPRING", FirstTimeOnly: true), Now).Value;

        code.SetActive(false, Now.AddDays(1));

        (code.IsActive, code.FirstTimeOnly, code.UpdatedAt).ShouldBe((false, true, Now.AddDays(1)));
        typeof(PromotionCode).GetProperties().Where(property => property.SetMethod?.IsPublic == true).ShouldBeEmpty();
    }

    private static CouponDraft Draft() =>
        new("launch", "Launch discount", 50m, null, null, CouponDuration.Repeating, 3, null, null, null, null);
}
