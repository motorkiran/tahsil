# Dunning

When a renewal's automatic payment fails, Tahsil tries to recover it before it gives up.

1. The failure is sorted by what it means: a temporary problem (try again later), a card that will not work (the
   customer must give another), a payment that needs the customer's 3D Secure, or a problem on the merchant's side,
   which is never blamed on the customer and is shown to the operator instead.
2. The customer is emailed with a link to the invoice's hosted page, where they pay or authenticate, and to the
   [portal](portal.md), where they change their card. Giving a new card retries the invoice at once.
3. Temporary failures are retried on a schedule: 1, 3, 5 and 7 days after the first failure by default
   (`Tahsil:Dunning:RetrySchedule`). The subscription is `past_due` meanwhile.
4. A day before the last retry (`FinalReminderBefore`), the customer is told it is the last one.
5. If nothing worked, the final action ends it: the subscription is canceled (`CancelSubscription`, the default) and
   the invoice stays open, is marked uncollectible or is voided (`InvoiceAction`).

Paying every open invoice of a `past_due` subscription makes it `active` again. The invoice's `dunning` filter in the
API and the admin panel lists what is being recovered, and the dashboard shows the share recovered over 30 days.

Your application learns of each step from `invoice.payment_failed`, `subscription.past_due`,
`invoice.action_required`, `invoice.paid` and `subscription.canceled`. With Kimlik, a past-due customer keeps their
plan until the final action (`Tahsil:Kimlik:PastDueAccess`).
