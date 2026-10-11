using Tahsil.Domain.Common;

namespace Tahsil.Domain.Coupons;

public static class CouponErrors
{
    public static readonly Error NotFound = Error.NotFound("coupon.not_found", "The coupon does not exist.");
    public static readonly Error KeyTaken = Error.Conflict("coupon.key_taken", "Another coupon has this key.");

    public static Error Invalid(string field, string message) => Error.Validation("coupon.invalid", message, field);

    public static Error Immutable(string field) =>
        Error.Validation("coupon.immutable", $"'{field}' cannot change once a coupon exists; create a new coupon instead.", field);
}

public static class PromotionCodeErrors
{
    public static readonly Error NotFound = Error.NotFound("promotion_code.not_found", "The promotion code does not exist.");
    public static readonly Error CodeTaken = Error.Conflict("promotion_code.code_taken", "Another promotion code has this code, in any case.");

    public static Error Invalid(string field, string message) => Error.Validation("promotion_code.invalid", message, field);
}
