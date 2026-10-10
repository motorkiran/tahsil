using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Application.Common;
using Tahsil.Contracts;
using Tahsil.Providers.Abstractions;
using Tahsil.Server.Tests.Connections;

namespace Tahsil.Server.Tests.Invoices;

public sealed partial class InvoiceApiTests(TahsilServerFixture server)
{
    private static readonly string[] Permissions =
    [
        TahsilPermissions.InvoicesRead, TahsilPermissions.InvoicesWrite, TahsilPermissions.CustomersRead, TahsilPermissions.CustomersWrite,
        TahsilPermissions.CatalogWrite, TahsilPermissions.ConnectionsRead, TahsilPermissions.ConnectionsWrite,
    ];

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "INV-21")]
    [Trait("Requirement", "INV-8")]
    [Trait("Requirement", "DOM-14")]
    public async Task AManualInvoice_IsDrafted_Finalized_AndThenFrozen()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        await CatalogAsync(client);
        var customer = await CustomerAsync(client, taxId: "10000000146");

        var draft = await CreateAsync(client, new CreateInvoiceRequest
        {
            Customer = customer,
            Currency = "TRY",
            Lines = [new InvoiceLineRequest { Price = "credits-1000", Quantity = 2 }, new InvoiceLineRequest { Description = "Setup", UnitAmount = 5_000 }],
            Memo = "Thank you",
        });
        var added = await ReadOkAsync<InvoiceResponse>(await client.PostJsonAsync($"/api/v1/invoices/{draft.Id}/lines", new AddInvoiceLinesRequest
        {
            Lines = [new InvoiceLineRequest { Description = "Discount", UnitAmount = -1_000 }],
        }));
        var removed = await ReadOkAsync<InvoiceResponse>(await client.DeleteAsync(new Uri($"/api/v1/invoices/{draft.Id}/lines/{added.Lines[2].Id}", UriKind.Relative), CancellationToken));
        var finalized = await ReadOkAsync<InvoiceResponse>(await client.PostPathAsync($"/api/v1/invoices/{draft.Id}/finalize"));
        using var lateLine = await client.PostJsonAsync($"/api/v1/invoices/{draft.Id}/lines", new AddInvoiceLinesRequest { Lines = [new InvoiceLineRequest { Description = "Late", UnitAmount = 100 }] });
        using var finalizedAgain = await client.PostPathAsync($"/api/v1/invoices/{draft.Id}/finalize");

        draft.Status.ShouldBe(InvoiceStatus.Draft);
        draft.Number.ShouldBeNull();
        draft.Lines.Select(line => (line.Type, line.Description, line.Amount, line.TaxAmount)).ShouldBe(
            [(InvoiceLineType.OneTime, "1.000 kredi × 2", 59_800L, 9_967L), (InvoiceLineType.Item, "Setup", 5_000L, 0L)]);
        draft.Total.ShouldBe(64_800);
        added.Total.ShouldBe(63_800);
        removed.Total.ShouldBe(64_800);
        finalized.Status.ShouldBe(InvoiceStatus.Open);
        NumberPattern().IsMatch(finalized.Number!).ShouldBeTrue(finalized.Number);
        finalized.Number.ShouldEndWith("-000001");
        (finalized.AmountDue, finalized.AmountRemaining, finalized.NextPaymentAttemptAt).ShouldBe((64_800, 64_800, finalized.FinalizedAt));
        finalized.CustomerSnapshot.ShouldNotBeNull().TaxId.ShouldBe("10000000146");
        finalized.CustomerSnapshot.Name.ShouldBe("Ayşe Yılmaz");
        (await lateLine.ProblemCodeAsync()).ShouldBe("invoice.not_draft");
        (await finalizedAgain.ProblemCodeAsync()).ShouldBe("invoice.invalid_state");
        var stored = await host.QueryAsync<byte[]>($"SELECT customer_tax_id AS \"Value\" FROM tahsil.invoices WHERE id = {draft.Id}");
        Encoding.UTF8.GetString(stored.Single()).ShouldNotContain("10000000146");
        (await host.QueryAsync<string>($"SELECT action AS \"Value\" FROM tahsil.audit_events WHERE subject_id = {draft.Id.ToString()} ORDER BY id"))
            .ShouldBe(["invoice.created", "invoice.updated", "invoice.updated", "invoice.finalized"]);
    }

    [Theory]
    [Trait("Requirement", "INV-21")]
    [InlineData("pro-monthly", "Invoices and invoice items take one-time prices")]
    [InlineData("credits-usd", "The price is in USD, not TRY.")]
    public async Task OnlyOneTimePricesInTheInvoicesCurrency_MakeLines(string price, string message)
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        await CatalogAsync(client);
        var customer = await CustomerAsync(client);

        using var response = await client.PostJsonAsync("/api/v1/invoices", new CreateInvoiceRequest
        {
            Customer = customer,
            Currency = "TRY",
            Lines = [new InvoiceLineRequest { Price = price }],
        });
        var problem = await response.Content.ReadAsStringAsync(CancellationToken);

        (await response.ProblemCodeAsync()).ShouldBe("invoice.invalid_line");
        problem.ShouldContain("lines[0].price");
        problem.ShouldContain(message);
    }

    [Theory]
    [Trait("Requirement", "MNY-22")]
    [Trait("Requirement", "MNY-23")]
    [Trait("Requirement", "CUS-17")]
    [InlineData(5_000, 29_900, 5_000, 24_900, 0)]
    [InlineData(50_000, 29_900, 29_900, 0, 20_100)]
    [InlineData(-1_200, 29_900, -1_200, 31_100, 0)]
    [InlineData(0, -15_000, -15_000, 0, 15_000)]
    [InlineData(0, 50, 50, 0, -50)]
    [InlineData(-50, 29_900, -50, 29_950, 0)]
    public async Task Finalization_AppliesTheBalance_AsTheTableSays(long before, long total, long applied, long due, long after)
    {
        await using var host = Host(provider: new FakeProvider("fakepay", "TRY"));
        using var client = await host.CreateClientWithAsync(Permissions);
        await RouteTryToAProviderWithAMinimumOf100Async(client);
        var customer = await CustomerAsync(client);
        if (before != 0)
        {
            await AdjustAsync(client, customer, before);
        }

        var draft = await CreateAsync(client, new CreateInvoiceRequest { Customer = customer, Currency = "TRY", Lines = [new InvoiceLineRequest { Description = "Line", UnitAmount = total }] });
        var invoice = await ReadOkAsync<InvoiceResponse>(await client.PostPathAsync($"/api/v1/invoices/{draft.Id}/finalize"));
        var balance = await ReadOkAsync<CustomerResponse>(await client.GetPathAsync($"/api/v1/customers/{customer}"));
        var history = await ReadOkAsync<Page<BalanceTransactionResponse>>(await client.GetPathAsync($"/api/v1/customers/{customer}/balance-transactions?currency=TRY"));

        (invoice.AppliedBalance, invoice.AmountDue).ShouldBe((applied, due));
        invoice.Status.ShouldBe(due == 0 ? InvoiceStatus.Paid : InvoiceStatus.Open);
        balance.Balances.ShouldBe([new CustomerBalanceResponse("TRY", after)]);
        var forInvoice = history.Items.Where(transaction => transaction.InvoiceId == invoice.Id).ToList();
        (-forInvoice.Sum(transaction => transaction.Amount)).ShouldBe(applied, "MNY-24");
        history.Items[0].BalanceAfter.ShouldBe(after);
    }

    [Fact]
    [Trait("Requirement", "MNY-23")]
    public async Task InvoicesSentForManualPayment_AreNotCarriedOver()
    {
        await using var host = Host(provider: new FakeProvider("fakepay", "TRY"));
        using var client = await host.CreateClientWithAsync(Permissions);
        await RouteTryToAProviderWithAMinimumOf100Async(client);
        var customer = await CustomerAsync(client);

        var draft = await CreateAsync(client, new CreateInvoiceRequest
        {
            Customer = customer,
            Currency = "TRY",
            CollectionMethod = CollectionMethod.SendInvoice,
            DaysUntilDue = 0,
            Lines = [new InvoiceLineRequest { Description = "Line", UnitAmount = 50 }],
        });
        var invoice = await ReadOkAsync<InvoiceResponse>(await client.PostPathAsync($"/api/v1/invoices/{draft.Id}/finalize"));

        (invoice.Status, invoice.AmountDue, invoice.NextPaymentAttemptAt).ShouldBe((InvoiceStatus.Open, 50, null));
        invoice.DueDate.ShouldNotBeNull().ShouldBeGreaterThan(invoice.FinalizedAt!.Value);
        (invoice.DueDate.Value - invoice.FinalizedAt.Value).ShouldBeLessThanOrEqualTo(TimeSpan.FromDays(1));
    }

    [Fact]
    [Trait("Requirement", "INV-9")]
    public async Task ConcurrentFinalizations_OnSeveralInstances_NeverDuplicateOrSkipNumbers()
    {
        var database = server.NewDatabase("invoices");
        await using var first = Host(database);
        await using var second = Host(database);
        using var one = await first.CreateClientWithAsync(Permissions);
        using var two = await second.CreateClientWithAsync(Permissions);
        var customer = await CustomerAsync(one);
        var drafts = new List<Guid>();
        for (var index = 0; index < 24; index++)
        {
            drafts.Add((await CreateAsync(one, new CreateInvoiceRequest { Customer = customer, Currency = "TRY", Lines = [new InvoiceLineRequest { Description = "Line", UnitAmount = 1_000 + index }] })).Id);
        }

        var finalized = await Task.WhenAll(drafts.Select((id, index) => ReadOkAsync<InvoiceResponse>((index % 2 == 0 ? one : two).PostPathAsync($"/api/v1/invoices/{id}/finalize"))));

        finalized.Select(invoice => int.Parse(invoice.Number![^6..], System.Globalization.CultureInfo.InvariantCulture)).Order().ShouldBe(Enumerable.Range(1, 24));
        finalized.Select(invoice => invoice.Number![..^6]).Distinct().ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Requirement", "INV-9")]
    public async Task Numbers_RestartEachYear_InTheBillingTimeZone()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 12, 31, 20, 0, 0, TimeSpan.Zero));
        await using var host = Host(clock: time, settings: new() { ["Tahsil:Billing:TimeZone"] = "Europe/Istanbul", ["Tahsil:Invoices:NumberPrefix"] = "TS" });
        using var client = await host.CreateClientWithAsync(Permissions);
        var customer = await CustomerAsync(client);

        var lastOf2026 = await FinalizeNewAsync(client, customer);
        time.Advance(TimeSpan.FromHours(1.5));
        var firstOf2027 = await FinalizeNewAsync(client, customer);
        var secondOf2027 = await FinalizeNewAsync(client, customer);

        // 21:30 UTC on 31 December is already 00:30 on 1 January in Istanbul.
        (lastOf2026.Number, firstOf2027.Number, secondOf2027.Number).ShouldBe(("TS-2026-000001", "TS-2027-000001", "TS-2027-000002"));
    }

    [Fact]
    [Trait("Requirement", "INV-16")]
    [Trait("Requirement", "INV-17")]
    public async Task Voiding_GivesTheBalanceBack_AndUncollectibleInvoicesCanStillBeVoided()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var customer = await CustomerAsync(client);
        await AdjustAsync(client, customer, 5_000);
        var draft = await CreateAsync(client, new CreateInvoiceRequest { Customer = customer, Currency = "TRY", Lines = [new InvoiceLineRequest { Description = "Line", UnitAmount = 29_900 }] });
        await ReadOkAsync<InvoiceResponse>(await client.PostPathAsync($"/api/v1/invoices/{draft.Id}/finalize"));

        var uncollectible = await ReadOkAsync<InvoiceResponse>(await client.PostPathAsync($"/api/v1/invoices/{draft.Id}/mark-uncollectible"));
        var voided = await ReadOkAsync<InvoiceResponse>(await client.PostPathAsync($"/api/v1/invoices/{draft.Id}/void"));
        using var again = await client.PostPathAsync($"/api/v1/invoices/{draft.Id}/void");
        var history = await ReadOkAsync<Page<BalanceTransactionResponse>>(await client.GetPathAsync($"/api/v1/customers/{customer}/balance-transactions"));

        (uncollectible.Status, uncollectible.NextPaymentAttemptAt).ShouldBe((InvoiceStatus.Uncollectible, null));
        voided.Status.ShouldBe(InvoiceStatus.Void);
        voided.Number.ShouldNotBeNull();
        (await again.ProblemCodeAsync()).ShouldBe("invoice.invalid_state");
        history.Items.Select(transaction => (transaction.Type, transaction.Amount, transaction.BalanceAfter)).ShouldBe(
        [
            (BalanceTransactionType.InvoiceVoided, 5_000L, 5_000L),
            (BalanceTransactionType.AppliedToInvoice, -5_000L, 0L),
            (BalanceTransactionType.Adjustment, 5_000L, 5_000L),
        ]);
    }

    [Fact]
    public async Task PendingItems_JoinTheNextManualInvoice_AndComeBackWhenItsDraftIsDeleted()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        await CatalogAsync(client);
        var customer = await CustomerAsync(client);
        var priced = await ReadCreatedAsync<InvoiceItemResponse>(await client.PostJsonAsync("/api/v1/invoice-items", new CreateInvoiceItemRequest { Customer = customer, Currency = "TRY", Price = "credits-1000" }));
        var credit = await ReadCreatedAsync<InvoiceItemResponse>(await client.PostJsonAsync("/api/v1/invoice-items", new CreateInvoiceItemRequest
        {
            Customer = customer,
            Currency = "TRY",
            Description = "Apology credit",
            UnitAmount = -2_000,
            TaxRate = "kdv-20",
        }));
        await ReadCreatedAsync<InvoiceItemResponse>(await client.PostJsonAsync("/api/v1/invoice-items", new CreateInvoiceItemRequest
        {
            Customer = customer,
            Currency = "USD",
            Description = "Other currency",
            UnitAmount = 1_000,
        }));

        var draft = await CreateAsync(client, new CreateInvoiceRequest { Customer = customer, Currency = "TRY", IncludePendingItems = true });
        using var deleteTaken = await client.DeleteAsync(new Uri($"/api/v1/invoice-items/{priced.Id}", UriKind.Relative), CancellationToken);
        var pendingWhileDrafted = await ReadOkAsync<Page<InvoiceItemResponse>>(await client.GetPathAsync($"/api/v1/invoice-items?customer={customer}&pending=true"));
        (await client.DeleteAsync(new Uri($"/api/v1/invoices/{draft.Id}", UriKind.Relative), CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var pendingAfterDelete = await ReadOkAsync<Page<InvoiceItemResponse>>(await client.GetPathAsync($"/api/v1/invoice-items?customer={customer}&pending=true"));

        draft.Lines.Select(line => (line.Type, line.Description, line.Amount, line.PendingItemId)).ShouldBe(
        [
            (InvoiceLineType.Item, "1.000 kredi × 1", 29_900L, (Guid?)priced.Id),
            (InvoiceLineType.Item, "Apology credit", -2_000L, credit.Id),
        ]);
        draft.Total.ShouldBe(27_900);
        (await deleteTaken.ProblemCodeAsync()).ShouldBe("invoice_item.invoiced");
        pendingWhileDrafted.Items.Select(item => item.Currency).ShouldBe(["USD"]);
        pendingAfterDelete.Items.Count.ShouldBe(3);
    }

    [Fact]
    [Trait("Requirement", "CUS-18")]
    [Trait("Requirement", "CUS-19")]
    public async Task BalanceAdjustments_NeedADescription_AndAreListedNewestFirst()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var customer = await CustomerAsync(client);

        using var undescribed = await client.PostAsJsonAsync(
            new Uri($"/api/v1/customers/{customer}/balance-transactions", UriKind.Relative), new { amount = 100, currency = "TRY", description = " " }, ApiClients.Json, CancellationToken);
        var credit = await AdjustAsync(client, customer, 5_000);
        var debt = await AdjustAsync(client, customer, -7_000);
        await AdjustAsync(client, customer, 1_000, "USD");
        var history = await ReadOkAsync<Page<BalanceTransactionResponse>>(await client.GetPathAsync($"/api/v1/customers/{customer}/balance-transactions?currency=TRY"));
        var single = await ReadOkAsync<BalanceTransactionResponse>(await client.GetPathAsync($"/api/v1/customers/{customer}/balance-transactions/{credit.Id}"));
        var customerResponse = await ReadOkAsync<CustomerResponse>(await client.GetPathAsync($"/api/v1/customers/{customer}"));

        (await undescribed.ProblemCodeAsync()).ShouldBe("request.invalid");
        history.Items.Select(transaction => transaction.Id).ShouldBe([debt.Id, credit.Id]);
        (debt.BalanceAfter, single.Description, single.Actor.Type).ShouldBe((-2_000, "Goodwill credit", ActorKind.ApiKey));
        customerResponse.Balances.ShouldBe([new CustomerBalanceResponse("TRY", -2_000), new CustomerBalanceResponse("USD", 1_000)]);
    }

    [Fact]
    [Trait("Requirement", "INV-4")]
    public async Task Drafts_FollowTheCustomersTaxExemption()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        await CatalogAsync(client);
        var customer = await CustomerAsync(client);
        var draft = await CreateAsync(client, new CreateInvoiceRequest { Customer = customer, Currency = "TRY", Lines = [new InvoiceLineRequest { Description = "Setup", UnitAmount = 10_000, TaxRate = "kdv-20-exclusive" }] });

        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri($"/api/v1/customers/{customer}", UriKind.Relative)) { Content = JsonContent.Create(new { isTaxExempt = true }, options: ApiClients.Json) };
        (await client.SendAsync(request, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var exempt = await ReadOkAsync<InvoiceResponse>(await client.GetPathAsync($"/api/v1/invoices/{draft.Id}"));

        (draft.TaxTotal, draft.Total).ShouldBe((2_000, 12_000));
        (exempt.TaxTotal, exempt.Total).ShouldBe((0, 10_000));
    }

    [Fact]
    [Trait("Requirement", "INV-23")]
    public async Task FinalizedInvoices_TakeOnlyTheLegalInvoiceAndMetadata()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var customer = await CustomerAsync(client);
        var invoice = await FinalizeNewAsync(client, customer);

        using var legal = await PatchAsync(client, invoice.Id, new { legalInvoice = new { number = "ABC2026000000123", date = "2026-10-09" }, metadata = new { erp = "42" } });
        using var memo = await PatchAsync(client, invoice.Id, new { memo = "Too late" });
        var updated = await ReadOkAsync<InvoiceResponse>(await client.GetPathAsync($"/api/v1/invoices/{invoice.Id}"));

        legal.StatusCode.ShouldBe(HttpStatusCode.OK, await legal.Content.ReadAsStringAsync(CancellationToken));
        updated.LegalInvoice.ShouldBe(new LegalInvoiceData("ABC2026000000123", new DateOnly(2026, 10, 9)));
        updated.Metadata["erp"].ShouldBe("42");
        (await memo.ProblemCodeAsync()).ShouldBe("invoice.not_draft");
    }

    private static async Task<InvoiceResponse> FinalizeNewAsync(HttpClient client, Guid customer)
    {
        var draft = await CreateAsync(client, new CreateInvoiceRequest { Customer = customer, Currency = "TRY", Lines = [new InvoiceLineRequest { Description = "Line", UnitAmount = 1_000 }] });
        return await ReadOkAsync<InvoiceResponse>(await client.PostPathAsync($"/api/v1/invoices/{draft.Id}/finalize"));
    }

    private static async Task CatalogAsync(HttpClient client)
    {
        await ReadCreatedAsync<TaxRateResponse>(await client.PostJsonAsync("/api/v1/tax-rates", new CreateTaxRateRequest { Key = "kdv-20", Name = "KDV", Percentage = 20, Inclusive = true }));
        await ReadCreatedAsync<TaxRateResponse>(await client.PostJsonAsync("/api/v1/tax-rates", new CreateTaxRateRequest { Key = "kdv-20-exclusive", Name = "KDV", Percentage = 20, Inclusive = false }));
        await ReadCreatedAsync<ProductResponse>(await client.PostJsonAsync("/api/v1/products", new CreateProductRequest { Key = "credits", Name = "1.000 kredi" }));
        await ReadCreatedAsync<ProductResponse>(await client.PostJsonAsync("/api/v1/products", new CreateProductRequest { Key = "pro", Name = "Pro" }));
        await ReadCreatedAsync<PriceResponse>(await client.PostJsonAsync("/api/v1/prices", new CreatePriceRequest
        {
            Product = "credits",
            Key = "credits-1000",
            Currency = "TRY",
            Type = PriceType.OneTime,
            UnitAmount = 29_900,
            TaxRate = "kdv-20",
        }));
        await ReadCreatedAsync<PriceResponse>(await client.PostJsonAsync("/api/v1/prices", new CreatePriceRequest
        {
            Product = "credits",
            Key = "credits-usd",
            Currency = "USD",
            Type = PriceType.OneTime,
            UnitAmount = 999,
        }));
        await ReadCreatedAsync<PriceResponse>(await client.PostJsonAsync("/api/v1/prices", new CreatePriceRequest
        {
            Product = "pro",
            Key = "pro-monthly",
            Currency = "TRY",
            Type = PriceType.Recurring,
            Interval = PriceInterval.Month,
            UnitAmount = 29_900,
        }));
    }

    private static async Task RouteTryToAProviderWithAMinimumOf100Async(HttpClient client)
    {
        await ReadCreatedAsync<ConnectionResponse>(await client.PostJsonAsync("/api/v1/provider-connections", new CreateConnectionRequest
        {
            Key = "fake-main",
            Provider = "fakepay",
            Name = "Fake",
            Environment = ConnectionEnvironment.Sandbox,
            Credentials = new Dictionary<string, string> { ["apiKey"] = "sk_test_0123456789abcdef", ["secretKey"] = "secret" },
        }));
        using var routed = await client.PutAsJsonAsync(
            new Uri("/api/v1/provider-connections/routing", UriKind.Relative), new Dictionary<string, string> { ["TRY"] = "fake-main" }, ApiClients.Json, CancellationToken);
        routed.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<Guid> CustomerAsync(HttpClient client, string? taxId = null) =>
        (await ReadCreatedAsync<CustomerResponse>(await client.PostJsonAsync("/api/v1/customers", new CreateCustomerRequest { Email = "ayse@example.com", Name = "Ayşe Yılmaz", TaxId = taxId }))).Id;

    private static async Task<BalanceTransactionResponse> AdjustAsync(HttpClient client, Guid customer, long amount, string currency = "TRY") =>
        await ReadCreatedAsync<BalanceTransactionResponse>(await client.PostJsonAsync(
            $"/api/v1/customers/{customer}/balance-transactions", new CreateBalanceTransactionRequest { Amount = amount, Currency = currency, Description = "Goodwill credit" }));

    private static async Task<InvoiceResponse> CreateAsync(HttpClient client, CreateInvoiceRequest request) =>
        await ReadCreatedAsync<InvoiceResponse>(await client.PostJsonAsync("/api/v1/invoices", request));

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, Guid invoice, object body) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, new Uri($"/api/v1/invoices/{invoice}", UriKind.Relative)) { Content = JsonContent.Create(body, options: ApiClients.Json) }, CancellationToken);

    private static async Task<T> ReadCreatedAsync<T>(HttpResponseMessage response)
    {
        using (response)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
            return await response.ReadAsync<T>();
        }
    }

    private static async Task<T> ReadOkAsync<T>(HttpResponseMessage response)
    {
        using (response)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
            return await response.ReadAsync<T>();
        }
    }

    private static async Task<T> ReadOkAsync<T>(Task<HttpResponseMessage> response) => await ReadOkAsync<T>(await response);

    private WebApplicationFactory<Program> Host(
        string? database = null, FakeProvider? provider = null, FakeTimeProvider? clock = null, Dictionary<string, string?>? settings = null)
    {
        var all = new Dictionary<string, string?>(settings ?? []) { ["ConnectionStrings:Tahsil"] = database ?? server.NewDatabase("invoices") };
        return server.WithSettings(all, services =>
        {
            if (provider is not null)
            {
                services.AddSingleton<IPaymentProvider>(provider);
            }

            if (clock is not null)
            {
                services.AddSingleton<BillingClock>(new FakeBillingClock(clock));
            }
        });
    }

    [GeneratedRegex(@"^INV-\d{4}-\d{6}$")]
    private static partial Regex NumberPattern();
}
