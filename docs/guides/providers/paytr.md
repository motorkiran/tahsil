# Taking payments with PayTR

This guide connects a Tahsil installation to [PayTR](https://www.paytr.com). PayTR offers two ways to take cards,
and the one a connection uses decides what Tahsil can do with it:

| | iFrame mode (default) | Direct form mode |
|---|---|---|
| Where the card is entered | In PayTR's iframe on Tahsil's page | In card fields on Tahsil's page that post straight to PayTR |
| Your PCI DSS scope | SAQ A | **SAQ A-EP** |
| Cards saved | No | Yes |
| Subscriptions | Each renewal is emailed as an invoice the customer pays | Renewals are charged to the saved card |
| PayTR permissions | None beyond a store | Direkt API, Non3D and recurring payment |

Start with iFrame mode. Move to direct form mode only if you need renewals charged automatically and accept the
wider PCI scope: your pages then host the card fields, even though card numbers never reach Tahsil's server.

## Before you start

You need:

- A PayTR store (apply at <https://www.paytr.com/uye-isyeri-olun>). A new store runs in test mode until PayTR
  approves it for live payments.
- Your **Mağaza No** (merchant ID), **Mağaza Parola** (merchant key) and **Mağaza Gizli Anahtar** (merchant salt), in
  PayTR's panel under **Destek & Kurulum → Entegrasyon Bilgileri**. Only the store's main and technical users see
  them.
- For USD or EUR, PayTR's confirmation that your store takes foreign currencies.
- For direct form mode only: ask PayTR (**Destek & Kurulum → Destek**) for the **Direkt API**, **Non3D** and
  **recurring payment** permissions. PayTR reviews each separately.

## 1. Create the connection

A Test mode installation takes sandbox connections, which send PayTR's `test_mode=1`; a Live mode installation takes
production connections.

**In the admin panel**, open **Provider connections**, choose **Create connection**, pick **PayTR**, and enter:

| Field | Value |
|---|---|
| Key | A name for the connection, such as `paytr-main` |
| Merchant ID, Merchant key, Merchant salt | From PayTR's panel. Tahsil stores the key and the salt encrypted and never sends them to anyone, PayTR included: they only sign requests. |
| Address and phone for customers without one | PayTR requires an address and a phone number with every payment. Tahsil sends the customer's when it has them, and these values otherwise. |
| Card fields on Tahsil's page | Off for iFrame mode. Turn it on for direct form mode, once PayTR has given the three permissions. |

Tahsil tests the connection right after you create it. "Check the merchant key and salt" means one of them is wrong.

**In a provisioning file**, keep the secrets in configuration, not in the file:

```json
{
  "providerConnections": [
    {
      "key": "paytr-main",
      "provider": "paytr",
      "name": "PayTR",
      "environment": "production",
      "currencies": ["TRY"],
      "credentials": {
        "merchantId": "${PayTR:MerchantId}",
        "merchantKey": "${PayTR:MerchantKey}",
        "merchantSalt": "${PayTR:MerchantSalt}"
      }
    }
  ]
}
```

Then set `PayTR__MerchantId`, `PayTR__MerchantKey` and `PayTR__MerchantSalt` in Tahsil's environment.

## 2. Route your currencies

In **Provider connections**, choose **Route currencies** and route `TRY` (and `USD` or `EUR` if PayTR enabled them)
to the PayTR connection.

## 3. Set the notification URL

Each connection shows its notification URL, in the form
`https://billing.example.com/provider-notifications/{connection ID}`. In PayTR's panel, open **Destek & Kurulum →
Ayarlar → Bildirim URL Ayarları** and enter it. It must be HTTPS, and nothing may stand between PayTR and it: no bot
protection, no firewall challenge, no sign-in.

PayTR pays you only for payments whose notification was answered `OK`, and keeps sending a notification every minute
until it is. Tahsil checks each notification's signature, answers `OK`, and confirms a success with PayTR before it
marks anything paid. A store has one notification URL: if another system shares your store, Tahsil receives its
notifications too, answers them `OK` and otherwise ignores them.

## 4. Try it

With a Test mode installation, a store in test mode and a sandbox connection:

1. Create a checkout session for a one-time price in TRY and open its URL.
2. Pay in PayTR's iframe with the test card `4355 0843 5508 4358`, expiry `12/30` and CVC `000`.
3. You return to the success URL. The invoice is paid, the order confirmation is sent, and your webhook endpoint
   receives `checkout.completed`, `invoice.paid` and `payment.succeeded`.
4. Refund the payment, wholly or in part, in the admin panel.

## Subscriptions in iFrame mode

PayTR's iframe cannot save cards, so Tahsil cannot charge renewals by itself on an iFrame connection. A subscription
bought there is collected invoice by invoice: each renewal is emailed to the customer with a link to its invoice
page, where they pay in PayTR's iframe. The checkout says so before the customer subscribes. Trials that need a card
are not offered.

## Direct form mode

With **Card fields on Tahsil's page** on:

- The checkout and invoice pages show card fields that post straight to PayTR (`https://www.paytr.com/odeme`). The
  response that shows them runs no scripts at all, and its content security policy lets the form post only to PayTR.
  Card numbers never reach Tahsil's server, but because your pages host the fields, PCI DSS puts you in SAQ A-EP.
  In Live mode, Tahsil logs a warning at startup for every such connection.
- PayTR stores the card when the customer agrees to it at checkout, and renewals are charged to it without
  3D Secure. One charge runs per card at a time.
- A connection keeps its mode while it has saved cards: turning the setting on or off is refused then
  (`connection.mode_change_with_cards`).

What to know before relying on it: PayTR's documentation leaves some of this mode's behavior open, and Tahsil has not
yet been run against a PayTR store with the three permissions. In particular, Tahsil learns which card PayTR stored
from PayTR's notification and its card list; if PayTR does not name the stored card there, the payment still
succeeds, but the card is not saved and the customer is asked for it again. Check a first subscription and its
renewal in test mode before going live.

## Good to know

- **Refunds.** Any part of a payment can be refunded, up to a year after it. PayTR accepts a refund only once the
  payment's notification has been answered; until then, Tahsil keeps the refund pending and tries again.
- **Failed payments.** PayTR reports only successful payments when asked; Tahsil learns of a failure from PayTR's
  notification, or treats a payment that never succeeded as abandoned when its time is up.
- **Amounts.** PayTR takes at least ₺1.00 per payment. Installments are turned off.
- **Failures.** Tahsil turns PayTR's failure codes and the bank's message into its own failure codes, such as
  `insufficient_funds` or `card_declined`. The bank's own message stays available on the payment for support.

## Troubleshooting

| Symptom | Cause |
|---|---|
| The connection test says to check the merchant key and salt | One of them is wrong, or belongs to another store |
| The connection test says the request was refused | A wrong merchant ID, or the store is not active yet |
| Payments stay `processing` and PayTR's panel shows notifications waiting | The notification URL is missing or blocked: PayTR must reach it without any challenge |
| Refunds stay pending | The payment's notification has not been answered `OK` yet; see the row above |
| Renewals fail with `merchant_not_permitted` (direct form mode) | PayTR has not given the recurring payment or Non3D permission |
