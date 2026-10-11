using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using Tahsil.Providers.Abstractions;
using Tahsil.Server.Tests.Connections;

namespace Tahsil.Server.Tests.Checkout;

/// <summary>Creating checkout sessions (CHK §1): every rule, the quote, the page's URL, and expiry.</summary>
public sealed class CheckoutSessionTests(TahsilServerFixture server)
{
    private static readonly string[] Permissions =
    [
        TahsilPermissions.CheckoutWrite, TahsilPermissions.SubscriptionsRead, TahsilPermissions.CatalogWrite, TahsilPermissions.CatalogRead,
        TahsilPermissions.CustomersWrite, TahsilPermissions.ConnectionsWrite,
    ];

    private readonly FakeTimeProvider _time = new(FakeBillingClock.Start);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "CHK-1")]
    [Trait("Requirement", "CHK-7")]
    [Trait("Requirement", "CHK-31")]
    public async Task APaymentSession_QuotesTheItemsWithTax_AndGivesThePagesUrl()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        await CatalogAsync(client);

        var session = await CreateAsync(client, Payment(("credits-1000", 2), ("setup-fee", 1)) with { ClientReferenceId = "order-881", Metadata = new Dictionary<string, string> { ["cart"] = "42" } });
        var read = await GetAsync<CheckoutSessionResponse>(client, $"/api/v1/checkout-sessions/{session.Id}");

        (session.Mode, session.Status, session.Currency, session.ClientReferenceId).ShouldBe((CheckoutMode.Payment, CheckoutStatus.Open, "TRY", "order-881"));
        session.Url!.ToString().ShouldStartWith("http://localhost/checkout/cs_");
        session.Url.ToString().Length.ShouldBe("http://localhost/checkout/cs_".Length + 43);
        read.Url.ShouldBe(session.Url, "the URL is given again while the session is open");
        session.ExpiresAt.ShouldBe(session.CreatedAt.AddHours(24));

        // MNY vectors: 2 × ₺299.00 with 20% KDV inside, and ₺100.00 with 20% added.
        session.Quote.Lines.Select(line => (line.Amount, line.TaxAmount, line.Total)).ShouldBe([(59_800L, 9_967L, 59_800L), (10_000L, 2_000L, 12_000L)]);
        (session.Quote.Subtotal, session.Quote.TaxTotal, session.Quote.TotalExcludingTax, session.Quote.Total, session.Quote.AmountDue)
            .ShouldBe((69_800L, 11_967L, 59_833L, 71_800L, 71_800L));
        session.Quote.Lines[0].Description.ShouldBe("1,000 credits × 2");
    }

    [Fact]
    [Trait("Requirement", "CHK-7")]
    public async Task ATaxExemptCustomer_IsQuotedWithoutTax()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        await CatalogAsync(client);
        var customer = await CustomerAsync(client, new CreateCustomerRequest { Email = "exempt@example.com", IsTaxExempt = true });

        var session = await CreateAsync(client, Payment(("setup-fee", 1)) with { Customer = customer });

        (session.CustomerId, session.Quote.TaxTotal, session.Quote.AmountDue).ShouldBe((customer, 0L, 10_000L));
    }

    [Fact]
    [Trait("Requirement", "CHK-1")]
    public async Task ASetupSession_SellsNothing_AndNeedsACurrency()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);

        var session = await CreateAsync(client, Setup() with { CustomerDetails = new CheckoutCustomerDetails { ExternalId = "user-42", Email = "ada@example.com", Locale = "en" } });

        (session.Mode, session.Currency, session.Quote.AmountDue).ShouldBe((CheckoutMode.Setup, "TRY", 0L));
        session.Quote.Lines.ShouldBeEmpty();
        session.CustomerDetails.ShouldNotBeNull().ExternalId.ShouldBe("user-42");
    }

    public static TheoryData<string, string, string> InvalidSessions => new()
    {
        { "no items", "checkout.invalid_items", "items" },
        { "recurring price", "checkout.invalid_items", "items[0].price" },
        { "archived price", "checkout.invalid_items", "items[0].price" },
        { "mixed currencies", "checkout.invalid_items", "items[1].price" },
        { "zero quantity", "checkout.invalid_items", "items[0].quantity" },
        { "unknown price", "checkout.invalid_items", "items[0].price" },
        { "setup with items", "checkout.invalid_items", "items" },
        { "setup without currency", "checkout.invalid", "currency" },
        { "unrouted currency", "checkout.currency_not_routed", "currency" },
        { "plain http", "checkout.invalid_url", "successUrl" },
        { "fragment", "checkout.invalid_url", "cancelUrl" },
        { "too short", "checkout.invalid", "expiresIn" },
        { "too long", "checkout.invalid", "expiresIn" },
        { "unknown locale", "checkout.invalid", "locale" },
        { "customer and details", "checkout.invalid", "customerDetails" },
        { "bad email", "customer.invalid", "customerDetails.email" },
        { "long reference", "checkout.invalid", "clientReferenceId" },
    };

    [Theory]
    [MemberData(nameof(InvalidSessions))]
    [Trait("Requirement", "CHK-1")]
    [Trait("Requirement", "CHK-2")]
    [Trait("Requirement", "CHK-3")]
    [Trait("Requirement", "CHK-5")]
    [Trait("Requirement", "CHK-6")]
    public async Task InvalidSessions_AreRefused(string reason, string code, string field)
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        await CatalogAsync(client);
        var customer = await CustomerAsync(client, new CreateCustomerRequest { Email = "ada@example.com" });
        var request = reason switch
        {
            "no items" => Payment(),
            "recurring price" => Payment(("pro-monthly", 1)),
            "archived price" => Payment(("retired", 1)),
            "mixed currencies" => Payment(("setup-fee", 1), ("setup-fee-usd", 1)),
            "zero quantity" => Payment(("setup-fee", 0)),
            "unknown price" => Payment(("nope", 1)),
            "setup with items" => Setup() with { Items = [new CheckoutItemRequest { Price = "setup-fee" }] },
            "setup without currency" => Setup() with { Currency = null },
            "unrouted currency" => Payment(("setup-fee-gbp", 1)),
            "plain http" => Payment(("setup-fee", 1)) with { SuccessUrl = "http://shop.example.com/done" },
            "fragment" => Payment(("setup-fee", 1)) with { CancelUrl = "https://shop.example.com/cart#top" },
            "too short" => Payment(("setup-fee", 1)) with { ExpiresIn = "PT10M" },
            "too long" => Payment(("setup-fee", 1)) with { ExpiresIn = "PT25H" },
            "unknown locale" => Payment(("setup-fee", 1)) with { Locale = "de" },
            "customer and details" => Payment(("setup-fee", 1)) with { Customer = customer, CustomerDetails = new CheckoutCustomerDetails { Email = "x@example.com" } },
            "bad email" => Payment(("setup-fee", 1)) with { CustomerDetails = new CheckoutCustomerDetails { Email = "not-an-email" } },
            "long reference" => Payment(("setup-fee", 1)) with { ClientReferenceId = new string('x', 201) },
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };

        using var response = await client.PostJsonAsync("/api/v1/checkout-sessions", request);
        var problem = await response.ReadAsync<System.Text.Json.Nodes.JsonNode>();

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, problem.ToJsonString());
        problem["code"]!.GetValue<string>().ShouldBe(code);
        problem["errors"]!.AsObject().Select(error => error.Key).ShouldContain(field);
    }

    [Fact]
    [Trait("Requirement", "CHK-2")]
    public async Task UnknownAndAnonymizedCustomers_CannotBuy()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync([.. Permissions, TahsilPermissions.InvoicesWrite]);
        await CatalogAsync(client);
        var anonymized = await CustomerAsync(client, new CreateCustomerRequest { Email = "gone@example.com" });
        using (var invoice = await client.PostJsonAsync("/api/v1/invoices", new CreateInvoiceRequest { Customer = anonymized, Currency = "TRY", Lines = [new InvoiceLineRequest { Description = "History", UnitAmount = 100 }] }))
        {
            invoice.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        (await client.DeleteAsync(new Uri($"/api/v1/customers/{anonymized}", UriKind.Relative), CancellationToken)).Dispose();

        using var unknown = await client.PostJsonAsync("/api/v1/checkout-sessions", Payment(("setup-fee", 1)) with { Customer = Guid.CreateVersion7() });
        using var gone = await client.PostJsonAsync("/api/v1/checkout-sessions", Payment(("setup-fee", 1)) with { Customer = anonymized });

        (unknown.StatusCode, await unknown.ProblemCodeAsync()).ShouldBe((HttpStatusCode.NotFound, "customer.not_found"));
        (gone.StatusCode, await gone.ProblemCodeAsync()).ShouldBe((HttpStatusCode.Conflict, "customer.anonymized"));
    }

    [Fact]
    [Trait("Requirement", "CHK-3")]
    public async Task ASetupSession_NeedsAConnectionThatSavesCards()
    {
        var provider = new FakeProvider("fakepay", "USD");
        provider = new FakeProvider("fakepay", "USD") { Descriptor = provider.Descriptor with { Capabilities = provider.Descriptor.Capabilities with { SavesCards = CardSaving.None } } };
        await using var host = Host(services => services.AddSingleton<IPaymentProvider>(provider));
        using var client = await host.CreateClientWithAsync(Permissions);
        using (var created = await client.PostJsonAsync("/api/v1/provider-connections", new CreateConnectionRequest
        {
            Key = "fake-main",
            Provider = "fakepay",
            Name = "Fake",
            Environment = ConnectionEnvironment.Sandbox,
            Credentials = new Dictionary<string, string> { ["apiKey"] = "sk_test_000000000000", ["secretKey"] = "secret" },
        }))
        {
            created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(CancellationToken));
        }

        (await client.PutAsJsonAsync(new Uri("/api/v1/provider-connections/routing", UriKind.Relative), new Dictionary<string, string> { ["TRY"] = "simulator", ["USD"] = "fake-main" }, ApiClients.Json, CancellationToken)).Dispose();

        using var response = await client.PostJsonAsync("/api/v1/checkout-sessions", Setup() with { Currency = "USD" });

        (await response.ProblemCodeAsync()).ShouldBe("checkout.not_supported_by_connection");
    }

    [Fact]
    [Trait("Requirement", "CHK-5")]
    public async Task LoopbackHttp_IsAcceptedInTestMode_AndTheSessionIdPlaceholderIsKept()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);

        var session = await CreateAsync(client, Setup() with { SuccessUrl = "http://localhost:3000/billing?session={CHECKOUT_SESSION_ID}" });

        session.SuccessUrl.ShouldBe("http://localhost:3000/billing?session={CHECKOUT_SESSION_ID}");
    }

    [Fact]
    [Trait("Requirement", "DOM-10")]
    [Trait("Requirement", "CHK-6")]
    public async Task Sessions_ExpireWhenAsked_OrWhenTheirTimeRunsOut()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var asked = await CreateAsync(client, Setup() with { ClientReferenceId = "asked" });
        var timed = await CreateAsync(client, Setup() with { ClientReferenceId = "timed", ExpiresIn = "PT30M" });

        using var expired = await client.PostPathAsync($"/api/v1/checkout-sessions/{asked.Id}/expire");
        using var again = await client.PostPathAsync($"/api/v1/checkout-sessions/{asked.Id}/expire");
        _time.Advance(TimeSpan.FromMinutes(29));
        await host.RunWorkersUntilIdleAsync();
        var stillOpen = await GetAsync<CheckoutSessionResponse>(client, $"/api/v1/checkout-sessions/{timed.Id}");
        _time.Advance(TimeSpan.FromMinutes(1));
        await host.RunWorkersUntilIdleAsync();
        var ran = await GetAsync<CheckoutSessionResponse>(client, $"/api/v1/checkout-sessions/{timed.Id}");
        var listed = await GetAsync<Page<CheckoutSessionResponse>>(client, "/api/v1/checkout-sessions?status=expired&clientReferenceId=asked");

        var session = await expired.ReadAsync<CheckoutSessionResponse>();
        (session.Status, session.Url).ShouldBe((CheckoutStatus.Expired, null));
        (again.StatusCode, await again.ProblemCodeAsync()).ShouldBe((HttpStatusCode.Conflict, "checkout.not_open"));
        stillOpen.Status.ShouldBe(CheckoutStatus.Open);
        (ran.Status, ran.ExpiredAt).ShouldBe((CheckoutStatus.Expired, FakeBillingClock.Start.AddMinutes(30)));
        listed.Items.ShouldHaveSingleItem().Id.ShouldBe(asked.Id);
        (await host.QueryAsync<string>($"SELECT data->>'clientReferenceId' AS \"Value\" FROM tahsil.audit_events WHERE action = 'checkout.expired'"))
            .ShouldBe(["asked", "timed"], ignoreOrder: true);
    }

    [Fact]
    [Trait("Requirement", "API-13")]
    public async Task ARetriedCreation_GivesTheSameSessionAndUrl_ToAKeyThatCannotReadSessions()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(TahsilPermissions.CheckoutWrite);

        async Task<CheckoutSessionResponse> SendAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/checkout-sessions", UriKind.Relative)) { Content = JsonContent.Create(Setup(), options: ApiClients.Json) };
            request.Headers.Add("Idempotency-Key", "checkout-retry-1");
            using var response = await client.SendAsync(request, CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
            return await response.ReadAsync<CheckoutSessionResponse>();
        }

        var first = await SendAsync();
        var retried = await SendAsync();

        (retried.Id, retried.Url).ShouldBe((first.Id, first.Url));
        using var read = await client.GetPathAsync($"/api/v1/checkout-sessions/{first.Id}");
        read.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static CreateCheckoutSessionRequest Payment(params (string Price, int Quantity)[] items) => new()
    {
        Mode = CheckoutMode.Payment,
        Items = [.. items.Select(item => new CheckoutItemRequest { Price = item.Price, Quantity = item.Quantity })],
        SuccessUrl = "https://shop.example.com/done?session={CHECKOUT_SESSION_ID}",
        CancelUrl = "https://shop.example.com/cart",
    };

    private static CreateCheckoutSessionRequest Setup() => new()
    {
        Mode = CheckoutMode.Setup,
        Currency = "TRY",
        SuccessUrl = "https://shop.example.com/done",
    };

    /// <summary>Prices of the MNY vectors: KDV inside and added, a recurring and an archived price, and other currencies.</summary>
    private static async Task CatalogAsync(HttpClient client)
    {
        await PostAsync(client, "/api/v1/tax-rates", new CreateTaxRateRequest { Key = "kdv-20", Name = "KDV", Percentage = 20, Inclusive = true });
        await PostAsync(client, "/api/v1/tax-rates", new CreateTaxRateRequest { Key = "vat-20", Name = "VAT", Percentage = 20, Inclusive = false });
        await PostAsync(client, "/api/v1/products", new CreateProductRequest { Key = "credits", Name = "1,000 credits" });
        await PriceAsync(client, "credits-1000", "TRY", 29_900, "kdv-20");
        await PriceAsync(client, "setup-fee", "TRY", 10_000, "vat-20");
        await PriceAsync(client, "setup-fee-usd", "USD", 1_000, null);
        await PriceAsync(client, "setup-fee-gbp", "GBP", 1_000, null);
        await PostAsync(client, "/api/v1/prices", new CreatePriceRequest { Product = "credits", Key = "pro-monthly", Currency = "TRY", Type = PriceType.Recurring, Interval = PriceInterval.Month, UnitAmount = 29_900 });
        await PriceAsync(client, "retired", "TRY", 5_000, null);
        using var archive = new HttpRequestMessage(HttpMethod.Patch, new Uri("/api/v1/prices/retired", UriKind.Relative)) { Content = JsonContent.Create(new { isArchived = true }) };
        (await client.SendAsync(archive, CancellationToken)).EnsureSuccessStatusCode().Dispose();
    }

    private static Task PriceAsync(HttpClient client, string key, string currency, long amount, string? taxRate) =>
        PostAsync(client, "/api/v1/prices", new CreatePriceRequest { Product = "credits", Key = key, Currency = currency, Type = PriceType.OneTime, UnitAmount = amount, TaxRate = taxRate });

    private static async Task PostAsync<T>(HttpClient client, string path, T body)
    {
        using var response = await client.PostJsonAsync(path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
    }

    private static async Task<Guid> CustomerAsync(HttpClient client, CreateCustomerRequest request)
    {
        using var response = await client.PostJsonAsync("/api/v1/customers", request);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await response.ReadAsync<CustomerResponse>()).Id;
    }

    private static async Task<CheckoutSessionResponse> CreateAsync(HttpClient client, CreateCheckoutSessionRequest request)
    {
        using var response = await client.PostJsonAsync("/api/v1/checkout-sessions", request);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<CheckoutSessionResponse>();
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string path)
    {
        using var response = await client.GetPathAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<T>();
    }

    private WebApplicationFactory<Program> Host(Action<IServiceCollection>? services = null) =>
        IsolatedHosts.Create(server, server.NewDatabase("checkout"), _time, services);
}
