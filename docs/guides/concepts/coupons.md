# Coupons and promotion codes

A **coupon** is a discount: a percentage, or an amount in one currency, off every product or only some. How long it
lasts:

| Duration | Discounts |
|---|---|
| `once` | the first invoice |
| `repeating` | the invoices of the first *n* periods (`durationInCycles`) |
| `forever` | every invoice of the subscription |

A coupon can be limited in redemptions (`maxRedemptions`) and in time (`redeemBy`).

A **promotion code** is how customers name a coupon: `LAUNCH2026`, with its own limits (redemptions, expiry,
first-time customers only, a minimum amount). Checkout sessions created with `allowPromotionCodes: true` show a code
field, and the [portal](portal.md) can take codes for plan changes.

Apply a coupon directly with `coupon` on a checkout session, a subscription or a manual invoice. A subscription has
one discount at a time; giving it a new coupon replaces the old one. Discounts are taken off before tax, spread over
the lines they apply to, and shown on the invoice.
