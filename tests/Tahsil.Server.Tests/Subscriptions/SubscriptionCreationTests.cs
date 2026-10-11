using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using static Tahsil.Server.Tests.Payments.PaymentScenarios;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;

namespace Tahsil.Server.Tests.Subscriptions;

/// <summary>Creating subscriptions through the API (SUB §1), with the simulator: each way a first invoice is collected.</summary>
public sealed class SubscriptionCreationTests(TahsilServerFixture server)
{
    private readonly FakeTimeProvider _time = new(FakeBillingClock.Start);

    [Fact]
    [Trait("Requirement", "SUB-5")]
    [Trait("Requirement", "SUB-6")]
    [Trait("Requirement", "INV-1")]
    public async Task WithASavedCard_TheFirstInvoiceIsPaidDuringTheRequest_AndTheSubscriptionIsActive()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CustomerAsync(client);
        await SaveCardAsync(client, customer, "4242424242424242");

        var subscription = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new SubscriptionItemRequest { Price = "team-monthly-try", Quantity = 5 }] });

        (subscription.Status, subscription.CurrentPeriodStart, subscription.CurrentPeriodEnd).ShouldBe((SubscriptionStatus.Active, _time.GetUtcNow(), _time.GetUtcNow().AddMonths(1)));
        subscription.Items.ShouldHaveSingleItem().Price.Product.Key.ShouldBe("team");
        var invoice = await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{subscription.LatestInvoice!.Id}");
        (invoice.Status, invoice.BillingReason, invoice.SubscriptionId, invoice.Total, invoice.AmountPaid).ShouldBe((InvoiceStatus.Paid, BillingReason.SubscriptionCreate, subscription.Id, 150_000L, 150_000L));
        (invoice.PeriodStart, invoice.PeriodEnd).ShouldBe((subscription.CurrentPeriodStart, subscription.CurrentPeriodEnd));
        var line = invoice.Lines.ShouldHaveSingleItem();
        (line.Type, line.Description, line.Quantity, line.TaxAmount).ShouldBe((InvoiceLineType.Subscription, "Team (Aylık) × 5", 5m, 25_000L));
        (await EventsAsync(host)).ShouldBe(["subscription.created incomplete", "subscription.updated active→incomplete"], ignoreOrder: true);
    }

    [Fact]
    [Trait("Requirement", "SUB-6")]
    [Trait("Requirement", "SUB-8")]
    public async Task ADeclinedFirstPayment_LeavesItIncomplete_UntilTheCustomerPaysOnTheInvoicePage()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CustomerAsync(client);
        await SaveCardAsync(client, customer, "4242424242424242", behavior: "insufficient_funds");

        var subscription = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new SubscriptionItemRequest { Price = "team-monthly-try" }] });

        (subscription.Status, subscription.IncompleteExpiresAt).ShouldBe((SubscriptionStatus.Incomplete, _time.GetUtcNow().AddHours(23)));
        (subscription.LatestInvoice!.Status, subscription.LatestInvoice.AmountDue).ShouldBe((InvoiceStatus.Open, 30_000L));
        subscription.LatestInvoice.HostedUrl.ShouldNotBeNull();

        await PayOnPageAsync(host, subscription.LatestInvoice.Id, "4242424242424242");

        var paid = await GetAsync<SubscriptionResponse>(client, $"/api/v1/subscriptions/{subscription.Id}");
        (paid.Status, paid.IncompleteExpiresAt, paid.LatestInvoice!.Status).ShouldBe((SubscriptionStatus.Active, null, InvoiceStatus.Paid));
    }

    [Fact]
    [Trait("Requirement", "SUB-6")]
    [Trait("Requirement", "SUB-8")]
    public async Task AFirstPaymentWithoutAnAnswer_IsSettledByReconciliation()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CustomerAsync(client);
        await SaveCardAsync(client, customer, "4242424242424242", behavior: "timeout_after_charge");

        var subscription = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new SubscriptionItemRequest { Price = "team-monthly-try" }] });
        subscription.Status.ShouldBe(SubscriptionStatus.Incomplete);

        _time.Advance(TimeSpan.FromMinutes(2));
        await host.RunWorkersUntilIdleAsync();

        (await GetAsync<SubscriptionResponse>(client, $"/api/v1/subscriptions/{subscription.Id}")).Status.ShouldBe(SubscriptionStatus.Active);
    }

    [Fact]
    [Trait("Requirement", "SUB-7")]
    public async Task SendingInvoices_ItIsActiveAtOnce_WithTheFirstInvoiceDue()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CustomerAsync(client);

        var subscription = await CreateAsync(client, new CreateSubscriptionRequest
        {
            Customer = customer,
            Items = [new SubscriptionItemRequest { Price = "team-monthly-try", Quantity = 2 }],
            CollectionMethod = CollectionMethod.SendInvoice,
            DaysUntilDue = 14,
        });

        subscription.Status.ShouldBe(SubscriptionStatus.Active);
        var invoice = await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{subscription.LatestInvoice!.Id}");
        (invoice.Status, invoice.CollectionMethod, invoice.AmountDue, invoice.NextPaymentAttemptAt).ShouldBe((InvoiceStatus.Open, CollectionMethod.SendInvoice, 60_000L, null));
        invoice.DueDate.ShouldNotBeNull().ShouldBeGreaterThan(_time.GetUtcNow().AddDays(14));
    }

    [Fact]
    [Trait("Requirement", "SUB-3")]
    [Trait("Requirement", "SUB-5")]
    public async Task WithATrial_NoCardIsNeeded_AndNothingIsInvoiced()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CustomerAsync(client);

        var subscription = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new SubscriptionItemRequest { Price = "pro-monthly-try" }] });

        var trialEnd = _time.GetUtcNow().AddDays(14);
        (subscription.Status, subscription.TrialEnd, subscription.CurrentPeriodEnd, subscription.BillingCycleAnchor).ShouldBe((SubscriptionStatus.Trialing, trialEnd, trialEnd, trialEnd));
        subscription.LatestInvoice.ShouldBeNull();
        (await host.QueryAsync<long>($"SELECT count(*) AS \"Value\" FROM tahsil.invoices")).ShouldBe([0L]);
    }

    [Fact]
    [Trait("Requirement", "SUB-4")]
    public async Task ALaterAnchor_BillsTheTimeUntilItAsAShorterFirstPeriod()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CustomerAsync(client);
        var now = _time.GetUtcNow();
        var anchor = now.AddDays(10);

        var subscription = await CreateAsync(client, new CreateSubscriptionRequest
        {
            Customer = customer,
            Items = [new SubscriptionItemRequest { Price = "team-monthly-try" }],
            CollectionMethod = CollectionMethod.SendInvoice,
            DaysUntilDue = 7,
            BillingCycleAnchor = anchor,
        });

        var invoice = await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{subscription.LatestInvoice!.Id}");
        var full = (anchor - anchor.AddMonths(-1)).TotalSeconds;
        (invoice.PeriodStart, invoice.PeriodEnd, subscription.BillingCycleAnchor).ShouldBe((now, anchor, anchor));
        invoice.Total.ShouldBe((long)Math.Round(30_000m * (decimal)((anchor - now).TotalSeconds / full), MidpointRounding.AwayFromZero));
    }

    [Theory]
    [Trait("Requirement", "SUB-1")]
    [Trait("Requirement", "SUB-5")]
    [InlineData("no card", "subscription.payment_method_required", "defaultPaymentMethod")]
    [InlineData("another customer's card", "subscription.payment_method_unusable", "defaultPaymentMethod")]
    [InlineData("unknown price", "subscription.invalid_items", "items[0].price")]
    [InlineData("one-time price", "subscription.invalid_items", "items[0]")]
    [InlineData("mixed intervals", "subscription.invalid_items", "items[1]")]
    [InlineData("send invoice without days", "subscription.invalid", "daysUntilDue")]
    public async Task InvalidRequests_AreRefused_WithoutCreatingAnything(string request, string code, string field)
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CustomerAsync(client);
        var stranger = await CustomerAsync(client);
        var strangersCard = await SaveCardAsync(client, stranger, "4242424242424242");
        var body = request switch
        {
            "another customer's card" => new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try" }], DefaultPaymentMethod = strangersCard },
            "unknown price" => new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "nope" }] },
            "one-time price" => new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "setup-fee-try" }] },
            "mixed intervals" => new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try" }, new() { Price = "team-yearly-try" }] },
            "send invoice without days" => new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try" }], CollectionMethod = CollectionMethod.SendInvoice },
            _ => new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try" }] },
        };

        using var response = await client.PostJsonAsync("/api/v1/subscriptions", body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.ReadAsync<System.Text.Json.Nodes.JsonNode>();
        (problem["code"]!.GetValue<string>(), problem["errors"]!.AsObject().Single().Key).ShouldBe((code, field));
        (await host.QueryAsync<long>($"SELECT count(*) AS \"Value\" FROM tahsil.subscriptions")).ShouldBe([0L]);
    }

    [Fact]
    [Trait("Requirement", "API-1")]
    public async Task Subscriptions_AreListedByCustomerStatusPriceAndProduct()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CustomerAsync(client);
        var trialing = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "pro-monthly-try" }] });
        _time.Advance(TimeSpan.FromSeconds(1));
        var active = await CreateAsync(client, new CreateSubscriptionRequest
        {
            Customer = customer,
            Items = [new() { Price = "team-monthly-try" }],
            CollectionMethod = CollectionMethod.SendInvoice,
            DaysUntilDue = 7,
        });

        (await ListAsync(client, $"customer={customer}")).ShouldBe([active.Id, trialing.Id]);
        (await ListAsync(client, "status=trialing")).ShouldBe([trialing.Id]);
        (await ListAsync(client, "price=team-monthly-try")).ShouldBe([active.Id]);
        (await ListAsync(client, "product=pro")).ShouldBe([trialing.Id]);
        (await client.GetPathAsync($"/api/v1/subscriptions/{Guid.CreateVersion7()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private WebApplicationFactory<Program> Host() => IsolatedHosts.Create(server, server.NewDatabase("subscriptions"), _time);

    private static async Task<List<Guid>> ListAsync(HttpClient client, string query) =>
        [.. (await GetAsync<Page<SubscriptionResponse>>(client, $"/api/v1/subscriptions?{query}")).Items.Select(subscription => subscription.Id)];

    private static async Task<List<string>> EventsAsync(WebApplicationFactory<Program> host) =>
        await host.QueryAsync<string>($"""
            SELECT action || ' ' || (data->>'status') || coalesce('→' || (data->>'previousStatus'), '') AS "Value"
            FROM tahsil.audit_events WHERE action LIKE 'subscription.%'
            """);
}
