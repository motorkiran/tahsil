# Tahsil.Client

A typed .NET client for the [Tahsil](https://github.com/motorkiran/tahsil) API.

```csharp
builder.Services.AddTahsilClient(options =>
{
    options.BaseUrl = new Uri("https://billing.example.com/");
    options.ApiKey = builder.Configuration["Tahsil:ApiKey"];
});

public sealed class BillingService(ITahsilClient tahsil)
{
    public async Task<Uri> StartCheckoutAsync(string userId, string email, CancellationToken cancellationToken)
    {
        var session = await tahsil.CheckoutSessions.CreateAsync(new CreateCheckoutSessionRequest
        {
            Mode = CheckoutMode.Subscription,
            CustomerDetails = new CheckoutCustomerDetails { ExternalId = userId, Email = email },
            Items = [new CheckoutItemRequest { Price = "pro-monthly-try" }],
            SuccessUrl = "https://app.example.com/billing?session={CHECKOUT_SESSION_ID}",
        }, cancellationToken: cancellationToken);

        return session.Url!;
    }
}
```

`ITahsilClient` has one client per API resource: `Customers`, `Products`, `Prices`, `TaxRates`, `Meters`,
`CheckoutSessions`, `PortalSessions`, `Subscriptions`, `Invoices`, `InvoiceItems`, `Payments`, `Refunds`, `Coupons`,
`PromotionCodes`, `UsageEvents`, `ProviderConnections`, `Webhooks`, `ApiKeys`, `AuditEvents`, `Provisioning` and
`Test`. The API key needs the permissions of the calls you make.

## What the client does for you

- **Idempotency.** Every `POST` carries an `Idempotency-Key`: yours (`new RequestOptions { IdempotencyKey = … }`) or a
  new one per call, kept for the client's own retries of that call. Give your own key when your code may repeat a
  call itself, such as after a crash.
- **Retries.** Reads and `POST`s are retried up to `MaxRetries` times (2 by default) after a connection error, `429`
  (after its `Retry-After`) or `503`, with backoff. Other answers are not retried.
- **Errors.** Any other answer throws `TahsilApiException` with the HTTP status, the problem's `Code`, its message,
  the invalid fields (`Errors`) and the `TraceId` to quote to your operator:

  ```csharp
  catch (TahsilApiException exception) when (exception.IsCode("subscription.kimlik_plan_conflict"))
  ```

- **Paging.** List methods return one page with `NextCursor`; the `ListAll…Async` methods follow the cursors for you
  as an `IAsyncEnumerable<T>`.
- **Concurrency.** Pass a `RequestOptions` to a read: afterwards its `ETag` holds the resource's version. Give it as
  `IfMatch` to a change, which then fails with `412` if someone changed the resource since.
- **Changes.** In `Update…Request`s, a property you do not set is not sent and keeps its value; one you set to
  `null` is sent and clears it.

`AddTahsilClient` returns the `IHttpClientBuilder`, to add handlers such as logging or resilience of your own.
