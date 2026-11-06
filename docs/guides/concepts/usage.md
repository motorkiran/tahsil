# Usage-based billing

Metered prices bill what a customer used in a period: API calls, gigabytes, active users.

1. Create a **meter**, such as `api_calls`, and say how it counts: the sum of the events' values, the number of
   events, the largest value, or the number of distinct values of a property (such as `user_id`, for active users).
2. Give a recurring price `usageType: metered` and the meter; any scheme works, typically tiered.
3. Subscribe the customer to it, alone or beside licensed prices.
4. Report usage as it happens:

```http
POST /api/v1/usage-events
{
  "events": [
    { "id": "req-7f3a-0001", "customerExternalId": "user-42", "meter": "api_calls", "value": 1 },
    { "id": "req-7f3a-0002", "customerExternalId": "user-42", "meter": "api_calls", "value": 3, "timestamp": "2026-10-09T12:00:00Z" }
  ]
}
```

A batch takes up to 1,000 events, and each is accepted, a duplicate, or rejected by itself. The `id` is yours: an
event whose `id` Tahsil has already stored is a duplicate, whatever it says, so retrying a batch is always safe.

## When usage is billed

In arrears: each renewal invoice bills the usage of the period that just ended, beside the licensed items of the
period that starts. Events reported late still count until that invoice is finalized, an hour later by default;
after that, the period is closed, and events in it are refused with `usage_event.period_closed`. A cancellation now
bills the usage so far, a subscription that ends with its period gets a final invoice of it, and a metered item
removed from a subscription bills its usage with the change. Usage during a trial is free.

A customer's usage on a meter is billed by one subscription item at a time (`subscription.meter_conflict`).

## Seeing usage

`GET /api/v1/customers/{id}/usage?meter=api_calls&interval=day` gives a customer's usage per day or month, and
`GET /api/v1/subscriptions/{id}/usage` what each metered item used this period and would bill now. The admin panel
and the customer [portal](portal.md) show them. Events are kept 400 days (`Tahsil:Usage:RetentionPeriod`), never less
than until they are billed.
