# API reference

<!-- Generated from the OpenAPI snapshot by ReferenceDocsTests; do not edit. Run the tests with TAHSIL_UPDATE_SNAPSHOTS=1 after changing the API. -->

Every endpoint of the Tahsil API, under `/api/v1`, grouped as the OpenAPI document groups them. An installation serves
the document itself at `/openapi/v1.json`, and an interactive reference at `/scalar/v1` (both on by default in Test
mode; `Tahsil:Api:PublicDocumentation` turns them on or off). Request and response bodies are described there;
[the API guide](../guides/api.md) explains authentication, idempotency keys, errors, paging and versions.

## API keys

### List API keys

`GET /api/v1/api-keys`

Lists API keys in creation order, revoked and expired ones included.

Requires the `tahsil.api_keys:read` permission.

| Parameter | Description |
|---|---|
| `cursor` | The `nextCursor` of the previous page. |
| `limit` | Page size, 50 by default and at most 200. |

Request: none. Response: `200` `PageOfApiKeyResponse`.

### Create an API key

`POST /api/v1/api-keys`

Creates a key with permissions the caller holds itself. The response is the only time the key's secret is shown.

Requires the `tahsil.api_keys:write` permission.

Request: `CreateApiKeyRequest`. Response: `201` `CreatedApiKeyResponse`.

### Get an API key

`GET /api/v1/api-keys/{id}`

Requires the `tahsil.api_keys:read` permission.

Request: none. Response: `200` `ApiKeyResponse`.

### Revoke an API key

`POST /api/v1/api-keys/{id}/revoke`

The key stops working at once, and stays listed with the time it was revoked.

Requires the `tahsil.api_keys:write` permission.

Request: none. Response: `200` `ApiKeyResponse`.

## Tax rates

### List tax rates

`GET /api/v1/tax-rates`

Requires the `tahsil.catalog:read` permission.

| Parameter | Description |
|---|---|
| `archived` | Only archived (true) or current (false) rates. |
| `cursor` | The `nextCursor` of the previous page. |
| `limit` | Page size, 50 by default and at most 200. |

Request: none. Response: `200` `PageOfTaxRateResponse`.

### Create a tax rate

`POST /api/v1/tax-rates`

The percentage and whether prices include the tax never change: to change a rate, create a new one.

Requires the `tahsil.catalog:write` permission.

Request: `CreateTaxRateRequest`. Response: `201` `TaxRateResponse`.

### Get a tax rate

`GET /api/v1/tax-rates/{idOrKey}`

Requires the `tahsil.catalog:read` permission.

Request: none. Response: `200` `TaxRateResponse`.

### Update a tax rate

`PATCH /api/v1/tax-rates/{idOrKey}`

Changes the name, description or archive flag. Archived rates cannot be given to new prices.

Requires the `tahsil.catalog:write` permission.

Request: `UpdateTaxRateRequest`. Response: `200` `TaxRateResponse`.

## Meters

### List meters

`GET /api/v1/meters`

Requires the `tahsil.catalog:read` permission.

| Parameter | Description |
|---|---|
| `archived` | Only archived (true) or current (false) meters. |
| `cursor` | The `nextCursor` of the previous page. |
| `limit` | Page size, 50 by default and at most 200. |

Request: none. Response: `200` `PageOfMeterResponse`.

### Create a meter

`POST /api/v1/meters`

The key is the name usage events report; it, the aggregation and the counted property never change.

Requires the `tahsil.catalog:write` permission.

Request: `CreateMeterRequest`. Response: `201` `MeterResponse`.

### Get a meter

`GET /api/v1/meters/{idOrKey}`

Requires the `tahsil.catalog:read` permission.

Request: none. Response: `200` `MeterResponse`.

### Update a meter

`PATCH /api/v1/meters/{idOrKey}`

Requires the `tahsil.catalog:write` permission.

Request: `UpdateMeterRequest`. Response: `200` `MeterResponse`.

## Products

### List products

`GET /api/v1/products`

Requires the `tahsil.catalog:read` permission.

| Parameter | Description |
|---|---|
| `archived` | Only archived (true) or current (false) products. |
| `q` | Matches the key or name, ignoring case. |
| `cursor` | The `nextCursor` of the previous page. |
| `limit` | Page size, 50 by default and at most 200. |

Request: none. Response: `200` `PageOfProductResponse`.

### Create a product

`POST /api/v1/products`

Requires the `tahsil.catalog:write` permission.

Request: `CreateProductRequest`. Response: `201` `ProductResponse`.

### Get a product

`GET /api/v1/products/{idOrKey}`

Requires the `tahsil.catalog:read` permission.

Request: none. Response: `200` `ProductResponse`.

### Update a product

`PATCH /api/v1/products/{idOrKey}`

JSON Merge Patch: omitted fields stay, null clears. Archiving stops new sales; existing subscriptions keep renewing.

Requires the `tahsil.catalog:write` permission.

Request: `UpdateProductRequest`. Response: `200` `ProductResponse`.

### Delete a product

`DELETE /api/v1/products/{idOrKey}`

Deletes the product and its prices while none of them was ever used; otherwise archive it.

Requires the `tahsil.catalog:write` permission.

Request: none. Response: `204`.

## Prices

### List prices

`GET /api/v1/prices`

Requires the `tahsil.catalog:read` permission.

| Parameter | Description |
|---|---|
| `product` | The product's ID or key. |
| `currency` | An ISO 4217 code, such as TRY. |
| `type` | `one_time` or `recurring`. |
| `archived` | Only archived (true) or current (false) prices. |
| `cursor` | The `nextCursor` of the previous page. |
| `limit` | Page size, 50 by default and at most 200. |

Request: none. Response: `200` `PageOfPriceResponse`.

### Create a price

`POST /api/v1/prices`

Amounts are integers in minor units (29900 is ₺299.00). Only the key, name, trial, archive flag and metadata change later: a new amount is a new price. With transferKey, the lookup key moves from the price that holds it.

Requires the `tahsil.catalog:write` permission.

Request: `CreatePriceRequest`. Response: `201` `PriceResponse`.

### Get a price

`GET /api/v1/prices/{idOrKey}`

Requires the `tahsil.catalog:read` permission.

Request: none. Response: `200` `PriceResponse`.

### Update a price

`PATCH /api/v1/prices/{idOrKey}`

Changes the lookup key, name, trial, archive flag or metadata; any other field answers price.immutable.

Requires the `tahsil.catalog:write` permission.

Request: `UpdatePriceRequest`. Response: `200` `PriceResponse`.

## Coupons

### List coupons

`GET /api/v1/coupons`

Requires the `tahsil.coupons:read` permission.

| Parameter | Description |
|---|---|
| `archived` | Only archived (true) or current (false) coupons. |
| `cursor` | The `nextCursor` of the previous page. |
| `limit` | Page size, 50 by default and at most 200. |

Request: none. Response: `200` `PageOfCouponResponse`.

### Create a coupon

`POST /api/v1/coupons`

A percentage or an amount off, once, for a number of cycles or forever. The discount, duration and products never change.

Requires the `tahsil.coupons:write` permission.

Request: `CreateCouponRequest`. Response: `201` `CouponResponse`.

### Get a coupon

`GET /api/v1/coupons/{idOrKey}`

Requires the `tahsil.coupons:read` permission.

Request: none. Response: `200` `CouponResponse`.

### Update a coupon

`PATCH /api/v1/coupons/{idOrKey}`

JSON Merge Patch of the name, limits, archive flag and metadata. Archived coupons and their codes redeem no more; existing discounts continue.

Requires the `tahsil.coupons:write` permission.

Request: `UpdateCouponRequest`. Response: `200` `CouponResponse`.

## Promotion codes

### List promotion codes

`GET /api/v1/promotion-codes`

Requires the `tahsil.coupons:read` permission.

| Parameter | Description |
|---|---|
| `coupon` | The coupon's ID or key. |
| `active` | Only active (true) or inactive (false) codes. |
| `code` | A code, ignoring case. |
| `cursor` | The `nextCursor` of the previous page. |
| `limit` | Page size, 50 by default and at most 200. |

Request: none. Response: `200` `PageOfPromotionCodeResponse`.

### Create a promotion code

`POST /api/v1/promotion-codes`

Codes are stored in uppercase and unique ignoring case across all coupons. Restrictions never change; deactivate a code and create another.

Requires the `tahsil.coupons:write` permission.

Request: `CreatePromotionCodeRequest`. Response: `201` `PromotionCodeResponse`.

### Get a promotion code

`GET /api/v1/promotion-codes/{id}`

Requires the `tahsil.coupons:read` permission.

Request: none. Response: `200` `PromotionCodeResponse`.

### Activate or deactivate a promotion code

`PATCH /api/v1/promotion-codes/{id}`

Requires the `tahsil.coupons:write` permission.

Request: `UpdatePromotionCodeRequest`. Response: `200` `PromotionCodeResponse`.

## Customers

### List and search customers

`GET /api/v1/customers`

Newest first. `email` and `externalId` match exactly (the email ignoring case); `q` matches the beginning of the email, name or external ID.

Requires the `tahsil.customers:read` permission.

| Parameter | Description |
|---|---|
| `q` | Matches the beginning of the email, name or external ID, ignoring case. |
| `email` | An email address, ignoring case. |
| `externalId` | The application's ID for the customer. |
| `cursor` | The `nextCursor` of the previous page. |
| `limit` | Page size, 50 by default and at most 200. |

Request: none. Response: `200` `PageOfCustomerResponse`.

### Create a customer

`POST /api/v1/customers`

An external ID that another customer has answers 409 customer.external_id_taken with that customer's ID in `customerId`.

Requires the `tahsil.customers:write` permission.

Request: `CreateCustomerRequest`. Response: `201` `CustomerResponse`.

### Get a customer

`GET /api/v1/customers/{id}`

Requires the `tahsil.customers:read` permission.

Request: none. Response: `200` `CustomerResponse`.

### Update a customer

`PATCH /api/v1/customers/{id}`

JSON Merge Patch: omitted fields stay, null clears.

Requires the `tahsil.customers:write` permission.

Request: `UpdateCustomerRequest`. Response: `200` `CustomerResponse`.

### Delete or anonymize a customer

`DELETE /api/v1/customers/{id}`

Deletes a customer without billing history (204). A customer with history is anonymized instead, and returned (200).

Requires the `tahsil.customers:write` permission.

Request: none. Response: `204`.

### Export a customer's data

`GET /api/v1/customers/{id}/export`

Everything Tahsil holds about the customer, for data subject requests under KVKK.

Requires the `tahsil.customers:read` permission.

Request: none. Response: `200` `CustomerExportResponse`.

### List a customer's consent records

`GET /api/v1/customers/{id}/consents`

The evidence of what the customer was shown and accepted: legal documents, the consent to future charges of a saved card, cancellations and withdrawals. Newest first. Records are kept when a customer is anonymized.

Requires the `tahsil.customers:read` permission.

| Parameter | Description |
|---|---|
| `includeDocuments` | Whether each record carries the rendered document the customer saw, as HTML. |

Request: none. Response: `200` `PageOfConsentRecordResponse`.

### Get a customer by external ID

`GET /api/v1/customers/by-external-id/{externalId}`

Requires the `tahsil.customers:read` permission.

Request: none. Response: `200` `CustomerResponse`.

### Create or update a customer by external ID

`PUT /api/v1/customers/by-external-id/{externalId}`

Creates the customer (201) or updates it as a JSON Merge Patch (200), for applications that sync customers on every sign-in.

Requires the `tahsil.customers:write` permission.

Request: `UpdateCustomerRequest`. Response: `201` `CustomerResponse`.

### List a customer's saved cards

`GET /api/v1/customers/{id}/payment-methods`

Active cards first.

Requires the `tahsil.customers:read` permission.

| Parameter | Description |
|---|---|
| `status` | `active`, `expired` or `removed`. |

Request: none. Response: `200` `PageOfPaymentMethodResponse`.

### Set a customer's default card

`POST /api/v1/customers/{id}/default-payment-method`

Requires the `tahsil.customers:write` permission.

Request: `SetDefaultPaymentMethodRequest`. Response: `200` `PaymentMethodResponse`.

### Get a saved card

`GET /api/v1/payment-methods/{id}`

Requires the `tahsil.customers:read` permission.

Request: none. Response: `200` `PaymentMethodResponse`.

### Remove a saved card

`DELETE /api/v1/payment-methods/{id}`

The card is never charged again and stops being the default; the provider is asked to delete its token.

Requires the `tahsil.customers:write` permission.

Request: none. Response: `204`.

### List a customer's balance history

`GET /api/v1/customers/{customerId}/balance-transactions`

Newest first.

Requires the `tahsil.customers:read` permission.

| Parameter | Description |
|---|---|
| `currency` | Only this currency. |
| `cursor` | The `nextCursor` of the previous page. |
| `limit` | Page size, 50 by default and at most 200. |

Request: none. Response: `200` `PageOfBalanceTransactionResponse`.

### Adjust a customer's balance

`POST /api/v1/customers/{customerId}/balance-transactions`

Positive amounts give credit that the next invoices use; negative amounts are added to the next invoice. Balances never turn into refunds.

Requires the `tahsil.invoices:write` permission.

Request: `CreateBalanceTransactionRequest`. Response: `201` `BalanceTransactionResponse`.

### Get a balance transaction

`GET /api/v1/customers/{customerId}/balance-transactions/{id}`

Requires the `tahsil.customers:read` permission.

Request: none. Response: `200` `BalanceTransactionResponse`.

## Provider connections

### List payment providers

`GET /api/v1/providers`

The providers this installation has adapters for, with the credentials and settings their connections take and what they can do.

Requires the `tahsil.connections:read` permission.

Request: none. Response: `200` `PageOfProviderResponse`.

### Get the connection of each currency

`GET /api/v1/provider-connections/routing`

Currency codes and the keys of the connections that take customer-present payments in them.

Requires the `tahsil.connections:read` permission.

Request: none. Response: `200`.

### Set the connection of each currency

`PUT /api/v1/provider-connections/routing`

Replaces the routing, such as `{ "TRY": "iyzico-main", "USD": "stripe-main" }`. A connection serves only currencies its provider supports, and only while enabled; saved cards are always charged through the connection that saved them.

Requires the `tahsil.connections:write` permission.

Request: none. Response: `200`.

### List provider connections

`GET /api/v1/provider-connections`

Requires the `tahsil.connections:read` permission.

| Parameter | Description |
|---|---|
| `provider` | Only connections to this provider. |
| `enabled` | Only enabled (true) or disabled (false) connections. |
| `cursor` | The `nextCursor` of the previous page. |
| `limit` | Page size, 50 by default and at most 200. |

Request: none. Response: `200` `PageOfConnectionResponse`.

### Create a provider connection

`POST /api/v1/provider-connections`

Credentials are validated against the provider's fields, stored encrypted and never returned. Test mode takes sandbox connections, Live mode production ones.

Requires the `tahsil.connections:write` permission.

Request: `CreateConnectionRequest`. Response: `201` `ConnectionResponse`.

### Get a provider connection

`GET /api/v1/provider-connections/{idOrKey}`

Requires the `tahsil.connections:read` permission.

Request: none. Response: `200` `ConnectionResponse`.

### Update a provider connection

`PATCH /api/v1/provider-connections/{idOrKey}`

Credentials and settings merge with the current ones. A disabled connection takes no new payments, and renewals of its cards fail until it is enabled again.

Requires the `tahsil.connections:write` permission.

Request: `UpdateConnectionRequest`. Response: `200` `ConnectionResponse`.

### Delete a provider connection

`DELETE /api/v1/provider-connections/{idOrKey}`

Only while the connection has no payments or payment methods; otherwise disable it.

Requires the `tahsil.connections:write` permission.

Request: none. Response: `204`.

### Test a provider connection

`POST /api/v1/provider-connections/{idOrKey}/test`

Asks the provider whether the credentials work and records the result as `lastTest`.

Requires the `tahsil.connections:write` permission.

Request: none. Response: `200` `ConnectionResponse`.

## Invoices

### Pay an invoice with a saved card

`POST /api/v1/invoices/{id}/pay`

Charges one of the customer's saved cards now, off session, whatever the invoice's schedule. The payment's status says how it ended; `processing` means the provider's answer is still being confirmed.

Requires the `tahsil.invoices:write` permission.

Request: `PayInvoiceRequest`. Response: `201` `PaymentResponse`.

### List invoices

`GET /api/v1/invoices`

Newest first.

Requires the `tahsil.invoices:read` permission.

| Parameter | Description |
|---|---|
| `customer` | Only this customer's invoices. |
| `status` | `draft`, `open`, `paid`, `void` or `uncollectible`. |
| `number` | An invoice number, such as `INV-2026-000123`. |
| `subscription` | Only this subscription's invoices. |
| `dunning` | Only unpaid invoices in dunning: after a failed automatic payment or a passed due date, before the final action. |
| `overdue` | Only invoices sent for payment whose due date passed unpaid. |
| `cursor` | The `nextCursor` of the previous page. |
| `limit` | Page size, 50 by default and at most 200. |

Request: none. Response: `200` `PageOfInvoiceResponse`.

### Create a draft invoice

`POST /api/v1/invoices`

Lines are one-time prices in the invoice's currency, or descriptions with unit amounts (negative for credits). The draft changes freely until it is finalized.

Requires the `tahsil.invoices:write` permission.

Request: `CreateInvoiceRequest`. Response: `201` `InvoiceResponse`.

### Preview a subscription's next renewal invoice

`GET /api/v1/invoices/upcoming`

The invoice the next renewal would create as things stand, with a scheduled change that takes effect then, the metered usage so far and the subscription's pending items; nothing is created.

Requires the `tahsil.invoices:read` permission.

| Parameter | Description |
|---|---|
| `subscription` (required) | The subscription whose next renewal to preview. |

Request: none. Response: `200` `InvoicePreviewResponse`.

### Get an invoice

`GET /api/v1/invoices/{id}`

Requires the `tahsil.invoices:read` permission.

Request: none. Response: `200` `InvoiceResponse`.

### Update an invoice

`PATCH /api/v1/invoices/{id}`

Drafts: the collection method, days until due, memo and metadata. Finalized invoices: the legal invoice the application issued, and metadata.

Requires the `tahsil.invoices:write` permission.

Request: `UpdateInvoiceRequest`. Response: `200` `InvoiceResponse`.

### Delete a draft invoice

`DELETE /api/v1/invoices/{id}`

Its pending items wait for another invoice. Finalized invoices are voided instead.

Requires the `tahsil.invoices:write` permission.

Request: none. Response: `204`.

### Email an invoice again

`POST /api/v1/invoices/{id}/send`

Emails a finalized, unpaid invoice sent for payment to its customer again, with its hosted page link; at most once every ten minutes.

Requires the `tahsil.invoices:write` permission.

Request: none. Response: `200` `InvoiceResponse`.

### Add lines to a draft invoice

`POST /api/v1/invoices/{id}/lines`

Requires the `tahsil.invoices:write` permission.

Request: `AddInvoiceLinesRequest`. Response: `200` `InvoiceResponse`.

### Remove a line from a draft invoice

`DELETE /api/v1/invoices/{id}/lines/{lineId}`

Requires the `tahsil.invoices:write` permission.

Request: none. Response: `200` `InvoiceResponse`.

### Finalize a draft invoice

`POST /api/v1/invoices/{id}/finalize`

Assigns the number, takes the customer's billing details and applies the customer's balance. An invoice with nothing left to pay is paid at once.

Requires the `tahsil.invoices:write` permission.

Request: none. Response: `200` `InvoiceResponse`.

### Void an invoice

`POST /api/v1/invoices/{id}/void`

For open and uncollectible invoices without payments. The customer's balance gets back what finalization took from it or added to it.

Requires the `tahsil.invoices:write` permission.

Request: none. Response: `200` `InvoiceResponse`.

### Mark an invoice uncollectible

`POST /api/v1/invoices/{id}/mark-uncollectible`

Stops collection; the customer can still pay it.

Requires the `tahsil.invoices:write` permission.

Request: none. Response: `200` `InvoiceResponse`.

### List invoice items

`GET /api/v1/invoice-items`

Requires the `tahsil.invoices:read` permission.

| Parameter | Description |
|---|---|
| `customer` | Only this customer's items. |
| `pending` | Only items no invoice has taken yet (true), or only invoiced ones (false). |
| `cursor` | The `nextCursor` of the previous page. |
| `limit` | Page size, 50 by default and at most 200. |

Request: none. Response: `200` `PageOfInvoiceItemResponse`.

### Create a pending invoice item

`POST /api/v1/invoice-items`

A charge or credit that joins the customer's next manual invoice created with `includePendingItems`.

Requires the `tahsil.invoices:write` permission.

Request: `CreateInvoiceItemRequest`. Response: `201` `InvoiceItemResponse`.

### Get an invoice item

`GET /api/v1/invoice-items/{id}`

Requires the `tahsil.invoices:read` permission.

Request: none. Response: `200` `InvoiceItemResponse`.

### Delete a pending invoice item

`DELETE /api/v1/invoice-items/{id}`

Only while no invoice includes it.

Requires the `tahsil.invoices:write` permission.

Request: none. Response: `204`.

## Usage

### Report usage events

`POST /api/v1/usage-events`

Stores a batch of usage events for metered prices. Each event is accepted, a duplicate or rejected by itself, and the answer says which: an event whose `id` is stored already is a duplicate whatever it says, so retry a batch with the same IDs. An event of a period that is already invoiced is rejected with `usage_event.period_closed`. This endpoint has a rate limit of its own.

Requires the `tahsil.usage:write` permission.

Request: `ReportUsageRequest`. Response: `200` `ReportUsageResponse`.

### Get a customer's usage on a meter

`GET /api/v1/customers/{id}/usage`

The quantity of the range and of each day or month in it, each aggregated the meter's way, whether a subscription bills the usage or not. At most 400 intervals.

Requires the `tahsil.usage:read` permission.

| Parameter | Description |
|---|---|
| `meter` | The meter, by ID or key. |
| `from` | The start of the range; 30 days before `to` by default, or 12 months for months. |
| `to` | The end of the range, which is not part of it; now by default. |
| `interval` | `day` (the default) or `month`, in the billing time zone. |

Request: none. Response: `200` `CustomerUsageResponse`.

### Get a subscription's usage in its current period

`GET /api/v1/subscriptions/{id}/usage`

For each metered item: the quantity used in the current period so far and the amount it would bill now, before discounts and tax that is added.

Requires the `tahsil.usage:read` permission.

Request: none. Response: `200` `SubscriptionUsageResponse`.

## Audit events

### Query the audit trail

`GET /api/v1/audit-events`

Lists audit events, newest first. Every filter is optional, and filters combine.

Requires the `tahsil.audit:read` permission.

| Parameter | Description |
|---|---|
| `action` | An action such as `api_key.revoked`. |
| `actorType` | `system`, `api_key`, `admin` or `customer`. |
| `actorId` | An API key ID, administrator subject or customer ID. |
| `subjectType` | What was acted on, such as `customer` or `invoice`. |
| `subjectId` | The ID of what was acted on. |
| `customerId` | Events concerning this customer. |
| `from` | Events at or after this time (ISO 8601). |
| `to` | Events before this time (ISO 8601). |
| `cursor` | The `nextCursor` of the previous page. |
| `limit` | Page size, 50 by default and at most 200. |

Request: none. Response: `200` `PageOfAuditEventResponse`.

## Payments

### List payments

`GET /api/v1/payments`

Newest first.

Requires the `tahsil.payments:read` permission.

| Parameter | Description |
|---|---|
| `customer` | Only this customer's payments. |
| `invoice` | Only payments of this invoice. |
| `status` | `processing`, `requires_action`, `succeeded`, `failed` or `canceled`. |
| `connection` | Only payments through this provider connection. |
| `failureCode` | Only payments that failed with this code, such as `insufficient_funds`. |
| `cursor` | The `nextCursor` of the previous page. |
| `limit` | Page size, 50 by default and at most 200. |

Request: none. Response: `200` `PageOfPaymentResponse`.

### Get a payment

`GET /api/v1/payments/{id}`

Requires the `tahsil.payments:read` permission.

Request: none. Response: `200` `PaymentResponse`.

## Refunds

### Refund a payment

`POST /api/v1/refunds`

Refunds what is left of a succeeded payment, or part of it. The provider may answer later: `processing` refunds settle by themselves. Refunds never change the status of the payment or its invoice.

Requires the `tahsil.refunds:write` permission.

Request: `CreateRefundRequest`. Response: `201` `RefundResponse`.

### List refunds

`GET /api/v1/refunds`

Newest first.

Requires the `tahsil.payments:read` permission.

| Parameter | Description |
|---|---|
| `payment` | Only refunds of this payment. |
| `status` | `processing`, `succeeded` or `failed`. |
| `cursor` | The `nextCursor` of the previous page. |
| `limit` | Page size, 50 by default and at most 200. |

Request: none. Response: `200` `PageOfRefundResponse`.

### Get a refund

`GET /api/v1/refunds/{id}`

Requires the `tahsil.payments:read` permission.

Request: none. Response: `200` `RefundResponse`.

## Webhooks

### List the event types

`GET /api/v1/webhooks/event-types`

Requires the `tahsil.webhooks:read` permission.

Request: none. Response: `200` `PageOfEventTypeResponse`.

### List webhook endpoints

`GET /api/v1/webhooks/endpoints`

Requires the `tahsil.webhooks:read` permission.

| Parameter | Description |
|---|---|
| `cursor` | The `nextCursor` of the previous page. |
| `limit` | Page size, 50 by default and at most 200. |

Request: none. Response: `200` `PageOfWebhookEndpointResponse`.

### Create a webhook endpoint

`POST /api/v1/webhooks/endpoints`

Returns the signing secret once. Events are signed as Standard Webhooks describes; an empty list of event types means all.

Requires the `tahsil.webhooks:write` permission.

Request: `CreateWebhookEndpointRequest`. Response: `201` `CreatedWebhookEndpointResponse`.

### Get a webhook endpoint

`GET /api/v1/webhooks/endpoints/{id}`

Requires the `tahsil.webhooks:read` permission.

Request: none. Response: `200` `WebhookEndpointResponse`.

### Update a webhook endpoint

`PATCH /api/v1/webhooks/endpoints/{id}`

Requires the `tahsil.webhooks:write` permission.

Request: `UpdateWebhookEndpointRequest`. Response: `200` `WebhookEndpointResponse`.

### Delete a webhook endpoint

`DELETE /api/v1/webhooks/endpoints/{id}`

Deletes the endpoint with its delivery log; deliveries under way are dropped.

Requires the `tahsil.webhooks:write` permission.

Request: none. Response: `204`.

### Rotate a webhook endpoint's secret

`POST /api/v1/webhooks/endpoints/{id}/secret`

Returns the new secret once. Until `previousSecretExpiresAt`, deliveries carry a signature with each secret, so receivers can switch without losing events.

Requires the `tahsil.webhooks:write` permission.

Request: none. Response: `200` `WebhookSecretResponse`.

### Send a test event

`POST /api/v1/webhooks/endpoints/{id}/test`

Sends a `webhook.test` event to the endpoint, even when it is disabled.

Requires the `tahsil.webhooks:write` permission.

Request: none. Response: `200` `WebhookDeliveryResponse`.

### List webhook deliveries

`GET /api/v1/webhooks/deliveries`

Newest first.

Requires the `tahsil.webhooks:read` permission.

| Parameter | Description |
|---|---|
| `endpoint` | Only deliveries to this endpoint. |
| `status` | `pending`, `succeeded` or `failed`. |
| `eventType` | Only deliveries of this event type. |
| `cursor` | The `nextCursor` of the previous page. |
| `limit` | Page size, 50 by default and at most 200. |

Request: none. Response: `200` `PageOfWebhookDeliveryResponse`.

### Get a webhook delivery

`GET /api/v1/webhooks/deliveries/{id}`

Requires the `tahsil.webhooks:read` permission.

Request: none. Response: `200` `WebhookDeliveryResponse`.

### Send a delivery again

`POST /api/v1/webhooks/deliveries/{id}/redeliver`

Sends the event again with the same event ID, as a fresh series of attempts.

Requires the `tahsil.webhooks:write` permission.

Request: none. Response: `200` `WebhookDeliveryResponse`.

## Checkout

### Create a checkout session

`POST /api/v1/checkout-sessions`

Prices and the amount due are computed now and shown on the page as the `quote`; send the customer to `url`. The session expires unpaid after `expiresIn` (24 hours by default).

Requires the `tahsil.checkout:write` permission.

Request: `CreateCheckoutSessionRequest`. Response: `201` `CheckoutSessionResponse`.

### List checkout sessions

`GET /api/v1/checkout-sessions`

Newest first.

Requires the `tahsil.subscriptions:read` permission.

| Parameter | Description |
|---|---|
| `status` | `open`, `complete` or `expired`. |
| `customer` | Only sessions of this customer. |
| `clientReferenceId` | Only sessions with this client reference ID. |
| `cursor` | The `nextCursor` of the previous page. |
| `limit` | Page size, 50 by default and at most 200. |

Request: none. Response: `200` `PageOfCheckoutSessionResponse`.

### Get a checkout session

`GET /api/v1/checkout-sessions/{id}`

`url` is given while the session is open.

Requires the `tahsil.subscriptions:read` permission.

Request: none. Response: `200` `CheckoutSessionResponse`.

### Expire a checkout session

`POST /api/v1/checkout-sessions/{id}/expire`

Ends an open session so it can no longer be paid; `checkout.expired` is sent.

Requires the `tahsil.checkout:write` permission.

Request: none. Response: `200` `CheckoutSessionResponse`.

## Customer portal

### Create a portal session

`POST /api/v1/portal-sessions`

For a customer the application has authenticated: redirect them to `url`, where they manage their subscriptions, card, invoices and billing details. The session works for that customer alone, as often as they like until `expiresAt` (`Tahsil:Portal:SessionLifetime`, an hour by default). `url` is given now only; create another session when it is lost. `flow` opens the portal on one task.

Requires the `tahsil.checkout:write` permission.

Request: `CreatePortalSessionRequest`. Response: `201` `PortalSessionResponse`.

## Subscriptions

### Create a subscription

`POST /api/v1/subscriptions`

With a trial, the subscription starts `trialing` and nothing is charged. Otherwise its first invoice is finalized at once: charged to the card during the request with `charge_automatically` (`active` when paid; `incomplete` when declined, with `latestInvoice.hostedUrl` for the customer to pay within a day), or sent with `send_invoice` (`active` at once).

Requires the `tahsil.subscriptions:write` permission.

Request: `CreateSubscriptionRequest`. Response: `201` `SubscriptionResponse`.

### List subscriptions

`GET /api/v1/subscriptions`

Newest first.

Requires the `tahsil.subscriptions:read` permission.

| Parameter | Description |
|---|---|
| `customer` | Only subscriptions of this customer. |
| `status` | `incomplete`, `incomplete_expired`, `trialing`, `active`, `past_due` or `canceled`. |
| `price` | Only subscriptions with this price, by ID or lookup key. |
| `product` | Only subscriptions with a price of this product, by ID or key. |
| `cursor` | The `nextCursor` of the previous page. |
| `limit` | Page size, 50 by default and at most 200. |

Request: none. Response: `200` `PageOfSubscriptionResponse`.

### Get a subscription

`GET /api/v1/subscriptions/{id}`

Requires the `tahsil.subscriptions:read` permission.

Request: none. Response: `200` `SubscriptionResponse`.

### Change a subscription

`PATCH /api/v1/subscriptions/{id}`

`items` replaces the item set. Changed `now` (the default), it is prorated by `prorationBehavior`: `always_invoice` bills the difference at once and charges it like a first invoice (a credit goes to the customer's balance), `create_prorations` leaves it for the next renewal, `none` bills nothing until then. With `effective: period_end` (and optionally a later renewal as `effectiveAt`) it is stored as `scheduledChange` and applied at that renewal. Prices with another interval restart the period now. A higher price for an individual customer must be scheduled at least `Tahsil:Subscriptions:PriceIncreaseNotice` ahead.

Requires the `tahsil.subscriptions:write` permission.

Request: `UpdateSubscriptionRequest`. Response: `200` `SubscriptionResponse`.

### Cancel a subscription

`POST /api/v1/subscriptions/{id}/cancel`

`at: period_end` (the default) or a time within the current period schedules the end, which `resume` takes back; `at: now` ends it at once, bills its pending items on a final invoice unless `invoiceNow` is false, and may refund the unused share of the period (`prorated`) or the last payment in full (`last_payment`). A refund that fails does not undo the cancellation; it becomes an attention item or a `refund.failed` event.

Requires the `tahsil.subscriptions:write` permission.

Request: `CancelSubscriptionRequest`. Response: `200` `SubscriptionResponse`.

### Take back a scheduled cancellation

`POST /api/v1/subscriptions/{id}/resume`

Requires the `tahsil.subscriptions:write` permission.

Request: none. Response: `200` `SubscriptionResponse`.

### Remove a subscription's discount

`DELETE /api/v1/subscriptions/{id}/discount`

Invoices created afterwards are not discounted; a draft still open drops the discount when it is finalized. The same as `PATCH` with `coupon: null`.

Requires the `tahsil.subscriptions:write` permission.

Request: none. Response: `200` `SubscriptionResponse`.

### Preview a change of a subscription

`POST /api/v1/subscriptions/{id}/preview`

Takes the body of a change of `items`, with a `coupon` or `promotionCode` to discount it by, and returns the invoice it would create now (lines, totals, tax and the balance it would apply), the lines it would leave for the next renewal, and the next renewal, without changing or redeeming anything.

Requires the `tahsil.subscriptions:read` permission.

Request: `UpdateSubscriptionRequest`. Response: `200` `SubscriptionPreviewResponse`.

## Provisioning

### Export the provisioning document

`GET /api/v1/provisioning`

The current catalog, ordered by key, in the format that POST applies.

Requires the `tahsil.provisioning:read` permission.

Request: none. Response: `200` `ProvisioningDocument`.

### Apply a provisioning document

`POST /api/v1/provisioning`

Creates what is missing and updates what differs, in one transaction, and never deletes. Each item goes through the same rules as the API, with the caller's permissions; an error names the item and nothing is applied.

Requires the `tahsil.provisioning:write` permission.

| Parameter | Description |
|---|---|
| `dryRun` | Validates the document and reports what would change, without changing anything. |

Request: `ProvisioningDocument`. Response: `200` `ProvisioningResult`.

## Test mode

### Read the billing clock

`GET /api/v1/test/clock`

Requires the `tahsil.test:write` permission.

Request: none. Response: `200` `TestClockResponse`.

### Travel forward in time

`POST /api/v1/test/clock`

Test mode only: moves the billing clock forward by `advanceBy` (an ISO 8601 duration such as `P1M`) or to `advanceTo`, at most 400 days, and processes everything that falls due on the way, at its own instant. Returns when done.

Requires the `tahsil.test:write` permission.

Request: `AdvanceClockRequest`. Response: `200` `TimeTravelResponse`.

### Save a simulator test card for a customer

`POST /api/v1/test/simulator/payment-methods`

Test mode only: a saved card without a browser, with a consent record marked as made by the test API.

Requires the `tahsil.test:write` permission.

Request: `CreateSimulatorPaymentMethodRequest`. Response: `201` `PaymentMethodResponse`.

### Change how a simulator card answers

`PATCH /api/v1/test/simulator/payment-methods/{id}`

Test mode only: `succeed`, a failure code such as `insufficient_funds`, `timeout_after_charge`, `timeout_before_charge` or `pending`.

Requires the `tahsil.test:write` permission.

Request: `UpdateSimulatorPaymentMethodRequest`. Response: `200` `PaymentMethodResponse`.
