# Getting started

This guide runs Tahsil on your machine in Test mode, where nothing is charged: a built-in simulator stands in for
the payment provider. In about fifteen minutes you sell a subscription through the hosted checkout, pay it with a
test card, receive its webhooks and move the clock a month ahead to see it renew.

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download), Docker or Podman, `curl`, and a clone of
[the repository](https://github.com/motorkiran/tahsil).

## 1. Start Tahsil

```sh
docker compose up -d                        # PostgreSQL on port 5434 and Mailpit on http://localhost:8026
dotnet run --project src/Tahsil.Server      # Tahsil on http://localhost:5090
```

Tahsil creates its database tables when it starts. Its development settings
(`src/Tahsil.Server/appsettings.Development.json`) put it in Test mode with example merchant details, send its emails
to Mailpit, and let webhooks reach applications on your machine. A new Test mode installation has one payment
provider connection: the simulator, for TRY, USD and EUR.

## 2. Get an API key

Open the admin panel at <http://localhost:5090/admin>, choose **Sign in as developer** (offered only in Test mode in
the Development environment), then **API keys** → **Create key**. Give it a name and the permissions this guide uses:
`tahsil.provisioning:write`, `tahsil.catalog:write`, `tahsil.checkout:write`, `tahsil.subscriptions:read`,
`tahsil.invoices:read`, `tahsil.webhooks:write` and `tahsil.test:write`. Copy the key: it is shown once.

Without a browser, the server creates one for you; the key is the last line it prints:

```sh
dotnet run --project src/Tahsil.Server -- create-api-key "Getting started" \
  tahsil.provisioning:write tahsil.catalog:write tahsil.checkout:write tahsil.subscriptions:read \
  tahsil.invoices:read tahsil.webhooks:write tahsil.test:write
```

Then keep it at hand:

```sh
export TAHSIL_KEY=ths_test_…
```

## 3. Define what you sell

A provisioning document declares the catalog, and applying it again changes only what differs. This one has a Pro
plan at ₺299,00 a month, KDV included:

```sh
curl -s http://localhost:5090/api/v1/provisioning \
  -H "Authorization: Bearer $TAHSIL_KEY" -H "Content-Type: application/json" \
  -d '{
    "taxRates": [{ "key": "kdv-20", "name": "KDV", "percentage": 20, "inclusive": true }],
    "products": [{
      "key": "pro", "name": "Pro",
      "prices": [{ "key": "pro-monthly-try", "name": "Monthly", "currency": "TRY", "type": "recurring",
                   "interval": "month", "intervalCount": 1, "usageType": "licensed", "unitAmount": 29900, "taxRate": "kdv-20" }]
    }]
  }'
```

Amounts are in minor units: `29900` is ₺299,00. Production installations usually keep this document in a file that
Tahsil applies at startup (`Tahsil:Provisioning:FilePath`).

## 4. Sell a subscription

Your application creates a checkout session when a user wants to subscribe, and sends them to its `url`:

```sh
curl -s http://localhost:5090/api/v1/checkout-sessions \
  -H "Authorization: Bearer $TAHSIL_KEY" -H "Content-Type: application/json" \
  -d '{
    "mode": "subscription",
    "items": [{ "price": "pro-monthly-try" }],
    "customerDetails": { "externalId": "user-42", "email": "ada@example.com", "locale": "en" },
    "successUrl": "https://app.example.com/billing?session={CHECKOUT_SESSION_ID}"
  }'
```

Open the `url` of the answer in a browser. The page shows the plan with its tax, asks for the customer's name, and
for the consents Turkish distance sales require: the pre-information form and the distance sales contract, which
Tahsil renders from starter templates with the merchant's details, and the consent to charge the card for renewals.
Then it shows the simulator's card form, where you pay with:

| Card number | Expiry | CVC |
|---|---|---|
| `4242 4242 4242 4242` | `12/30` | `123` |

The browser returns to your `successUrl`. Tahsil created the customer (with your `externalId`), saved the card, paid
the first invoice and started the subscription; the admin panel shows them all, and Mailpit has the confirmation email
with the contract attached. Other [test cards](#test-cards) ask for 3D Secure, decline or time out.

## 5. Receive webhooks

Tahsil tells your application what happens through signed webhooks. Register an endpoint (any URL your application
listens on; here, port 5100 on your machine):

```sh
curl -s http://localhost:5090/api/v1/webhooks/endpoints \
  -H "Authorization: Bearer $TAHSIL_KEY" -H "Content-Type: application/json" \
  -d '{ "url": "http://localhost:5100/webhooks/tahsil" }'
```

The answer has the endpoint's `secret` (`whsec_…`), shown once. In an ASP.NET Core application,
[Tahsil.AspNetCore](../../sdk/Tahsil.AspNetCore/README.md) verifies the signatures and hands you typed events:

```csharp
app.MapTahsilWebhooks("/webhooks/tahsil", builder.Configuration["Tahsil:WebhookSecret"]!, async (TahsilEvent webhookEvent, CancellationToken cancellationToken) =>
{
    if (webhookEvent.Type == TahsilEventTypes.InvoicePaid)
    {
        var invoice = webhookEvent.DetailsAs<InvoiceDetails>()!;
        // Give the customer what they paid for.
    }
});
```

Any other stack verifies them with a [Standard Webhooks](https://www.standardwebhooks.com) library. The admin
panel's **Webhooks** page shows every delivery with its response, and can send a test event or deliver one again.

## 6. Move the clock

In Test mode you do not wait a month for a renewal: move Tahsil's clock, and it does on the way everything that falls
due, each at its own time.

```sh
curl -s http://localhost:5090/api/v1/test/clock \
  -H "Authorization: Bearer $TAHSIL_KEY" -H "Content-Type: application/json" \
  -d '{ "advanceBy": "P1M" }'
```

The subscription renews: its new period starts and its renewal invoice is created as a draft, which your
application may still add items to for an hour. Move another hour (`"advanceBy": "PT1H"`) and the invoice is
finalized and charged to the saved card; your endpoint receives `subscription.renewed`, `invoice.created`,
`invoice.finalized`, `invoice.paid` and `payment.succeeded`, and Mailpit has the receipt. The admin panel's
**Test mode** page does the same, and shows what each step did.

To see dunning, save a card that declines its renewals, such as `4000 0000 0000 0341`, and move the clock past a
renewal: Tahsil retries on its schedule, emails the customer, and in the end cancels the subscription or leaves the
invoice unpaid, as `Tahsil:Dunning` says.

## Next

- [The API](api.md): keys, idempotency, errors and paging, and the [API reference](../reference/api.md).
- The concepts: [catalog](concepts/catalog.md), [subscriptions](concepts/subscriptions.md),
  [invoices](concepts/invoices.md), [dunning](concepts/dunning.md), [coupons](concepts/coupons.md),
  [usage](concepts/usage.md), the [customer portal](concepts/portal.md) and [Kimlik](concepts/kimlik.md).
- [Tahsil.Client](../../sdk/Tahsil.Client/README.md), the .NET client of the API.
- Real payments: connect a [provider](providers/README.md), set your merchant details and legal documents, and read
  the [configuration reference](../reference/configuration.md) before you switch to Live mode.

## Test cards

The simulator takes these numbers only, with any future expiry date and any CVC. "Renewals" are the charges Tahsil
makes later to the saved card, without the customer.

| Card | At checkout | Renewals |
|---|---|---|
| `4242 4242 4242 4242` | Succeeds | Succeed |
| `4000 0000 0000 3220` | Asks for 3D Secure, then succeeds | Succeed |
| `4000 0025 0000 3155` | Asks for 3D Secure, then succeeds | Need the customer: `authentication_required` |
| `4000 0000 0000 0341` | Succeeds | Declined: `card_declined` |
| `4000 0000 0000 0259` | Succeeds | Declined: `fraud_suspected` |
| `4000 0000 0000 1026` | Succeeds | Succeed three times, then are declined |
| `4000 0000 0000 0002` | Declined: `card_declined` | |
| `4000 0000 0000 9995` | Declined: `insufficient_funds` | |
| `4000 0000 0000 0069` | Declined: `expired_card` | |
| `4000 0000 0000 9987` | Declined: `lost_or_stolen` | |
| `4000 0000 0000 0119` | Fails: `processing_error` | |
| `4000 0000 0000 1000` | Succeeds | Charged, but the answer times out; Tahsil finds out by asking the simulator |
| `4000 0000 0000 1018` | Succeeds | Time out before anything is charged |
| `4000 0000 0000 7726` | Succeeds | Succeed; refunds stay pending for an hour |
| `4000 0000 0000 5126` | Succeeds | Succeed; refunds fail |
| `9792 0000 0000 0001` | Succeeds (Troy debit card) | Succeed |

In Test mode, `POST /api/v1/test/simulator/payment-methods` saves one of these cards for a customer without a
checkout, and the admin panel's customer page has **Add a test card**.

