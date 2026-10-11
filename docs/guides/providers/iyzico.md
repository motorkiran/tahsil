# Taking payments with iyzico

This guide connects a Tahsil installation to [iyzico](https://www.iyzico.com). Customers pay on iyzico's hosted
payment page with 3D Secure, their card is stored by iyzico, and Tahsil charges the stored card for later payments
without asking the customer again.

## Before you start

You need:

- An iyzico merchant account. For testing, create a free sandbox account at
  <https://sandbox-merchant.iyzipay.com/auth/register>. In the sandbox, the SMS code is always `123456`.
- Your API key and secret key, under **Settings → Merchant Settings → API Keys**. Sandbox keys start with `sandbox-`.
- These account features, which iyzico enables on request (write to `entegrasyon@iyzico.com` with your merchant ID):
  - **Card storage.** Without it, iyzico cannot keep cards, and charges of saved cards fail with error 3007.
  - **NON-3DS payments for stored cards.** Without it, renewals fail with errors 5010, 5072 or 5167, and customers
    have to pay each renewal on the hosted invoice page.
  - **Webhook signature V3.** Tahsil verifies the `X-IYZ-SIGNATURE-V3` header and rejects notifications without it.
  - **Foreign currencies**, only if you sell in USD, EUR, GBP, NOK or CHF. Turkish cards pay in TRY only.

## 1. Create the connection

A Test mode installation takes sandbox connections, and a Live mode installation takes production connections.

**In the admin panel**, open **Provider connections**, choose **Create connection**, pick **iyzico**, and enter:

| Field | Value |
|---|---|
| Key | A name for the connection, such as `iyzico-main` |
| API key, Secret key | Your keys. Tahsil stores them encrypted and never shows them again. |
| Identity number for buyers without one | iyzico requires an identity number with every payment. Tahsil sends the customer's tax ID (TCKN or VKN) when it has one, and this value otherwise. The default is `11111111111`; ask iyzico whether it is acceptable for your account. |
| Address, city and country for buyers without one | iyzico requires an address with every payment. Tahsil sends the customer's address when it has one, and these values otherwise. |

Tahsil tests the connection right after you create it. A failure that says iyzico rejected the keys usually means
sandbox keys in a Live installation, or the other way round.

**In a provisioning file**, keep the keys in configuration, not in the file:

```json
{
  "providerConnections": [
    {
      "key": "iyzico-main",
      "provider": "iyzico",
      "name": "iyzico",
      "environment": "production",
      "currencies": ["TRY"],
      "credentials": { "apiKey": "${Iyzico:ApiKey}", "secretKey": "${Iyzico:SecretKey}" }
    }
  ]
}
```

Then set `Iyzico__ApiKey` and `Iyzico__SecretKey` in Tahsil's environment.

## 2. Route your currencies

Customers pay each currency through the connection routed for it. In **Provider connections**, choose **Route
currencies** and route `TRY` (and any other currency you sell) to the iyzico connection. The `currencies` of a
provisioned connection do the same.

## 3. Set the notification URL

iyzico tells Tahsil about payments through notifications. Each connection shows its notification URL, in the form
`https://billing.example.com/provider-notifications/{connection ID}`. In iyzico's merchant panel, open **Settings →
Merchant Settings → Merchant Notifications** and enter that URL. It must be HTTPS, and iyzico takes one URL per
merchant account.

Tahsil does not trust a notification on its own: it checks the signature, then asks iyzico how the payment stands
before it changes anything. Payments are therefore settled even if a notification is lost.

## 4. Try it

With a Test mode installation and a sandbox connection:

1. Create a checkout session for a one-time price and open its URL.
2. Pay on iyzico's page with the test card `5528 7900 0000 0008`, any future expiry date and any CVC, and enter
   `123456` as the SMS code.
3. You return to the success URL. The invoice is paid, the order confirmation is sent, and your webhook endpoint
   receives `checkout.completed`, `invoice.paid` and `payment.succeeded`.
4. Refund the payment in the admin panel. On the day of the payment, iyzico cancels it instead of refunding it.

iyzico documents more test cards for declines: `4111 1111 1111 1129` (insufficient funds), `4129 1111 1111 1111`
(do not honour), `4125 1111 1111 1115` (expired), `4127 1111 1111 1113` (lost card) and `4121 1111 1111 1119`
(fraud suspected).

## Good to know

- **Refunds.** On the day of the payment (Istanbul time), only a full refund is possible, and iyzico cancels the
  payment. Part of a payment can be refunded from the next day, for up to 365 days. Tahsil refuses an early partial
  refund before calling iyzico and says when it becomes possible.
- **Saving cards.** iyzico's page may let the customer decide whether to save the card. When a card is not saved, the
  purchase still completes, and the customer can add a card later.
- **Saving a card without a payment.** iyzico has no documented way to save a card without charging it, so checkout
  sessions in `setup` mode are not available with iyzico yet.
- **Amounts.** iyzico takes at least ₺1.00 per payment, and less than 100,000 per transaction unless iyzico raises
  your limit.
- **Failures.** Tahsil turns iyzico's error codes into its own failure codes, such as `insufficient_funds` or
  `card_declined`, and shows customers a message in their language. The provider's own code and message stay
  available on the payment for support.
- **Fraud review.** A payment iyzico holds for fraud review stays `processing` until iyzico decides; Tahsil checks
  again on its reconciliation schedule.

## Troubleshooting

| Symptom | Cause |
|---|---|
| The connection test fails with "iyzico rejected the keys" | Wrong keys, or sandbox keys in a Live installation (or the other way round) |
| Renewals fail with `authentication_required` | NON-3DS payments for stored cards are not enabled on the account |
| Renewals fail with `merchant_not_permitted` | Card storage is not enabled on the account |
| Payments stay `processing` after customers return | Notifications are not set up, or the account's notification URL points elsewhere; reconciliation still settles them within minutes |
| Notifications are rejected with 400 | Webhook signature V3 is not enabled, or the connection's secret key is not the account's |
