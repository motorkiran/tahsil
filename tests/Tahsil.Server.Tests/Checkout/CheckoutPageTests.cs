using System.Net;
using System.Net.Http.Json;
using System.Text;
using AngleSharp.Html.Dom;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Providers.Simulator;
using static Tahsil.Server.Tests.Checkout.CheckoutBrowser;
using CheckoutStatus = Tahsil.Domain.Checkout.CheckoutStatus;
using ConsentType = Tahsil.Domain.Customers.ConsentType;
using InvoiceStatus = Tahsil.Domain.Invoices.InvoiceStatus;
using PaymentStatus = Tahsil.Domain.Payments.PaymentStatus;

namespace Tahsil.Server.Tests.Checkout;

/// <summary>The checkout page (CHK §2): what it shows, what it asks, and paying through it with the simulator.</summary>
public sealed class CheckoutPageTests(TahsilServerFixture server)
{
    private static readonly string[] Permissions =
    [
        TahsilPermissions.CheckoutWrite, TahsilPermissions.SubscriptionsRead, TahsilPermissions.CatalogWrite, TahsilPermissions.ConnectionsWrite,
    ];

    private readonly FakeTimeProvider _time = new(FakeBillingClock.Start);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [Trait("Requirement", "CHK-8")]
    [Trait("Requirement", "CHK-11")]
    [Trait("Requirement", "CHK-17")]
    [Trait("Requirement", "CHK-32")]
    [InlineData("tr", "Ödeme", "₺598,00", "₺598,00 öde", "Ön Bilgilendirme Formu'nu okudum.")]
    [InlineData("en", "Checkout", "₺598.00", "Pay ₺598.00", "I have read the Pre-information Form.")]
    public async Task ThePage_ShowsTheSummaryDocumentsAndConsents_AndThePayButtonWaitsForThem(string locale, string title, string total, string button, string checkbox)
    {
        await using var host = Host();
        var session = await PaymentSessionAsync(host, locale);
        using var browser = Open(host);

        using var response = await browser.GetAsync(session.Url, CancellationToken);
        var page = await ParseAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (response.Headers.CacheControl?.NoStore, response.Headers.GetValues("Referrer-Policy").Single(), response.Headers.GetValues("X-Robots-Tag").Single()).ShouldBe((true, "no-referrer", "noindex"));
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("frame-ancestors 'none'");
        page.QuerySelector("h1")!.TextContent.ShouldBe(title);
        page.QuerySelectorAll("#summary .item").Select(row => row.TextContent.Trim()).ShouldHaveSingleItem().ShouldContain("1,000 credits × 2");
        page.QuerySelector("#total-due")!.TextContent.ShouldBe(total);
        page.QuerySelectorAll("#documents .document").Select(document => document.GetAttribute("data-key")).ShouldBe(["pre-information", "distance-sales"]);
        page.QuerySelectorAll("#documents .document-text").ShouldAllBe(text => text.TextContent.Contains("Acme Yazılım A.Ş."));
        var consents = page.QuerySelectorAll("input.required-consent").Cast<IHtmlInputElement>().ToList();
        consents.Select(box => box.IsChecked).ShouldBe([false, false], "required consents start unticked");
        consents[0].ParentElement!.TextContent.Trim().ShouldBe(checkbox);
        page.QuerySelector("#immediate-start").ShouldBeAssignableTo<IHtmlInputElement>()!.IsChecked.ShouldBeFalse();
        var pay = page.QuerySelector("#pay").ShouldBeAssignableTo<IHtmlButtonElement>()!;
        (pay.IsDisabled, pay.TextContent.Trim()).ShouldBe((true, button));
        page.QuerySelector("#final-summary")!.TextContent.ShouldContain(total);
        page.QuerySelector("#merchant")!.TextContent.ShouldContain("MERSİS 0123456789012345");
    }

    [Fact]
    [Trait("Requirement", "CHK-31")]
    public async Task UnknownAndExpiredSessions_AreNotFound()
    {
        await using var host = Host();
        var session = await PaymentSessionAsync(host, "tr");
        using var browser = Open(host);
        using var api = await host.CreateClientWithAsync(Permissions);
        (await api.PostPathAsync($"/api/v1/checkout-sessions/{session.Id}/expire")).Dispose();

        using var unknown = await browser.GetAsync(new Uri("/checkout/cs_nope", UriKind.Relative), CancellationToken);
        using var expired = await browser.GetAsync(session.Url, CancellationToken);

        (unknown.StatusCode, expired.StatusCode).ShouldBe((HttpStatusCode.NotFound, HttpStatusCode.NotFound));
    }

    [Theory]
    [Trait("Requirement", "CHK-10")]
    [Trait("Requirement", "CHK-11")]
    [InlineData("missing name", "Gerekli bilgileri doldurun.")]
    [InlineData("company without tax number", "Gerekli bilgileri doldurun.")]
    [InlineData("unticked document", "Devam etmek için gerekli kutuları işaretleyin.")]
    public async Task IncompleteForms_AreRefused_BeforeAnyPayment(string reason, string message)
    {
        await using var host = Host();
        var session = await PaymentSessionAsync(host, "tr");
        using var browser = Open(host);
        var form = Form("ayse@example.com", "Ayşe Yılmaz", ["pre-information", "distance-sales"]);
        switch (reason)
        {
            case "missing name":
                form["Form.Name"] = string.Empty;
                break;
            case "company without tax number":
                form["Form.Company"] = "true";
                break;
            default:
                form.Remove("Form.Documents#1");
                break;
        }

        using var response = await SubmitAsync(browser, session.Url!, form);
        var page = await ParseAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        page.QuerySelector("#checkout-error")!.TextContent.ShouldBe(message);
        (await QueryAsync(host, context => context.Payments.CountAsync(CancellationToken))).ShouldBe(0);
    }

    [Fact]
    [Trait("Requirement", "CHK-16")]
    [Trait("Requirement", "CHK-18")]
    [Trait("Requirement", "CHK-19")]
    [Trait("Requirement", "CHK-23")]
    [Trait("Requirement", "PAY-8")]
    public async Task AOneTimePurchase_PaysAPaidInvoice_AndRecordsTheAcceptedDocuments()
    {
        await using var host = Host();
        var session = await PaymentSessionAsync(host, "tr", new CheckoutCustomerDetails { ExternalId = "user-42", Locale = "tr" });
        using var browser = Open(host);

        using var submitted = await SubmitAsync(browser, session.Url!, Form("ayse@example.com", "Ayşe Yılmaz", ["pre-information", "distance-sales"], immediateStart: true));
        var returned = await PayOnTheEmbedAsync(host, browser, submitted, "4242424242424242");
        var page = await ParseAsync(returned);

        returned.StatusCode.ShouldBe(HttpStatusCode.OK);
        page.QuerySelector("#continue")!.GetAttribute("href").ShouldBe($"https://shop.example.com/done?session={session.Id}");
        var stored = await QueryAsync(host, context => context.CheckoutSessions.SingleAsync(candidate => candidate.Id == session.Id, CancellationToken));
        var invoice = await QueryAsync(host, context => context.Invoices.SingleAsync(candidate => candidate.Id == stored.InvoiceId, CancellationToken));
        var payment = await QueryAsync(host, context => context.Payments.SingleAsync(candidate => candidate.Id == stored.PaymentId, CancellationToken));
        var customer = await QueryAsync(host, context => context.Customers.SingleAsync(candidate => candidate.Id == stored.CustomerId, CancellationToken));
        var consents = await QueryAsync(host, context => context.ConsentRecords.Where(record => record.CheckoutSessionId == session.Id).ToListAsync(CancellationToken));

        stored.Status.ShouldBe(CheckoutStatus.Complete);
        (invoice.Status, invoice.BillingReason, invoice.Total, invoice.AmountPaid).ShouldBe((InvoiceStatus.Paid, Domain.Invoices.BillingReason.Checkout, 59_800L, 59_800L));
        invoice.Number.ShouldNotBeNull();
        (payment.Status, payment.InvoiceId, payment.Amount).ShouldBe((PaymentStatus.Succeeded, (Guid?)invoice.Id, 59_800L));
        (customer.ExternalId, customer.Name, customer.Email).ShouldBe(("user-42", "Ayşe Yılmaz", "ayse@example.com"));
        consents.Select(record => record.Type).ShouldBe([ConsentType.PreInformation, ConsentType.DistanceSalesContract, ConsentType.ImmediateStart], ignoreOrder: true);
        var preInformation = consents.Single(record => record.Type == ConsentType.PreInformation);
        (preInformation.DocumentKey, preInformation.DocumentVersion, preInformation.TextShown).ShouldBe(("pre-information", "2026-10", "Ön Bilgilendirme Formu'nu okudum."));
        preInformation.ReadDocument().ShouldNotBeNull().ShouldContain("Ayşe Yılmaz");
        (await QueryAsync(host, context => context.AuditEvents.Where(auditEvent => auditEvent.Action == "checkout.completed").CountAsync(CancellationToken))).ShouldBe(1);
    }

    [Fact]
    [Trait("Requirement", "CHK-19")]
    [Trait("Requirement", "PAY-26")]
    public async Task ADeclinedCard_ShowsWhy_AndAnotherAttemptCanPay()
    {
        await using var host = Host();
        var session = await PaymentSessionAsync(host, "tr");
        using var browser = Open(host);

        using var declined = await PayOnTheEmbedAsync(host, browser, await SubmitAsync(browser, session.Url!, Form("ayse@example.com", "Ayşe", ["pre-information", "distance-sales"])), "4000000000000002");
        declined.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        using var retry = await browser.GetAsync(declined.Headers.Location, CancellationToken);
        var retryPage = await ParseAsync(retry);
        using var paid = await PayOnTheEmbedAsync(host, browser, await SubmitAsync(browser, session.Url!, Form("ayse@example.com", "Ayşe", ["pre-information", "distance-sales"])), "4242424242424242");

        declined.Headers.Location!.ToString().ShouldEndWith("?failed=card_declined");
        retryPage.QuerySelector("#checkout-error")!.TextContent.ShouldBe("Kartınız bankanız tarafından reddedildi.");
        paid.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stored = await QueryAsync(host, context => context.CheckoutSessions.SingleAsync(candidate => candidate.Id == session.Id, CancellationToken));
        (stored.Status, stored.PaymentAttempts).ShouldBe((CheckoutStatus.Complete, 2));
    }

    [Fact]
    [Trait("Requirement", "CHK-1")]
    [Trait("Requirement", "CHK-13")]
    [Trait("Requirement", "CHK-23")]
    public async Task ASetupSession_SavesTheCardWithItsConsent()
    {
        await using var host = Host();
        using var api = await host.CreateClientWithAsync(Permissions);
        using var created = await api.PostJsonAsync("/api/v1/checkout-sessions", new CreateCheckoutSessionRequest
        {
            Mode = CheckoutMode.Setup,
            Currency = "TRY",
            Locale = "en",
            SuccessUrl = "https://shop.example.com/card-saved",
        });
        var session = await created.ReadAsync<CheckoutSessionResponse>();
        using var browser = Open(host);

        using var withoutConsent = await SubmitAsync(browser, session.Url!, Form("ada@example.com", "Ada", []));
        var form = Form("ada@example.com", "Ada", []);
        form["Form.CardStorage"] = "true";
        using var returned = await PayOnTheEmbedAsync(host, browser, await SubmitAsync(browser, session.Url!, form), "4242424242424242");

        withoutConsent.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        returned.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stored = await QueryAsync(host, context => context.CheckoutSessions.SingleAsync(candidate => candidate.Id == session.Id, CancellationToken));
        var method = await QueryAsync(host, context => context.PaymentMethods.SingleAsync(candidate => candidate.Id == stored.PaymentMethodId, CancellationToken));
        var consent = await QueryAsync(host, context => context.ConsentRecords.SingleAsync(record => record.Id == method.ConsentRecordId, CancellationToken));
        (stored.Status, stored.InvoiceId).ShouldBe((CheckoutStatus.Complete, null));
        (consent.Type, consent.CheckoutSessionId).ShouldBe((ConsentType.CardStorage, (Guid?)session.Id));
        consent.TextShown.ShouldStartWith("I agree that Acme saves my card");
        (await QueryAsync(host, context => context.Payments.SingleAsync(payment => payment.CheckoutSessionId == session.Id, CancellationToken))).Amount.ShouldBe(0);
    }

    [Fact]
    [Trait("Requirement", "PAY-13")]
    [Trait("Requirement", "CHK-23")]
    public async Task TheReturnAndANotificationTogether_CompleteTheSessionOnce()
    {
        await using var host = Host();
        var session = await PaymentSessionAsync(host, "tr");
        using var browser = Open(host);
        using var submitted = await SubmitAsync(browser, session.Url!, Form("ayse@example.com", "Ayşe", ["pre-information", "distance-sales"]));
        var token = await SimulatorTokenAsync(submitted);
        var redirect = (await host.Services.GetRequiredService<SimulatorBank>().SubmitCardAsync(token, new CardEntryForm("4242424242424242", "12/30", "123", SaveCard: false), CancellationToken))
            .ShouldBeOfType<CardPageResult.Redirect>();
        var paymentId = await QueryAsync(host, context => context.Payments.Where(payment => payment.CheckoutSessionId == session.Id).Select(payment => payment.Id).SingleAsync(CancellationToken));
        using (var notification = await NotifyAsync(host, paymentId.ToString("N")))
        {
            notification.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var returned = browser.GetAsync(redirect.Url, CancellationToken);
        await Task.WhenAll(returned, host.RunWorkersUntilIdleAsync());
        (await returned).Dispose();

        (await QueryAsync(host, context => context.Invoices.CountAsync(CancellationToken))).ShouldBe(1);
        (await QueryAsync(host, context => context.AuditEvents.CountAsync(auditEvent => auditEvent.Action == "checkout.completed" || auditEvent.Action == "payment.succeeded", CancellationToken))).ShouldBe(2);
    }

    [Fact]
    [Trait("Requirement", "ENG-18")]
    [Trait("Requirement", "DOM-10")]
    public async Task ASessionExpiring_WithAnAbandonedPayment_CancelsItFirst()
    {
        await using var host = Host();
        var session = await PaymentSessionAsync(host, "tr", expiresIn: "PT30M");
        using var browser = Open(host);
        (await SubmitAsync(browser, session.Url!, Form("ayse@example.com", "Ayşe", ["pre-information", "distance-sales"]))).Dispose();

        _time.Advance(TimeSpan.FromMinutes(30));
        await host.RunWorkersUntilIdleAsync();

        var stored = await QueryAsync(host, context => context.CheckoutSessions.SingleAsync(candidate => candidate.Id == session.Id, CancellationToken));
        var payment = await QueryAsync(host, context => context.Payments.SingleAsync(candidate => candidate.CheckoutSessionId == session.Id, CancellationToken));
        (stored.Status, payment.Status).ShouldBe((CheckoutStatus.Expired, PaymentStatus.Canceled));
    }

    private WebApplicationFactory<Program> Host() => IsolatedHosts.Create(server, server.NewDatabase("checkout_page"), _time);

    /// <summary>A session that sells two of a ₺299.00 price with KDV inside, through the simulator, which notifies only when told.</summary>
    private static async Task<CheckoutSessionResponse> PaymentSessionAsync(
        WebApplicationFactory<Program> host, string locale, CheckoutCustomerDetails? customer = null, string? expiresIn = null)
    {
        using var api = await host.CreateClientWithAsync(Permissions);
        using (var request = new HttpRequestMessage(HttpMethod.Patch, new Uri("/api/v1/provider-connections/simulator", UriKind.Relative))
        {
            Content = JsonContent.Create(new { settings = new { notifications = false } }, options: ApiClients.Json),
        })
        {
            (await api.SendAsync(request, CancellationToken)).EnsureSuccessStatusCode().Dispose();
        }

        (await api.PostJsonAsync("/api/v1/tax-rates", new CreateTaxRateRequest { Key = "kdv-20", Name = "KDV", Percentage = 20, Inclusive = true })).EnsureSuccessStatusCode().Dispose();
        (await api.PostJsonAsync("/api/v1/products", new CreateProductRequest { Key = "credits", Name = "1,000 credits" })).EnsureSuccessStatusCode().Dispose();
        (await api.PostJsonAsync("/api/v1/prices", new CreatePriceRequest { Product = "credits", Key = "credits-1000", Currency = "TRY", Type = PriceType.OneTime, UnitAmount = 29_900, TaxRate = "kdv-20" }))
            .EnsureSuccessStatusCode().Dispose();
        using var created = await api.PostJsonAsync("/api/v1/checkout-sessions", new CreateCheckoutSessionRequest
        {
            Mode = CheckoutMode.Payment,
            Items = [new CheckoutItemRequest { Price = "credits-1000", Quantity = 2 }],
            Locale = locale,
            CustomerDetails = customer,
            ExpiresIn = expiresIn,
            SuccessUrl = "https://shop.example.com/done?session={CHECKOUT_SESSION_ID}",
            CancelUrl = "https://shop.example.com/cart",
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(CancellationToken));
        return await created.ReadAsync<CheckoutSessionResponse>();
    }

    private static async Task<HttpResponseMessage> NotifyAsync(WebApplicationFactory<Program> host, string reference)
    {
        var connection = (await host.QueryAsync<Guid>($"SELECT id AS \"Value\" FROM tahsil.provider_connections WHERE key = 'simulator'")).Single();
        var body = Encoding.UTF8.GetBytes($$"""{"id":"evt_1","type":"payment.succeeded","reference":"{{reference}}"}""");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/provider-notifications/{connection}", UriKind.Relative)) { Content = new ByteArrayContent(body) };
        request.Headers.TryAddWithoutValidation(SimulatorBank.SignatureHeader, await host.Services.GetRequiredService<SimulatorBank>().SignAsync(connection, 1, body, CancellationToken));
        using var client = host.CreateClient();
        return await client.SendAsync(request, CancellationToken);
    }

    private static async Task<T> QueryAsync<T>(WebApplicationFactory<Program> host, Func<TahsilDbContext, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TahsilDbContext>());
    }
}
