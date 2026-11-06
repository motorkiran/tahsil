# The customer portal

A hosted page where customers manage their own billing, without your application building any of it.

Your application creates a session for a signed-in user and sends them to its URL:

```http
POST /api/v1/portal-sessions
{ "customer": "0199…", "returnUrl": "https://app.example.com/settings/billing" }
```

The session lasts an hour (`Tahsil:Portal:SessionLifetime`) and shows only that customer's billing. Tahsil's own
emails, such as a failed payment's or a trial's end, carry portal links of their own that work for a week.

## What customers can do

Each feature can be turned off in `Tahsil:Portal:Features`:

| Feature | |
|---|---|
| `subscriptions` | See their subscriptions, the next renewal and its amount, and usage so far |
| `plan_change` | Change plan or quantity among `Tahsil:Portal:Products`, with the amount shown before they confirm |
| `promotion_codes` | Enter a promotion code with a plan change |
| `cancel` | Cancel, at the period's end; individuals may also cancel at once |
| `resume` | Take back a cancellation that has not taken effect |
| `withdrawal` | Withdraw within 14 days, for individuals who did not agree to the service starting at once, with everything paid refunded |
| `payment_method` | Replace their card, on the provider's page |
| `invoices` | See and pay their invoices, and see their receipts |
| `billing_details` | Update their name, address and tax details |

Upgrades take effect at once and are charged pro rata; downgrades wait for the period's end
(`Tahsil:Portal:DowngradeTiming`). Cancellation can offer a coupon to stay (`Tahsil:Portal:RetentionCoupon`). Every
confirmation the customer gives, with the amounts they were shown, is kept as evidence.
