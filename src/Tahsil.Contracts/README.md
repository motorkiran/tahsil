# Tahsil.Contracts

The request and response models of the [Tahsil](https://github.com/motorkiran/tahsil) API and the types of its webhook
events, without dependencies. The server and the SDK share them, so they cannot drift apart.

- On the wire, properties are camelCase and enum values snake_case (`past_due`); `TahsilJson.Configure` applies these
  conventions to your own `JsonSerializerOptions`.
- Amounts are `long` minor units with an ISO 4217 currency code: 12345 in `TRY` is ₺123,45. `TahsilMoney.Format`
  formats them for display without floating point.
- `TahsilEventTypes` names the webhook events.

Use it through `Tahsil.Client` (the API) and `Tahsil.AspNetCore` (webhooks).
