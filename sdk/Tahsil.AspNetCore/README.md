# Tahsil.AspNetCore

[Tahsil](https://github.com/motorkiran/tahsil) webhooks in ASP.NET Core.

```csharp
builder.Services.AddTahsilWebhooks();

app.MapTahsilWebhooks("/webhooks/tahsil", builder.Configuration["Tahsil:WebhookSecret"]!, async (TahsilEvent webhookEvent, CancellationToken cancellationToken) =>
{
    if (webhookEvent.Type == TahsilEventTypes.InvoicePaid)
    {
        var details = webhookEvent.DetailsAs<InvoiceDetails>()!;
        // details.Number, details.AmountPaid, details.SubscriptionId, …
    }
});
```

- **Verification.** The endpoint reads the raw body once and checks it as the
  [Standard Webhooks](https://www.standardwebhooks.com) specification describes: a signature that matches the
  endpoint's `whsec_…` secret and a timestamp within five minutes. Anything else gets `401` and never reaches your
  handler. While you rotate the secret in Tahsil, pass both secrets: Tahsil signs with both for 24 hours.
- **Events.** `TahsilEvent` has the event's `Type` (see `TahsilEventTypes`), `Timestamp`, `Mode` (`test` or `live`) and
  `Data`: the subject, the customer, the actor and the details, which `DetailsAs<T>()` reads as their record
  (`InvoiceDetails`, `PaymentDetails`, `SubscriptionCanceledDetails`, …). Events are thin: fetch the subject with
  `Tahsil.Client` when you need its current state.
- **Once.** Tahsil delivers at least once and retries for about 45 hours. The endpoint skips a webhook whose
  `webhook-id` it handled already, and a handler that throws makes Tahsil deliver again. The default deduplicator keeps
  IDs in memory, which suits one instance; with several, implement `ITahsilWebhookDeduplicator` over storage every
  instance shares, such as the database your handler writes to, and register it.

Without the endpoint helper, `TahsilWebhook.ReadAsync(request, secret)` and `TahsilWebhook.TryVerify(headers, body,
secrets, out var webhookEvent)` verify a request you read yourself.
