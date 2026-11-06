# Subscriptions

A subscription bills a customer for one or more recurring prices, period after period, until it ends.

## Starting one

- **Through checkout** (`mode: subscription`): the customer pays the first period on Tahsil's page, and their card is
  saved for the renewals. This is how most applications sell.
- **Through the API** (`POST /api/v1/subscriptions`) for a customer who already has a card, or whose invoices are sent
  to them to pay (`collectionMethod: send_invoice`), as companies often prefer.

Charged automatically, a new subscription is `incomplete` until its first invoice is paid, and expires if that does
not happen within `Tahsil:Subscriptions:IncompleteExpiry`. With a **trial**, it starts `trialing` and is first
charged when the trial ends; a trial that ends without a card cancels the subscription, or invoices anyway
(`trialEndBehavior`). Tahsil emails the customer three days before the trial ends.

## Statuses

| Status | Meaning |
|---|---|
| `incomplete` | Waiting for the first payment |
| `trialing` | In its trial |
| `active` | Paid up |
| `past_due` | A renewal is unpaid and [dunning](dunning.md) is retrying it |
| `canceled` | Ended |
| `incomplete_expired` | The first payment never came |

## Periods and renewals

Periods follow the subscription's anchor in the billing time zone (`Tahsil:Billing:TimeZone`): a monthly subscription
started on 31 January renews on 28 February, then 31 March. At each renewal Tahsil creates the period's invoice as a
draft, which items can still join for an hour (`Tahsil:Billing:DraftPeriod`), then finalizes it and collects it.

## Changes

`PATCH /api/v1/subscriptions/{id}` changes the items or quantities, now or at the period's end:

- **Now**, the unused time of the old items is credited and the new items charged for the rest of the period
  (proration). `prorationBehavior` decides what happens with the difference: invoiced and charged at once
  (`always_invoice`, the default), added to the next renewal (`create_prorations`), or ignored (`none`). A negative
  difference becomes the customer's credit, which later invoices use first.
- **At the period's end** (`effective: period_end`), nothing is prorated: the renewal bills the new items.
- A change of interval, such as monthly to yearly, restarts the period at once.

`POST /api/v1/subscriptions/{id}/preview` shows what a change would invoice, without changing anything.

A price increase for an individual customer must be scheduled at least `Tahsil:Subscriptions:PriceIncreaseNotice`
(30 days) ahead; Tahsil emails them the new price and how to cancel before it applies.

## Ending

`POST /api/v1/subscriptions/{id}/cancel` ends a subscription at the period's end (the default), at a time, or now.
A cancellation now can refund the unused time (`refund: prorated`), the latest payment or all of them, and bills
any [usage](usage.md) and pending items on a final invoice. A cancellation that has not taken effect can be taken
back (`/resume`). Customers can do all of this in the [portal](portal.md).
