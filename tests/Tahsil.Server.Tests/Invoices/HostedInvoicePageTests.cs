using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using Tahsil.Providers.Simulator;
using static Tahsil.Server.Tests.Checkout.CheckoutBrowser;

namespace Tahsil.Server.Tests.Invoices;

/// <summary>The hosted invoice page (INV-24 to INV-28): links, content in both languages, payment and print.</summary>
public sealed partial class HostedInvoicePageTests(TahsilServerFixture server)
{
    /// <summary>Set to rewrite the HTML snapshots after an intended change to the page.</summary>
    private const string UpdateSnapshotVariable = "TAHSIL_UPDATE_SNAPSHOTS";

    private static readonly string[] Permissions =
    [
        TahsilPermissions.CustomersWrite, TahsilPermissions.InvoicesWrite, TahsilPermissions.InvoicesRead, TahsilPermissions.CatalogWrite, TahsilPermissions.ConnectionsWrite,
    ];

    private static readonly string ProjectDirectory = typeof(HostedInvoicePageTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(metadata => metadata.Key == "ProjectDirectory").Value!;

    private readonly FakeTimeProvider _time = new(FakeBillingClock.Start);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [Trait("Requirement", "INV-25")]
    [Trait("Requirement", "INV-27")]
    [Trait("Requirement", "INV-28")]
    [InlineData("tr", "Ödeme Talebi", "₺120,00", "Bu belge vergi mevzuatı kapsamında fatura yerine geçmez.")]
    [InlineData("en", "Invoice", "₺120.00", "This document does not replace an invoice under tax law.")]
    public async Task AnOpenInvoice_ShowsTheDocument_InTheCustomersLanguage(string locale, string title, string total, string note)
    {
        await using var host = Host();
        var invoice = await OpenInvoiceAsync(host, locale);
        using var browser = Open(host);

        using var response = await browser.GetAsync(invoice.HostedInvoiceUrl, CancellationToken);
        var page = await ParseAsync(response);

        (response.Headers.CacheControl?.NoStore, response.Headers.GetValues("Referrer-Policy").Single(), response.Headers.GetValues("X-Robots-Tag").Single()).ShouldBe((true, "no-referrer", "noindex"));
        page.QuerySelector("h1")!.TextContent.ShouldBe(title);
        page.QuerySelector(".invoice-number")!.TextContent.ShouldBe(invoice.Number);
        page.QuerySelector("#seller")!.TextContent.ShouldContain("Acme Yazılım A.Ş.");
        page.QuerySelector("#seller")!.TextContent.ShouldContain("1234567890");
        page.QuerySelector("#customer")!.TextContent.ShouldContain("Ayşe Yılmaz");
        page.QuerySelectorAll("#lines .line").Select(line => line.TextContent).ShouldHaveSingleItem().ShouldContain("Consulting");
        page.QuerySelectorAll("#lines .tax").ShouldHaveSingleItem();
        page.QuerySelector("#total")!.TextContent.ShouldBe(total);
        page.QuerySelector("#tax-note")!.TextContent.ShouldBe(note);
        page.QuerySelector("#pay").ShouldNotBeNull();
    }

    [Fact]
    [Trait("Requirement", "INV-24")]
    public async Task Links_AreVerified_AndAnotherKeyVersionInvalidatesThem()
    {
        var database = server.NewDatabase("invoice_page");
        Uri link;
        await using (var host = Host(database))
        {
            var invoice = await OpenInvoiceAsync(host, "tr");
            link = invoice.HostedInvoiceUrl!;
            using var browser = Open(host);
            using var valid = await browser.GetAsync(link, CancellationToken);
            var token = link.Segments[^1];
            using var tampered = await browser.GetAsync(new Uri($"/invoices/{token[..^2]}{(token[^2] == 'A' ? 'B' : 'A')}{token[^1]}", UriKind.Relative), CancellationToken);
            (valid.StatusCode, tampered.StatusCode).ShouldBe((HttpStatusCode.OK, HttpStatusCode.NotFound));
        }

        await using var rotated = Host(database, new() { ["Tahsil:Invoices:LinkKeyVersion"] = "2" });
        using var after = await Open(rotated).GetAsync(link, CancellationToken);

        after.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Requirement", "INV-24")]
    public async Task Drafts_HaveNoPage()
    {
        await using var host = Host();
        using var api = await host.CreateClientWithAsync(Permissions);
        var customer = await CustomerAsync(api, "tr");
        using var draft = await api.PostJsonAsync("/api/v1/invoices", new CreateInvoiceRequest { Customer = customer, Currency = "TRY", Lines = [new InvoiceLineRequest { Description = "Draft", UnitAmount = 100 }] });

        (await draft.ReadAsync<InvoiceResponse>()).HostedInvoiceUrl.ShouldBeNull();
    }

    [Fact]
    [Trait("Requirement", "INV-26")]
    [Trait("Requirement", "PAY-8")]
    [Trait("Requirement", "PTR-8")]
    public async Task AnOpenInvoice_IsPaidByCard_AndBecomesAReceipt()
    {
        await using var host = Host();
        var invoice = await OpenInvoiceAsync(host, "tr");
        using var browser = Open(host);
        using var get = await browser.GetAsync(invoice.HostedInvoiceUrl, CancellationToken);
        var form = await ParseAsync(get);
        using var started = await browser.PostAsync(
            new Uri($"{invoice.HostedInvoiceUrl}?handler=Pay"),
            new FormUrlEncodedContent([new("__RequestVerificationToken", form.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!)]),
            CancellationToken);
        var token = SimulatorToken().Match((await ParseAsync(started)).QuerySelector("#payment iframe")!.GetAttribute("src")!).Groups["token"].Value;
        var redirect = (await host.Services.GetRequiredService<SimulatorBank>().SubmitCardAsync(token, new CardEntryForm("4242424242424242", "12/30", "123", SaveCard: false), CancellationToken))
            .ShouldBeOfType<CardPageResult.Redirect>();
        using var returned = await browser.GetAsync(redirect.Url, CancellationToken);
        using var receipt = await browser.GetAsync(returned.Headers.Location, CancellationToken);
        var page = await ParseAsync(receipt);
        using var framed = new HttpRequestMessage(HttpMethod.Get, redirect.Url);
        framed.Headers.Add("Sec-Fetch-Dest", "iframe");
        using var inFrame = await browser.SendAsync(framed, CancellationToken);

        returned.Headers.Location!.ToString().ShouldEndWith("?paid=true");
        inFrame.StatusCode.ShouldBe(HttpStatusCode.OK, "a provider that returns inside its frame gets a page that moves the whole window (PTR-8)");
        var leave = (await ParseAsync(inFrame)).QuerySelector("#continue")!;
        (leave.GetAttribute("href"), leave.GetAttribute("target")).ShouldBe((returned.Headers.Location!.ToString(), "_top"));
        inFrame.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("frame-ancestors https://www.paytr.com");
        page.QuerySelector("h1")!.TextContent.ShouldBe("Tahsilat Makbuzu");
        page.QuerySelector("#paid-notice").ShouldNotBeNull();
        page.QuerySelectorAll("#payments li").ShouldHaveSingleItem().TextContent.ShouldContain("₺120,00");
        page.QuerySelector("#pay").ShouldBeNull();
    }

    [Fact]
    [Trait("Requirement", "INV-26")]
    public async Task BankTransferInstructions_ShowTheInvoiceNumberAsTheReference()
    {
        await using var host = Host(settings: new()
        {
            ["Tahsil:Invoices:BankTransfer:TRY:Bank"] = "Örnek Bankası",
            ["Tahsil:Invoices:BankTransfer:TRY:AccountHolder"] = "Acme Yazılım A.Ş.",
            ["Tahsil:Invoices:BankTransfer:TRY:Iban"] = "TR33 0006 1005 1978 6457 8413 26",
        });
        var invoice = await OpenInvoiceAsync(host, "tr");

        var page = await ParseAsync(await Open(host).GetAsync(invoice.HostedInvoiceUrl, CancellationToken));

        page.QuerySelector("#iban")!.TextContent.ShouldBe("TR33 0006 1005 1978 6457 8413 26");
        page.QuerySelector("#reference")!.TextContent.ShouldBe(invoice.Number);
    }

    [Theory]
    [Trait("Requirement", "INV-25")]
    [Trait("Requirement", "INV-27")]
    [InlineData("open")]
    [InlineData("paid")]
    [InlineData("void")]
    [InlineData("uncollectible")]
    public async Task EachStatus_MatchesItsReviewedSnapshot(string status)
    {
        await using var host = Host();
        var invoice = await OpenInvoiceAsync(host, "tr");
        using var api = await host.CreateClientWithAsync(Permissions);
        switch (status)
        {
            case "void":
                (await api.PostPathAsync($"/api/v1/invoices/{invoice.Id}/void")).EnsureSuccessStatusCode().Dispose();
                break;
            case "uncollectible":
                (await api.PostPathAsync($"/api/v1/invoices/{invoice.Id}/mark-uncollectible")).EnsureSuccessStatusCode().Dispose();
                break;
            case "paid":
                await Payments.PaymentScenarios.PayOnPageAsync(host, invoice.Id, "4242424242424242");
                break;
        }

        var page = await ParseAsync(await Open(host).GetAsync(invoice.HostedInvoiceUrl, CancellationToken));
        var html = Normalize(page.QuerySelector("#invoice")!.OuterHtml + (page.QuerySelector("#pay-section")?.OuterHtml ?? string.Empty));
        var path = Path.Combine(ProjectDirectory, "Invoices", "Snapshots", $"{status}.html");

        if (Environment.GetEnvironmentVariable(UpdateSnapshotVariable) is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, html, CancellationToken);
        }

        File.Exists(path).ShouldBeTrue($"No snapshot yet; run the tests with {UpdateSnapshotVariable}=1, review and commit it.");
        html.ShouldBe(await File.ReadAllTextAsync(path, CancellationToken), $"The page changed. Run the tests with {UpdateSnapshotVariable}=1, review the snapshot and commit it.");
    }

    private WebApplicationFactory<Program> Host(string? database = null, Dictionary<string, string?>? settings = null)
    {
        var host = IsolatedHosts.Create(server, database ?? server.NewDatabase("invoice_page"), _time);
        return settings is null ? host : host.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings)));
    }

    private static async Task<Guid> CustomerAsync(HttpClient api, string locale)
    {
        using var customer = await api.PostJsonAsync("/api/v1/customers", new CreateCustomerRequest
        {
            Email = "ayse@example.com",
            Name = "Ayşe Yılmaz",
            Locale = locale,
            Address = new CustomerAddress("Moda Cad. 1", null, "Kadıköy", "İstanbul", "34710", "TR"),
        });
        customer.StatusCode.ShouldBe(HttpStatusCode.Created, await customer.Content.ReadAsStringAsync(CancellationToken));
        return (await customer.ReadAsync<CustomerResponse>()).Id;
    }

    /// <summary>A finalized ₺100.00 invoice with 20% tax added, sent for payment on its page.</summary>
    private static async Task<InvoiceResponse> OpenInvoiceAsync(WebApplicationFactory<Program> host, string locale)
    {
        using var api = await host.CreateClientWithAsync(Permissions);
        using (var request = new HttpRequestMessage(HttpMethod.Patch, new Uri("/api/v1/provider-connections/simulator", UriKind.Relative))
        {
            Content = JsonContent.Create(new { settings = new { notifications = false } }, options: ApiClients.Json),
        })
        {
            (await api.SendAsync(request, CancellationToken)).EnsureSuccessStatusCode().Dispose();
        }

        (await api.PostJsonAsync("/api/v1/tax-rates", new CreateTaxRateRequest { Key = "vat-20", Name = "KDV", Percentage = 20, Inclusive = false })).EnsureSuccessStatusCode().Dispose();
        var customer = await CustomerAsync(api, locale);
        using var draft = await api.PostJsonAsync("/api/v1/invoices", new CreateInvoiceRequest
        {
            Customer = customer,
            Currency = "TRY",
            CollectionMethod = CollectionMethod.SendInvoice,
            DaysUntilDue = 7,
            Lines = [new InvoiceLineRequest { Description = "Consulting", UnitAmount = 10_000, TaxRate = "vat-20" }],
        });
        draft.StatusCode.ShouldBe(HttpStatusCode.Created, await draft.Content.ReadAsStringAsync(CancellationToken));
        using var finalized = await api.PostPathAsync($"/api/v1/invoices/{(await draft.ReadAsync<InvoiceResponse>()).Id}/finalize");
        finalized.StatusCode.ShouldBe(HttpStatusCode.OK, await finalized.Content.ReadAsStringAsync(CancellationToken));
        return await finalized.ReadAsync<InvoiceResponse>();
    }

    /// <summary>The page without what changes on every run: tokens, nonces and IDs.</summary>
    private static string Normalize(string html) =>
        InvoiceLink().Replace(AntiforgeryValue().Replace(Uuid().Replace(html, "{id}"), "value=\"{antiforgery}\""), "/invoices/{token}").Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

    [GeneratedRegex("/invoices/[A-Za-z0-9_-]{64}")]
    private static partial Regex InvoiceLink();

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex Uuid();

    [GeneratedRegex("value=\"CfDJ8[^\"]+\"")]
    private static partial Regex AntiforgeryValue();

    [GeneratedRegex("simulator/pay/(?<token>[A-Za-z0-9_-]+)")]
    private static partial Regex SimulatorToken();
}
