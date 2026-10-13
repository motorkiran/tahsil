# Taking payments with Stripe

This guide connects a Tahsil installation to [Stripe](https://stripe.com) for selling abroad. Customers pay on
Stripe Checkout, Stripe keeps their card, and Tahsil charges it for renewals without asking the customer again.
Tahsil uses Stripe only to move money: subscriptions, invoices, tax and dunning stay in Tahsil, so Stripe Billing's
fees do not apply.

## Before you start

You need:

- A Stripe account in a country Stripe supports. Türkiye is not one of them: a Turkish business needs an entity
  abroad to use Stripe.
- Card payments enabled in Stripe's Dashboard (**Settings → Payment methods**).
- A key. A restricted key (`rk_test_…` or `rk_live_…`) is safest: create it under **Developers → API keys → Create
  restricted key** with **Write** access to Customers, PaymentIntents, SetupIntents, PaymentMethods, Checkout
  Sessions, Charges and Refunds, and **Read** access to Events. A secret key (`sk_…`) also works.
- Optionally, a statement descriptor prefix under **Settings → Public details**; each connection can add a suffix.

Tahsil works with Stripe's API version `2026-09-30.endive` and needs no other setting in Stripe.

## 1. Create the connection

A Test mode installation takes sandbox connections and test keys (`_test_`); a Live mode installation takes
production connections and live keys (`_live_`).

**In the admin panel**, open **Provider connections**, choose **Create connection**, pick **Stripe**, and enter:

| Field | Value |
|---|---|
| Key | A name for the connection, such as `stripe-main` |
| Secret or restricted key | Your key. Tahsil stores it encrypted and never shows it again. |
| Event destination signing secret | The `whsec_…` secret of the event destination you create in step 3. Enter a placeholder first if you create the destination afterwards, and edit the connection then. |
| Statement descriptor suffix | Optional: added to your prefix on customers' card statements, up to 22 characters with it |

Tahsil tests the connection right after you create it. "The key's mode does not match" means a test key on a
production connection, or the other way round.

**In a provisioning file**, keep the secrets in configuration, not in the file:

```json
{
  "providerConnections": [
    {
      "key": "stripe-main",
      "provider": "stripe",
      "name": "Stripe",
      "environment": "production",
      "currencies": ["USD", "EUR"],
      "credentials": { "secretKey": "${Stripe:SecretKey}", "webhookSigningSecret": "${Stripe:WebhookSecret}" }
    }
  ]
}
```

Then set `Stripe__SecretKey` and `Stripe__WebhookSecret` in Tahsil's environment.

## 2. Route your currencies

Customers pay each currency through the connection routed for it. In **Provider connections**, choose **Route
currencies** and route the currencies you sell to the Stripe connection. Tahsil takes `USD`, `EUR`, `GBP`, `CHF`,
`CAD`, `AUD` and `JPY` through Stripe, each with Stripe's minimum charge (such as $0.50).

## 3. Create the event destination

Stripe tells Tahsil about payments through events. Each connection shows its notification URL, in the form
`https://billing.example.com/provider-notifications/{connection ID}`. In Stripe, open **Workbench → Webhooks →
Create an event destination**, and:

- choose **Thin** as the payload style;
- select these events: `v1.checkout.session.completed`, `v1.checkout.session.expired`, `v1.payment_intent.succeeded`,
  `v1.payment_intent.payment_failed`, `v1.payment_intent.canceled`, `v1.setup_intent.succeeded`,
  `v1.setup_intent.setup_failed`, `v1.refund.created`, `v1.refund.updated`, `v1.refund.failed` and
  `v1.payment_method.automatically_updated`;
- enter the connection's notification URL as the endpoint, and copy the destination's signing secret into the
  connection.

Tahsil checks each event's signature, reads the payment or refund it names from Stripe, and settles it the same way
whichever arrives first: the event or the customer coming back from Checkout. Payments are therefore settled even if
an event is lost. To test events in a sandbox from your machine, any tunnel that gives Tahsil a public HTTPS address
works.

## 4. Try it

With a Test mode installation and a sandbox connection:

1. Create a checkout session for a one-time price in a currency routed to Stripe, and open its URL.
2. Pay on Stripe Checkout with the test card `4242 4242 4242 4242`, any future expiry date and any CVC.
3. You return to the success URL. The invoice is paid, the order confirmation is sent, and your webhook endpoint
   receives `checkout.completed`, `invoice.paid` and `payment.succeeded`.
4. For a subscription, pay with `4000 0025 0000 3155`: it asks for 3D Secure once, and renewals then go through
   without the customer. Move Tahsil's test clock a month forward to see a renewal.
5. Refund the payment, wholly or in part, in the admin panel. Stripe may keep a refund pending for a while; Tahsil
   updates it when Stripe settles it.

Stripe's card `4000 0027 6000 3184` asks for 3D Secure every time: its renewals fail with `authentication_required`,
and the customer pays them on the hosted invoice page.

## Good to know

- **Saving cards.** Customers' cards are saved for later charges when they pay a subscription or a session that
  saves its card. A checkout session in `setup` mode saves a card without charging it, which is how trials with a
  card start.
- **Renewals and 3D Secure.** Renewals are charged without the customer. When the customer's bank still asks for
  3D Secure, the renewal fails with `authentication_required`, and the customer is asked to pay on the hosted
  invoice page.
- **Refunds.** Any part of a payment can be refunded. Refunds can stay pending; Tahsil finishes them when Stripe
  does. Stripe never cancels a payment that succeeded, so every refund is a refund.
- **Retries are safe.** Each request carries Tahsil's own idempotency key, so a retried renewal is never charged
  twice. When Stripe does not answer, Tahsil asks Stripe later instead of guessing.
- **Card updates.** When Stripe's card account updater gives a card a new expiry or number, Tahsil updates the saved
  card.
- **Failures.** Tahsil turns Stripe's decline codes into its own failure codes, such as `insufficient_funds` or
  `card_declined`, and shows customers a message in their language. Stripe's own code and message stay available on
  the payment for support.

## Troubleshooting

| Symptom | Cause |
|---|---|
| The connection test says the key's mode does not match | A test key on a production connection, or a live key on a sandbox one |
| The connection test fails with `invalid_credentials` | A revoked key, or a restricted key without one of the permissions above |
| Payments stay `processing` after customers return | The event destination is missing or points elsewhere; reconciliation still settles them within minutes |
| Events are rejected with 400 | The connection's signing secret is not the event destination's, or the destination sends snapshot payloads instead of thin ones |
| A currency cannot be routed to the connection | Tahsil does not take that currency through Stripe yet |
