using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using static Tahsil.Server.Tests.Checkout.CheckoutBrowser;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;

namespace Tahsil.Server.Tests.Checkout;

/// <summary>
/// Providers whose card fields are on Tahsil's page and post straight to the provider (ADP-13), with a PayTR
/// connection in direct form mode, which needs no call to PayTR to show its form (PTR-9).
/// </summary>
public sealed class DirectFormTests(TahsilServerFixture server)
{
    private static readonly string[] Documents = ["pre-information", "distance-sales"];

    private readonly FakeTimeProvider _time = new(FakeBillingClock.Start);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "ADP-13")]
    [Trait("Requirement", "PTR-9")]
    public async Task TheCardForm_IsShownAlone_WithoutScripts_AndPostsOnlyToTheProvider()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        await ConnectionAsync(api, "paytr-direct", directForm: true);
        using (var routed = await api.PutAsJsonAsync(
            new Uri("/api/v1/provider-connections/routing", UriKind.Relative), new Dictionary<string, string> { ["TRY"] = "paytr-direct" }, ApiClients.Json, CancellationToken))
        {
            routed.StatusCode.ShouldBe(HttpStatusCode.OK, await routed.Content.ReadAsStringAsync(CancellationToken));
        }

        using var created = await api.PostJsonAsync("/api/v1/checkout-sessions", new CreateCheckoutSessionRequest
        {
            Mode = CheckoutMode.Payment,
            Items = [new CheckoutItemRequest { Price = "setup-fee-try" }],
            CustomerDetails = new CheckoutCustomerDetails { Locale = "en" },
            SuccessUrl = "https://shop.example.com/done",
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(CancellationToken));
        var session = await created.ReadAsync<CheckoutSessionResponse>();
        using var browser = Open(host);

        using var submitted = await SubmitAsync(browser, session.Url!, Form("ada@example.com", "Ada Lovelace", Documents));
        var html = await submitted.Content.ReadAsStringAsync(CancellationToken);
        var page = await ParseAsync(submitted);

        submitted.StatusCode.ShouldBe(HttpStatusCode.OK, html);
        var form = page.QuerySelector("#card-form")!;
        (form.GetAttribute("action"), form.GetAttribute("method")).ShouldBe(("https://www.paytr.com/odeme", "post"));
        form.QuerySelectorAll("input:not([type=hidden])").Select(input => input.GetAttribute("name")).ShouldBe(["cc_owner", "card_number", "expiry_month", "expiry_year", "cvv"]);
        form.QuerySelector("input[name=payment_amount]")!.GetAttribute("value").ShouldBe("100.00");
        form.QuerySelector("input[name=paytr_token]")!.GetAttribute("value").ShouldNotBeNullOrEmpty();
        form.QuerySelector("input[name=__RequestVerificationToken]").ShouldBeNull("nothing of Tahsil's goes to the provider");
        page.QuerySelectorAll("form").Length.ShouldBe(1, "the card form is alone on the page");
        page.QuerySelectorAll("script").ShouldBeEmpty();
        var policy = submitted.Headers.GetValues("Content-Security-Policy").Single();
        policy.ShouldContain("script-src 'none'");
        policy.ShouldContain("form-action 'self' https://www.paytr.com");
        page.QuerySelector("#card-form-note")!.TextContent.ShouldBe("Your card details go straight to the payment provider (www.paytr.com); they never reach us.");
    }

    [Fact]
    [Trait("Requirement", "PTR-2")]
    public async Task AConnectionWithSavedCards_KeepsItsWayOfTakingCards()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        var withCards = await ConnectionAsync(api, "paytr-main", directForm: false);
        await ConnectionAsync(api, "paytr-new", directForm: false);
        using var created = await api.PostJsonAsync("/api/v1/customers", new CreateCustomerRequest { Name = "Ada Lovelace", Email = "ada@example.com", Locale = "en" });
        var card = await SaveCardAsync(api, (await created.ReadAsync<CustomerResponse>()).Id, "4242424242424242");
        await host.ExecuteSqlAsync($"UPDATE tahsil.payment_methods SET connection_id = {withCards} WHERE id = {card}");

        using var refused = await api.PatchJsonAsync("/api/v1/provider-connections/paytr-main", new { settings = new { allowDirectForm = true } });
        using var allowed = await api.PatchJsonAsync("/api/v1/provider-connections/paytr-new", new { settings = new { allowDirectForm = true } });

        (refused.StatusCode, await refused.ProblemCodeAsync()).ShouldBe((HttpStatusCode.Conflict, "connection.mode_change_with_cards"));
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync(CancellationToken));
        (await allowed.ReadAsync<ConnectionResponse>()).Settings["allowDirectForm"].GetBoolean().ShouldBeTrue();
    }

    private static async Task<Guid> ConnectionAsync(HttpClient api, string key, bool directForm)
    {
        using var created = await api.PostJsonAsync("/api/v1/provider-connections", new
        {
            key,
            provider = "paytr",
            name = "PayTR",
            environment = "sandbox",
            credentials = new { merchantId = "123456", merchantKey = "KKKEEEYYY", merchantSalt = "XXXYYYZZZ" },
            settings = new { allowDirectForm = directForm },
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(CancellationToken));
        return (await created.ReadAsync<ConnectionResponse>()).Id;
    }

    private WebApplicationFactory<Program> Host() => IsolatedHosts.Create(server, server.NewDatabase("direct_form"), _time);
}
