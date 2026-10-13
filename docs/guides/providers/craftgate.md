# Taking payments with Craftgate (and Kuveyt Türk)

This guide connects a Tahsil installation to [Craftgate](https://craftgate.io), a payment orchestration platform
that runs on your own bank virtual POSes. Customers pay on Craftgate's hosted payment page with 3D Secure, Craftgate
keeps their card, and Tahsil charges it for renewals on the POS you choose, without asking the customer again. This
is also how Tahsil takes payments through **Kuveyt Türk**: you connect your Kuveyt Türk POS in Craftgate, and Tahsil
uses it through Craftgate.

## Before you start

You need:

- A Craftgate account. For testing, ask Craftgate for a free sandbox account; its panel is
  <https://sandbox-panel.craftgate.io>, and the production panel <https://panel.craftgate.io>.
- Your API key and secret key, under **Management → API Authorization Management**.
- At least one bank POS defined in Craftgate (**POS Management**) in each currency you sell. Domestic cards pay only
  in TRY on Turkish POSes.

## 1. Connect your Kuveyt Türk POS in Craftgate

Skip this step if you use another bank's POS; the same settings apply to it.

1. Ask Kuveyt Türk for a virtual POS and its credentials (merchant number, user name and password, and the
   3D Secure keys the bank issues).
2. In Craftgate's panel, open **POS Management → Add POS**, choose **Kuveyt Türk** (integrator `KUVEYTTURK`) and enter
   the credentials. Note the POS **alias** Craftgate gives it, such as `62-kuveytturk-12`.
3. For renewals, Tahsil charges saved cards without the customer, so the POS must allow:
   - **payments without 3D Secure**: `NON_THREE_DS` among its authentication types, and **force 3D Secure** off;
   - **payments without CVC**: **enable payment without CVC** on.

   The bank must approve both. Ask Kuveyt Türk to enable non-3D Secure, CVC-less sales for stored cards on your POS,
   and Craftgate to turn the settings on once the bank agrees.

Without these settings, customers can still pay on the hosted page, but renewals fail with
`merchant_not_permitted`, and customers have to pay each renewal on the hosted invoice page.

## 2. Create the connection

A Test mode installation takes sandbox connections, and a Live mode installation takes production connections.

**In the admin panel**, open **Provider connections**, choose **Create connection**, pick **Craftgate**, and enter:

| Field | Value |
|---|---|
| Key | A name for the connection, such as `craftgate-kuveytturk` |
| API key, Secret key | Your keys. Tahsil stores them encrypted and never shows them again. |
| Merchant webhook key | From **Settings → Merchant Settings → General Settings** (step 4) |
| POS for renewals | The alias of the POS that charges saved cards, such as your Kuveyt Türk POS. Leave it empty to let Craftgate's routing choose. |

Tahsil tests the connection right after you create it. With a POS for renewals, the test also checks that the POS
exists, is active, allows payments without CVC and does not force 3D Secure, and says what is missing.

**In a provisioning file**, keep the keys in configuration, not in the file:

```json
{
  "providerConnections": [
    {
      "key": "craftgate-kuveytturk",
      "provider": "craftgate",
      "name": "Kuveyt Türk via Craftgate",
      "environment": "production",
      "currencies": ["TRY"],
      "credentials": {
        "apiKey": "${Craftgate:ApiKey}",
        "secretKey": "${Craftgate:SecretKey}",
        "webhookKey": "${Craftgate:WebhookKey}"
      },
      "settings": { "posAlias": "62-kuveytturk-12" }
    }
  ]
}
```

Then set `Craftgate__ApiKey`, `Craftgate__SecretKey` and `Craftgate__WebhookKey` in Tahsil's environment.

## 3. Route your currencies

In **Provider connections**, choose **Route currencies** and route `TRY` (and `USD`, `EUR` or `GBP` if you sell in
them and have a POS in that currency) to the Craftgate connection.

## 4. Set the webhook URL

Each connection shows its notification URL, in the form
`https://billing.example.com/provider-notifications/{connection ID}`. In Craftgate's panel, open **Settings → Merchant
Settings → General Settings**, enter it as the **Merchant Webhook URL**, and copy the **Merchant Webhook Key** into
the connection.

Tahsil checks each webhook's signature, then asks Craftgate how the payment or refund stands before it changes
anything: the signature does not cover the webhook's content. Payments are therefore settled even if a webhook is lost.

## 5. Try it

With a Test mode installation and a sandbox connection:

1. Create a checkout session for a one-time price in TRY and open its URL.
2. Pay on Craftgate's page with the test card `5209 2200 0000 0002`, any future expiry date and any CVC.
3. You return to the success URL. The invoice is paid, the order confirmation is sent, and your webhook endpoint
   receives `checkout.completed`, `invoice.paid` and `payment.succeeded`.
4. For a subscription, the card is saved; move Tahsil's test clock a month forward, and the renewal is charged on the
   POS for renewals.
5. Refund the payment in the admin panel. On the day of the payment, a full refund cancels it.

Craftgate documents more test cards for failures: `5100 0500 0000 6661` (insufficient funds), `4234 8000 0000 6661`
(CVC required, which Tahsil reports as `merchant_not_permitted`) and `5342 5300 0000 6660` (debit card that requires
3D Secure).

## Good to know

- **Refunds.** On the day of the payment, only a full refund is possible, and Craftgate cancels the payment. Part of a
  payment can be refunded from the next day. Tahsil refuses an early partial refund before calling Craftgate.
- **Renewals stay on your POS.** Tahsil asks Craftgate not to retry a declined renewal on another POS, so a renewal is
  charged where you chose.
- **Saving a card without a payment** is not available with Craftgate yet, so checkout sessions in `setup` mode and
  trials that need a card are not offered on Craftgate connections.
- **Retries are safe.** Each payment and refund carries Tahsil's reference as its idempotency key; when Craftgate does
  not answer, Tahsil sends the same request once more, and otherwise asks Craftgate later instead of guessing.
- **Failures.** Tahsil turns Craftgate's error groups into its own failure codes, such as `insufficient_funds` or
  `card_declined`, and shows customers a message in their language. Craftgate's own code and message stay available
  on the payment for support.

## Troubleshooting

| Symptom | Cause |
|---|---|
| The connection test fails with `invalid_credentials` | Wrong keys, or sandbox keys in a Live installation (or the other way round) |
| The connection test fails with `pos_not_ready` | The POS for renewals forces 3D Secure or does not allow payments without CVC (step 1) |
| Renewals fail with `merchant_not_permitted` | The same POS settings; the bank has not approved non-3D Secure, CVC-less sales |
| Renewals fail with `authentication_required` | The customer's card is a debit card that its bank requires 3D Secure for; the customer pays on the hosted invoice page |
| Payments stay `processing` after customers return | The webhook URL is missing or points elsewhere; reconciliation still settles them within minutes |
