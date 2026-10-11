using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tahsil.Contracts;
using Tahsil.Infrastructure.Persistence;

namespace Tahsil.Server.Tests.Subscriptions;

/// <summary>The steps subscription tests share: a catalog of recurring prices, saved cards, and subscriptions.</summary>
internal static class SubscriptionScenarios
{
    public static readonly string[] Permissions =
    [
        TahsilPermissions.SubscriptionsWrite, TahsilPermissions.SubscriptionsRead, TahsilPermissions.ProvisioningWrite, TahsilPermissions.CatalogWrite,
        TahsilPermissions.CustomersWrite, TahsilPermissions.CustomersRead, TahsilPermissions.InvoicesRead, TahsilPermissions.InvoicesWrite,
        TahsilPermissions.PaymentsRead, TahsilPermissions.RefundsWrite, TahsilPermissions.TestWrite,
    ];

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    /// <summary>
    /// A client with every permission the tests need, on a host with the catalog: Team monthly (30,000), yearly and Plus
    /// (60,000), a setup fee, Pro with a 14-day trial, and Growth's Starter (29,900) and Growth (49,900).
    /// </summary>
    public static async Task<HttpClient> ClientAsync(WebApplicationFactory<Program> host)
    {
        var client = await host.CreateClientWithAsync(Permissions);
        using var provisioned = await client.PostJsonAsync("/api/v1/provisioning", new ProvisioningDocument
        {
            TaxRates = [new ProvisionedTaxRate { Key = "kdv-20", Name = "KDV", Percentage = 20, Inclusive = true }],
            Products =
            [
                new ProvisionedProduct
                {
                    Key = "team",
                    Name = "Team",
                    Prices =
                    [
                        Recurring("team-monthly-try", "Aylık", PriceInterval.Month, 30_000),
                        Recurring("team-yearly-try", "Yıllık", PriceInterval.Year, 300_000),
                        Recurring("team-plus-monthly-try", "Plus Aylık", PriceInterval.Month, 60_000),
                        new ProvisionedPrice { Key = "setup-fee-try", Currency = "TRY", Type = PriceType.OneTime, UnitAmount = 10_000, TaxRate = "kdv-20" },
                    ],
                },
                new ProvisionedProduct { Key = "pro", Name = "Pro", Prices = [Recurring("pro-monthly-try", "Aylık", PriceInterval.Month, 50_000) with { TrialDays = 14 }] },
                new ProvisionedProduct
                {
                    Key = "growth",
                    Name = "Growth",
                    Prices = [Recurring("starter-monthly-try", "Starter", PriceInterval.Month, 29_900), Recurring("growth-monthly-try", "Growth", PriceInterval.Month, 49_900)],
                },
            ],
        });
        provisioned.StatusCode.ShouldBe(HttpStatusCode.OK, await provisioned.Content.ReadAsStringAsync(CancellationToken));
        return client;
    }

    /// <summary>A company customer, to whom price increases apply without notice when asked (SUB-34).</summary>
    public static async Task<Guid> CompanyAsync(HttpClient client)
    {
        using var response = await client.PostJsonAsync("/api/v1/customers", new CreateCustomerRequest { Name = "Acme A.Ş.", Email = "billing@acme.example", Type = CustomerType.Company });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await response.ReadAsync<CustomerResponse>()).Id;
    }

    public static async Task<Guid> SaveCardAsync(HttpClient client, Guid customer, string card, string? behavior = null)
    {
        using var response = await client.PostJsonAsync("/api/v1/test/simulator/payment-methods", new CreateSimulatorPaymentMethodRequest { Customer = customer, Card = card });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        var id = (await response.ReadAsync<PaymentMethodResponse>()).Id;
        if (behavior is not null)
        {
            await SetBehaviorAsync(client, id, behavior);
        }

        return id;
    }

    /// <summary>Scripts how a saved simulator card answers from now on, such as <c>card_declined</c> or <c>succeed</c>.</summary>
    public static async Task SetBehaviorAsync(HttpClient client, Guid card, string behavior)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri($"/api/v1/test/simulator/payment-methods/{card}", UriKind.Relative))
        {
            Content = JsonContent.Create(new { behavior }, options: ApiClients.Json),
        };
        (await client.SendAsync(request, CancellationToken)).EnsureSuccessStatusCode().Dispose();
    }

    public static async Task<SubscriptionResponse> CreateAsync(HttpClient client, CreateSubscriptionRequest request)
    {
        using var response = await client.PostJsonAsync("/api/v1/subscriptions", request);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<SubscriptionResponse>();
    }

    /// <summary>A monthly Team subscription whose invoices are sent: active at once, without a card.</summary>
    public static Task<SubscriptionResponse> SendingInvoicesAsync(HttpClient client, Guid customer, int quantity = 1) =>
        CreateAsync(client, new CreateSubscriptionRequest
        {
            Customer = customer,
            Items = [new SubscriptionItemRequest { Price = "team-monthly-try", Quantity = quantity }],
            CollectionMethod = CollectionMethod.SendInvoice,
            DaysUntilDue = 14,
        });

    public static async Task<T> GetAsync<T>(HttpClient client, string path)
    {
        using var response = await client.GetPathAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<T>();
    }

    public static Task<SubscriptionResponse> GetSubscriptionAsync(HttpClient client, Guid id) => GetAsync<SubscriptionResponse>(client, $"/api/v1/subscriptions/{id}");

    /// <summary>Changes a subscription in storage with the domain, for states the API cannot reach yet.</summary>
    public static async Task ChangeAsync(WebApplicationFactory<Program> host, Guid id, Action<Domain.Subscriptions.Subscription> change)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TahsilDbContext>();
        change(await context.Subscriptions.Include(subscription => subscription.Items).SingleAsync(subscription => subscription.Id == id, CancellationToken));
        await context.SaveChangesAsync(CancellationToken);
    }

    private static ProvisionedPrice Recurring(string key, string name, PriceInterval interval, long amount) => new()
    {
        Key = key,
        Name = name,
        Currency = "TRY",
        Type = PriceType.Recurring,
        Interval = interval,
        IntervalCount = 1,
        UsageType = UsageType.Licensed,
        UnitAmount = amount,
        TaxRate = "kdv-20",
    };
}
