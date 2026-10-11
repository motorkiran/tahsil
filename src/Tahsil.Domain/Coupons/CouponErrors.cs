using Tahsil.Domain.Common;

namespace Tahsil.Domain.Coupons;

public static class CouponErrors
{
    public static readonly Error NotFound = Error.NotFound("coupon.not_found", "The coupon does not exist.");
    public static readonly Error KeyTaken = Error.Conflict("coupon.key_taken", "Another coupon has this key.");

    public static readonly Error Archived = Error.Validation("coupon.archived", "The coupon is archived and redeems no more.");
    public static readonly Error Expired = Error.Validation("coupon.expired", "The coupon could be redeemed only until its redeem-by time.");
    public static readonly Error MaxRedemptionsReached = Error.Validation("coupon.max_redemptions_reached", "The coupon has been redeemed as often as it may be.");
    public static readonly Error CurrencyMismatch = Error.Validation("coupon.currency_mismatch", "The coupon takes an amount off in another currency.");
    public static readonly Error NotApplicable = Error.Validation("coupon.not_applicable", "The coupon is for other products.");

    public static Error Invalid(string field, string message) => Error.Validation("coupon.invalid", message, field);

    public static Error Immutable(string field) =>
        Error.Validation("coupon.immutable", $"'{field}' cannot change once a coupon exists; create a new coupon instead.", field);
}

public static class PromotionCodeErrors
{
    public static readonly Error NotFound = Error.NotFound("promotion_code.not_found", "The promotion code does not exist.");
    public static readonly Error CodeTaken = Error.Conflict("promotion_code.code_taken", "Another promotion code has this code, in any case.");

    public static readonly Error Inactive = Error.Validation("promotion_code.inactive", "The promotion code is not active.");
    public static readonly Error Expired = Error.Validation("promotion_code.expired", "The promotion code has expired.");
    public static readonly Error MaxRedemptionsReached = Error.Validation("promotion_code.max_redemptions_reached", "The promotion code has been redeemed as often as it may be.");
    public static readonly Error NotEligible = Error.Validation("promotion_code.not_eligible", "The promotion code is not for this customer.");

    public static Error Invalid(string field, string message) => Error.Validation("promotion_code.invalid", message, field);

    public static Error MinimumNotMet(long minimum, string currency)
    {
        var exponent = Money.Currency.Get(currency).Exponent;
        var major = (minimum / (decimal)Math.Pow(10, exponent)).ToString($"F{exponent}", System.Globalization.CultureInfo.InvariantCulture);
        return Error.Validation("promotion_code.minimum_not_met", $"The promotion code needs a subtotal of at least {major} {currency}.");
    }
}
