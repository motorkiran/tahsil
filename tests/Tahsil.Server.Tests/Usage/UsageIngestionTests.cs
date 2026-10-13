using System.Net;
using Microsoft.EntityFrameworkCore;
using Tahsil.Contracts;

namespace Tahsil.Server.Tests.Usage;

/// <summary>Reporting usage events in batches (USG-1 to USG-6).</summary>
public sealed class UsageIngestionTests(TahsilServerFixture server)
{
    private static readonly string[] Permissions =
    [
        TahsilPermissions.UsageWrite, TahsilPermissions.CatalogWrite, TahsilPermissions.CustomersWrite, TahsilPermissions.ProvisioningWrite,
        TahsilPermissions.SubscriptionsWrite, TahsilPermissions.ApiKeysRead,
    ];

    private readonly string _run = Guid.CreateVersion7().ToString("N")[20..];

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "USG-1")]
    [Trait("Requirement", "USG-3")]
    [Trait("Requirement", "USG-4")]
    [Trait("Requirement", "CAT-17")]
    public async Task EveryEvent_IsAcceptedOrRejectedByItself()
    {
        using var api = await server.CreateClientWithAsync(Permissions);
        var calls = await MeterAsync(api, "calls", MeterAggregation.Sum);
        var requests = await MeterAsync(api, "requests", MeterAggregation.Count);
        var seats = await MeterAsync(api, "seats", MeterAggregation.UniqueCount, "user_id");
        var retired = await MeterAsync(api, "retired", MeterAggregation.Sum);
        (await api.PatchJsonAsync($"/api/v1/meters/{retired}", new UpdateMeterRequest { IsArchived = true })).EnsureSuccessStatusCode().Dispose();
        var (customer, externalId) = await CustomerAsync(api);
        var happened = DateTimeOffset.UtcNow.AddHours(-3);

        var answer = await ReportAsync(
            api,
            new { id = Id("sum"), customer, meter = calls, value = 12.5m, timestamp = happened },
            new { id = Id("count"), customerExternalId = externalId, meter = requests, value = 40 },
            new { id = Id("seat"), customer, meter = seats, properties = new { user_id = "u-9", plan = "pro" } },
            new { id = "not valid!", customer, meter = calls },
            new { id = Id("no-meter"), customer, meter = $"missing_{_run}" },
            new { id = Id("archived"), customer, meter = retired },
            new { id = Id("no-customer"), customer = Guid.CreateVersion7(), meter = calls },
            new { id = Id("both"), customer, customerExternalId = externalId, meter = calls },
            new { id = Id("future"), customer, meter = calls, timestamp = DateTimeOffset.UtcNow.AddHours(1) },
            new { id = Id("negative"), customer, meter = calls, value = -1 },
            new { id = Id("precise"), customer, meter = calls, value = 0.123456789m },
            new { id = Id("no-seat"), customer, meter = seats, properties = new { user_id = 9 } },
            new { id = Id("large"), customer, meter = calls, properties = new { note = new string('x', 4100) } });

        answer.Accepted.ShouldBe(3);
        answer.Duplicates.ShouldBeEmpty();
        answer.Rejected.Select(rejected => (rejected.Id, rejected.Index, rejected.Code)).ShouldBe(
        [
            ("not valid!", 3, "usage_event.invalid"),
            (Id("no-meter"), 4, "usage_event.meter_not_found"),
            (Id("archived"), 5, "usage_event.meter_archived"),
            (Id("no-customer"), 6, "usage_event.customer_not_found"),
            (Id("both"), 7, "usage_event.invalid"),
            (Id("future"), 8, "usage_event.future_timestamp"),
            (Id("negative"), 9, "usage_event.invalid"),
            (Id("precise"), 10, "usage_event.invalid"),
            (Id("no-seat"), 11, "usage_event.missing_unique_property"),
            (Id("large"), 12, "usage_event.invalid"),
        ]);
        answer.Rejected.ShouldAllBe(rejected => rejected.Message.Length > 0);

        var stored = await server.QueryDatabaseAsync(context => context.UsageEvents.Where(usage => usage.CustomerId == customer).OrderBy(usage => usage.Identifier).ToListAsync());
        stored.Select(usage => (usage.Identifier, usage.Value)).ShouldBe([(Id("count"), 1m), (Id("seat"), 1m), (Id("sum"), 12.5m)], "a meter that counts events ignores their value");
        stored.Single(usage => usage.Identifier == Id("sum")).OccurredAt.ShouldBe(happened, TimeSpan.FromMilliseconds(1));
        stored.Single(usage => usage.Identifier == Id("count")).OccurredAt.ShouldBe(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        stored.Single(usage => usage.Identifier == Id("seat")).Properties!.ShouldContain("\"user_id\"");
        stored.Single(usage => usage.Identifier == Id("sum")).Properties.ShouldBeNull();
    }

    [Fact]
    [Trait("Requirement", "USG-2")]
    [Trait("Requirement", "USG-6")]
    public async Task ARepeatedEvent_IsADuplicate_WhateverItSays()
    {
        using var api = await server.CreateClientWithAsync(Permissions);
        var calls = await MeterAsync(api, "calls", MeterAggregation.Sum);
        var (customer, _) = await CustomerAsync(api);

        var first = await ReportAsync(
            api,
            new { id = Id("a"), customer, meter = calls, value = 3 },
            new { id = Id("b"), customer, meter = calls, value = 4 },
            new { id = Id("a"), customer, meter = calls, value = 5 });
        var retried = await ReportAsync(
            api,
            new { id = Id("a"), customer, meter = calls, value = 99 },
            new { id = Id("b"), customer, meter = $"missing_{_run}", value = 4 },
            new { id = Id("c"), customer, meter = calls, value = 6 });

        (first.Accepted, first.Rejected.Count).ShouldBe((2, 0));
        first.Duplicates.ShouldBe([Id("a")], "an ID is stored once, also within a batch");
        (retried.Accepted, retried.Rejected.Count).ShouldBe((1, 0));
        retried.Duplicates.ShouldBe([Id("a"), Id("b")]);
        var stored = await server.QueryDatabaseAsync(context => context.UsageEvents.Where(usage => usage.CustomerId == customer).OrderBy(usage => usage.Identifier).ToListAsync());
        stored.Select(usage => (usage.Identifier, usage.Value)).ShouldBe([(Id("a"), 3m), (Id("b"), 4m), (Id("c"), 6m)]);
    }

    [Fact]
    [Trait("Requirement", "USG-5")]
    public async Task AnEventOfAnInvoicedPeriod_IsRejected_EvenWhenRetriedLater()
    {
        using var api = await server.CreateClientWithAsync(Permissions);
        var calls = $"calls_{_run}";
        using (var provisioned = await api.PostJsonAsync("/api/v1/provisioning", new ProvisioningDocument
        {
            Meters = [new ProvisionedMeter { Key = calls, Name = "API calls", Aggregation = MeterAggregation.Sum }],
            Products =
            [
                new ProvisionedProduct
                {
                    Key = $"api-{_run}",
                    Name = "API",
                    Prices =
                    [
                        new ProvisionedPrice
                        {
                            Key = $"api-calls-{_run}", Currency = "TRY", Type = PriceType.Recurring, Interval = PriceInterval.Month, IntervalCount = 1, UsageType = UsageType.Metered, Meter = calls, UnitAmount = 5,
                        },
                    ],
                },
            ],
        }))
        {
            provisioned.StatusCode.ShouldBe(HttpStatusCode.OK, await provisioned.Content.ReadAsStringAsync(CancellationToken));
        }

        var (customer, _) = await CustomerAsync(api);
        var (other, _) = await CustomerAsync(api);
        using var subscribed = await api.PostJsonAsync("/api/v1/subscriptions", new CreateSubscriptionRequest
        {
            Customer = customer,
            Items = [new SubscriptionItemRequest { Price = $"api-calls-{_run}" }],
            CollectionMethod = CollectionMethod.SendInvoice,
            DaysUntilDue = 30,
        });
        subscribed.StatusCode.ShouldBe(HttpStatusCode.Created, await subscribed.Content.ReadAsStringAsync(CancellationToken));
        var subscription = (await subscribed.ReadAsync<SubscriptionResponse>()).Id;
        var billedUntil = DateTimeOffset.UtcNow.AddHours(-1);
        var before = await ReportAsync(api, new { id = Id("early"), customer, meter = calls, timestamp = billedUntil.AddMinutes(-30) });
        await server.ExecuteSqlAsync($"UPDATE tahsil.subscription_items SET usage_billed_until = {billedUntil} WHERE subscription_id = {subscription}");

        var after = await ReportAsync(
            api,
            new { id = Id("early"), customer, meter = calls, timestamp = billedUntil.AddMinutes(-30) },
            new { id = Id("late"), customer, meter = calls, timestamp = billedUntil.AddMinutes(-1) },
            new { id = Id("open"), customer, meter = calls, timestamp = billedUntil },
            new { id = Id("other"), customer = other, meter = calls, timestamp = billedUntil.AddMinutes(-1) });

        before.Accepted.ShouldBe(1);
        after.Accepted.ShouldBe(2, "an event at the very end of the invoiced period belongs to the next one, and other customers are not concerned");
        after.Duplicates.ShouldBe([Id("early")], "the retry of a stored event is a duplicate, not a refusal");
        after.Rejected.Select(rejected => (rejected.Id, rejected.Code)).ShouldBe([(Id("late"), "usage_event.period_closed")]);
    }

    [Fact]
    [Trait("Requirement", "USG-1")]
    [Trait("Requirement", "USG-3")]
    public async Task ABatch_FailsAsAWhole_WhenItIsEmptyOrTooLarge()
    {
        await using var host = server.WithSettings(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Tahsil"] = server.NewDatabase("usage_batches"),
            ["Tahsil:Usage:MaxBatchSize"] = "2",
        });
        using var api = await host.CreateClientWithAsync(Permissions);
        using var reader = await host.CreateClientWithAsync(TahsilPermissions.CatalogWrite);
        var calls = await MeterAsync(api, "calls", MeterAggregation.Sum);
        var (customer, _) = await CustomerAsync(api);
        var one = new { id = Id("x"), customer, meter = calls };

        using var empty = await api.PostJsonAsync("/api/v1/usage-events", new { events = Array.Empty<object>() });
        using var tooLarge = await api.PostJsonAsync("/api/v1/usage-events", new { events = new[] { one, one, one } });
        using var malformed = await api.PostJsonAsync("/api/v1/usage-events", new { events = new[] { new { customer, meter = calls } } });
        using var forbidden = await reader.PostJsonAsync("/api/v1/usage-events", new { events = new[] { one } });
        using var full = await api.PostJsonAsync("/api/v1/usage-events", new { events = new[] { one, one } });

        (empty.StatusCode, await empty.ProblemCodeAsync()).ShouldBe((HttpStatusCode.BadRequest, "usage.batch_invalid"));
        (tooLarge.StatusCode, await tooLarge.ProblemCodeAsync()).ShouldBe((HttpStatusCode.BadRequest, "usage.batch_invalid"));
        (malformed.StatusCode, await malformed.ProblemCodeAsync()).ShouldBe((HttpStatusCode.BadRequest, "request.invalid"));
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        full.StatusCode.ShouldBe(HttpStatusCode.OK, await full.Content.ReadAsStringAsync(CancellationToken));
        var answer = await full.ReadAsync<ReportUsageResponse>();
        answer.Accepted.ShouldBe(1);
        answer.Duplicates.ShouldBe([Id("x")]);
    }

    [Fact]
    [Trait("Requirement", "API-14")]
    public async Task UsageReporting_HasItsOwnRateLimit_ApartFromTheKeysOtherRequests()
    {
        await using var host = server.WithSettings(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Tahsil"] = server.NewDatabase("usage_limits"),
            ["Tahsil:RateLimits:UsageBatchesPerSecond"] = "1",
        });
        using var api = await host.CreateClientWithAsync(Permissions);
        var calls = await MeterAsync(api, "calls", MeterAggregation.Sum);
        var (customer, _) = await CustomerAsync(api);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(index =>
            api.PostJsonAsync("/api/v1/usage-events", new { events = new[] { new { id = Id($"r{index}"), customer, meter = calls } } })));
        using var other = await api.GetPathAsync("/api/v1/api-keys");

        var limited = responses.Where(response => response.StatusCode == HttpStatusCode.TooManyRequests).ToList();
        limited.Count.ShouldBeGreaterThanOrEqualTo(2);
        responses.ShouldContain(response => response.StatusCode == HttpStatusCode.OK);
        (await limited[0].ProblemCodeAsync()).ShouldBe("rate_limited");
        limited[0].Headers.Contains("Retry-After").ShouldBeTrue();
        other.StatusCode.ShouldBe(HttpStatusCode.OK);

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    private static async Task<ReportUsageResponse> ReportAsync(HttpClient api, params object[] events)
    {
        using var response = await api.PostJsonAsync("/api/v1/usage-events", new { events });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<ReportUsageResponse>();
    }

    private string Id(string name) => $"evt:{_run}.{name}";

    private async Task<string> MeterAsync(HttpClient api, string name, MeterAggregation aggregation, string? uniqueProperty = null)
    {
        var key = $"{name}_{_run}";
        using var created = await api.PostJsonAsync("/api/v1/meters", new CreateMeterRequest { Key = key, Name = name, Aggregation = aggregation, UniqueProperty = uniqueProperty });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(CancellationToken));
        return key;
    }

    private static async Task<(Guid Id, string ExternalId)> CustomerAsync(HttpClient api)
    {
        var externalId = $"user-{Guid.CreateVersion7():N}";
        using var created = await api.PostJsonAsync("/api/v1/customers", new CreateCustomerRequest { Name = "Ada Lovelace", Email = $"{externalId}@example.com", ExternalId = externalId });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(CancellationToken));
        return ((await created.ReadAsync<CustomerResponse>()).Id, externalId);
    }
}
