# The catalog

What you sell, described once and referred to everywhere by key.

- **Products** are what customers buy: "Pro", "Team", "API access". A product has a key, a name and an optional
  Kimlik plan (see [Kimlik](kimlik.md)).
- **Prices** say how a product is paid for: a currency, one-time or recurring every *n* days, weeks, months or
  years, and an amount. A price's **lookup key**, such as `pro-monthly-try`, is how applications and provisioning name
  it; a key can move to a new price, so that the application keeps asking for `pro-monthly-try` while new customers
  get the new amount.
- **Tax rates** are attached to prices: KDV 20% included in the price (the usual case in Turkey) or added on top.
  A rate's percentage never changes; to change the rate, create a new one, give it to new prices and archive the
  old.
- **Meters** count usage for metered prices; see [usage-based billing](usage.md).

## Kinds of prices

| Scheme | The amount for a quantity |
|---|---|
| Per unit | quantity × unit amount, such as ₺100 per seat |
| Package | per started package, such as ₺50 per 1,000 messages, rounded up or down |
| Tiered, graduated | each tier bills the units that fall in it: the first 1,000 free, the next at 0.2 kuruş… |
| Tiered, volume | the tier the whole quantity falls in bills every unit |

Recurring prices are **licensed** (a quantity the customer chooses, billed at the start of each period) or **metered**
(usage, billed at the end). A recurring price can have a trial length that subscriptions bought through checkout get.
Amounts are integers in the currency's minor units; for tiny per-unit prices, `unitAmountDecimal` takes fractions of
a kuruş.

## Changing the catalog

Prices that have been sold never change: their amount, currency and interval are part of what customers agreed
to. To change a price, create a new one and move the lookup key to it; existing subscriptions keep the old price
until you change them (see [subscriptions](subscriptions.md) for the notice Turkish law asks before a price
increase). Archived products and prices sell no more but keep billing what they sold.

## Provisioning

Declare the catalog in a JSON document and let Tahsil make it so: `POST /api/v1/provisioning`, or a file applied at
startup (`Tahsil:Provisioning:FilePath`). Items are matched by key; what is missing is created, what differs is
updated, nothing is ever deleted (retire an item with `"isArchived": true`). `?dryRun=true` shows what would change.
`GET /api/v1/provisioning` exports the installation as such a document, which is how a catalog built in the admin
panel moves to version control. The same document declares coupons, provider connections and webhook endpoints,
with secrets written as configuration references such as `${Iyzico:ApiKey}`.
