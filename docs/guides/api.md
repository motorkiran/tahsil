# The Tahsil API

Applications use Tahsil through a REST API over JSON, under `/api/v1` of the installation's public URL. This page
explains what every call has in common; the [API reference](../reference/api.md) lists the endpoints, and an
installation serves its OpenAPI document at `/openapi/v1.json` with an interactive reference at `/scalar/v1` (on by
default in Test mode, off in Live mode; `Tahsil:Api:PublicDocumentation` decides). .NET applications can use
[Tahsil.Client](../../sdk/Tahsil.Client/README.md), which does all of the following for them.

## Keys and permissions

Every call carries an API key:

```http
Authorization: Bearer ths_test_…
```

Administrators create keys in the admin panel (**API keys**) or with `POST /api/v1/api-keys`; the key is shown once.
Keys of a Test mode installation start with `ths_test_`, those of a Live mode installation with `ths_live_`, and
neither works in the other. A key has permissions, such as `tahsil.customers:read` or `tahsil.subscriptions:write`;
each endpoint names the one it needs, and a call without it is refused with `403`. Give each application the
permissions it uses, and no more: a key can only create keys with permissions it holds itself.

## Bodies

- Properties are camelCase; enum values are snake_case strings (`past_due`, `charge_automatically`). Lists of values
  may grow: treat an unknown value as something you do not handle yet.
- Amounts are integers in the currency's minor units, with an ISO 4217 code: `{ "amount": 29900, "currency": "TRY" }`
  is ₺299,00.
- Times are RFC 3339 in UTC, such as `2026-10-01T09:00:00Z`.
- `PATCH` bodies are JSON Merge Patch: a property you leave out keeps its value, `null` clears it.
- Ignore properties you do not know: new ones are added without notice.

## Idempotency keys

Send an `Idempotency-Key` header, such as a UUID, with every `POST`. If the call is repeated with the same key, as
when your application retries after a timeout, Tahsil does not do it again: it answers with the first response and
`Idempotent-Replayed: true`. Keys are kept 24 hours and belong to the API key that sent them.

- The same key with another body or path is refused with `422 idempotency.key_reused`.
- A repeat that arrives while the first call still runs is told to retry, with `409 idempotency.in_progress`.
- Server errors (`5xx`) are not kept, so a retry with the same key runs the call again.

## Errors

Refusals are [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) problem details:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Conflict",
  "status": 409,
  "detail": "The customer may have one subscription with a Kimlik plan, with one such product; change that subscription's plan instead.",
  "code": "subscription.kimlik_plan_conflict",
  "traceId": "4bf92f3577b34da6a3ce929d0e0e4736"
}
```

Branch on `code`, which is stable; `detail` is for people and may change. Invalid requests (`400`) add `errors`, the
fields at fault and why. Quote the `traceId` when you ask the installation's operator about a failure.

| Status | Meaning |
|---|---|
| `400` | The request is invalid: see `code` and `errors` |
| `401` | No key, or a key that is unknown, revoked or expired |
| `403` | The key lacks the endpoint's permission |
| `404` | No such resource, or not one this key may see |
| `409` | The resource's state does not allow it, such as finalizing a paid invoice |
| `412` | The resource changed since the version in `If-Match` |
| `422` | The idempotency key was used for another request |
| `429` | Too many requests: wait as long as `Retry-After` says |
| `503` | Tahsil or the payment provider cannot answer now: retry later |

## Lists

List endpoints return a page:

```json
{ "items": [ … ], "nextCursor": "eyJpZCI6…" }
```

Pass `nextCursor` as `cursor` to get the next page, until it is `null`. `limit` sets the page size, 50 by default and
200 at most. Cursors belong to the list and filters that made them.

## Versions of a resource

Single resources answer with an `ETag`. Send it back as `If-Match` with a `PATCH`, and the change is refused with
`412 concurrency.precondition_failed` if someone changed the resource in the meantime: read it again and decide.

## Rate limits

Each API key may make 25 requests a second on average and 50 at once after a quiet period;
`POST /usage-events` has its own allowance of 10 batches a second. Over the limit, Tahsil answers
`429 rate_limited` with `Retry-After`. The limits are per instance, and the operator sets them
(`Tahsil:RateLimits`).

## Test mode

An installation in Test mode takes no real payments: its sandbox connections and the built-in simulator charge
nothing. Every response carries `Tahsil-Mode: test`, hosted pages show a banner, and `/api/v1/test` offers time travel
and the simulator's test cards (these endpoints answer `404` in Live mode). See [Getting started](getting-started.md).

## Versions of the API

The major version is in the path (`/api/v1`). Within it, changes are additive: new endpoints, new optional request
properties, new response properties, new event types and new enum values. Anything that would break a client comes
as `/api/v2`.
