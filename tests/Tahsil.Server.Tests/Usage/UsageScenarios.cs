using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Tahsil.Contracts;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;

namespace Tahsil.Server.Tests.Usage;

/// <summary>The steps usage tests share: a catalog of metered prices, customers, subscriptions and reported events.</summary>
internal static class UsageScenarios
{
    private static readonly string[] AllPermissions = [.. Permissions, TahsilPermissions.UsageWrite, TahsilPermissions.UsageRead];

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public static object Event(string id, Guid customer, string meter, decimal? value = null, DateTimeOffset? timestamp = null, object? properties = null) =>
        new { id, customer, meter, value, timestamp, properties };

    public static async Task<ReportUsageResponse> ReportAsync(HttpClient api, params object[] events)
    {
        using var response = await api.PostJsonAsync("/api/v1/usage-events", new { events });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<ReportUsageResponse>();
    }

    public static CreateSubscriptionRequest Request(Guid customer, params string[] prices) => new()
    {
        Customer = customer,
        Items = [.. prices.Select(price => new SubscriptionItemRequest { Price = price })],
        CollectionMethod = CollectionMethod.SendInvoice,
        DaysUntilDue = 365,
    };

    public static Task<SubscriptionResponse> SubscribeAsync(HttpClient api, Guid customer, params string[] prices) => CreateAsync(api, Request(customer, prices));

    public static async Task<Guid> CustomerAsync(HttpClient api)
    {
        using var created = await api.PostJsonAsync("/api/v1/customers", new CreateCustomerRequest { Name = "Ada Lovelace", Email = $"{Guid.CreateVersion7():N}@example.com", Locale = "en" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(CancellationToken));
        return (await created.ReadAsync<CustomerResponse>()).Id;
    }

    /// <summary>
    /// A client on a catalog of metered prices: API calls (summed; the first 1,000 free, then 0.5 kuruş each, or a flat
    /// kuruş each), requests (counted, 10 kuruş), storage (the largest value, ₺1), seats (distinct users, ₺20) and
    /// traffic (summed; ₺7 per started 100), beside a licensed base price of ₺100.
    /// </summary>
    public static async Task<HttpClient> ApiAsync(WebApplicationFactory<Program> host)
    {
        var api = await host.CreateClientWithAsync(AllPermissions);
        using var provisioned = await api.PostJsonAsync("/api/v1/provisioning", new ProvisioningDocument
        {
            Meters =
            [
                new ProvisionedMeter { Key = "api_calls", Name = "API calls", Aggregation = MeterAggregation.Sum },
                new ProvisionedMeter { Key = "requests", Name = "Requests", Aggregation = MeterAggregation.Count },
                new ProvisionedMeter { Key = "storage", Name = "Storage", Aggregation = MeterAggregation.Max },
                new ProvisionedMeter { Key = "seats", Name = "Seats", Aggregation = MeterAggregation.UniqueCount, UniqueProperty = "user_id" },
                new ProvisionedMeter { Key = "traffic", Name = "Traffic", Aggregation = MeterAggregation.Sum },
            ],
            Products =
            [
                new ProvisionedProduct
                {
                    Key = "api",
                    Name = "API",
                    Prices =
                    [
                        new ProvisionedPrice { Key = "api-base-try", Name = "Base", Currency = "TRY", Type = PriceType.Recurring, Interval = PriceInterval.Month, IntervalCount = 1, UsageType = UsageType.Licensed, UnitAmount = 10_000 },
                        Metered("api-calls-try", "api_calls") with
                        {
                            BillingScheme = BillingScheme.Tiered,
                            TiersMode = TiersMode.Graduated,
                            Tiers = [new PriceTier(1_000, UnitAmount: 0), new PriceTier(null, UnitAmountDecimal: 0.5m)],
                        },
                        Metered("api-calls-flat-try", "api_calls") with { UnitAmount = 1 },
                        Metered("requests-try", "requests") with { UnitAmount = 10 },
                        Metered("storage-try", "storage") with { UnitAmount = 100 },
                        Metered("seats-try", "seats") with { UnitAmount = 2_000 },
                        Metered("traffic-try", "traffic") with { BillingScheme = BillingScheme.Package, PackageSize = 100, PackageRounding = PackageRounding.Up, UnitAmount = 700 },
                    ],
                },
            ],
        });
        provisioned.StatusCode.ShouldBe(HttpStatusCode.OK, await provisioned.Content.ReadAsStringAsync(CancellationToken));
        return api;
    }

    private static ProvisionedPrice Metered(string key, string meter) => new()
    {
        Key = key,
        Currency = "TRY",
        Type = PriceType.Recurring,
        Interval = PriceInterval.Month,
        IntervalCount = 1,
        UsageType = UsageType.Metered,
        Meter = meter,
    };
}
