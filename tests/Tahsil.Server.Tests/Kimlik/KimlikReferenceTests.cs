using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;

namespace Tahsil.Server.Tests.Kimlik;

/// <summary>
/// Products' Kimlik plans and customers' Kimlik subscribers, checked against Kimlik while it can be reached (KML-12,
/// CAT-5, CUS-3), with <see cref="KimlikStub"/>.
/// </summary>
public sealed class KimlikReferenceTests : IDisposable
{
    private static readonly DateTimeOffset April = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly TahsilServerFixture _server;
    private readonly FakeTimeProvider _time;
    private readonly KimlikStub _kimlik;

    public KimlikReferenceTests(TahsilServerFixture server)
    {
        _server = server;
        _time = new FakeTimeProvider(April);
        _kimlik = new KimlikStub(_time);
        _kimlik.AddPlan("pro");
        _kimlik.AddPlan("extra-seats", kind: "addOn");
        _kimlik.AddPlan("legacy", archived: true);
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose() => _kimlik.Dispose();

    [Fact]
    [Trait("Requirement", "CAT-5")]
    [Trait("Requirement", "KML-12")]
    public async Task AProductsKimlikPlan_IsABasePlanOpenInKimlik_UnlessKimlikCannotBeReached()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);

        using var known = await ProductAsync(api, "pro-product", "pro");
        using var unknown = await ProductAsync(api, "unknown-product", "enterprise");
        using var addOn = await ProductAsync(api, "seats-product", "extra-seats");
        using var archived = await ProductAsync(api, "legacy-product", "legacy");
        var planReads = PlanReads();
        _kimlik.AddPlan("team");
        using var added = await ProductAsync(api, "team-product", "team");
        using var cached = await ProductAsync(api, "second-pro", "pro");
        var planReadsAfter = PlanReads();
        _kimlik.Fail(HttpStatusCode.ServiceUnavailable);
        using var unverified = await ProductAsync(api, "later-product", "enterprise");
        using var changed = await api.PatchJsonAsync("/api/v1/products/team", new UpdateProductRequest { KimlikPlan = "enterprise" });

        known.StatusCode.ShouldBe(HttpStatusCode.Created);
        foreach (var refused in new[] { unknown, addOn, archived, changed })
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await refused.ProblemCodeAsync()).ShouldBe("product.unknown_kimlik_plan");
        }

        (added.StatusCode, cached.StatusCode).ShouldBe((HttpStatusCode.Created, HttpStatusCode.Created));
        planReadsAfter.ShouldBe(planReads + 1, "plans are kept five minutes, and read again for one created in Kimlik meanwhile");
        unverified.StatusCode.ShouldBe(HttpStatusCode.Created, "the connector checks it again when it syncs");
    }

    [Fact]
    [Trait("Requirement", "CUS-3")]
    [Trait("Requirement", "KML-12")]
    public async Task ACustomersKimlikSubscriber_ExistsInKimlik_AndStaysWhileAKimlikPlanIsSubscribed()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        var ada = _kimlik.AddUser();
        var acme = _kimlik.AddOrganization("acme");
        using (var product = await api.PatchJsonAsync("/api/v1/products/team", new UpdateProductRequest { KimlikPlan = "pro" }))
        {
            product.StatusCode.ShouldBe(HttpStatusCode.OK, await product.Content.ReadAsStringAsync(CancellationToken));
        }

        using var nobody = await CustomerAsync(api, "nobody@example.com", new KimlikSubscriber(KimlikSubscriberType.User, Guid.CreateVersion7()));
        using var user = await CustomerAsync(api, "ada@example.com", new KimlikSubscriber(KimlikSubscriberType.User, ada));
        using var organization = await CustomerAsync(api, "acme@example.com", new KimlikSubscriber(KimlikSubscriberType.Organization, acme));
        var customer = (await user.ReadAsync<CustomerResponse>()).Id;
        await SaveCardAsync(api, customer, "4242424242424242");
        var subscription = await CreateAsync(api, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try" }] });
        var other = new KimlikSubscriber(KimlikSubscriberType.User, _kimlik.AddUser());
        using var locked = await api.PatchJsonAsync($"/api/v1/customers/{customer}", new UpdateCustomerRequest { KimlikSubscriber = other });
        using (var canceled = await api.PostJsonAsync($"/api/v1/subscriptions/{subscription.Id}/cancel", new CancelSubscriptionRequest { At = "now" }))
        {
            canceled.EnsureSuccessStatusCode();
        }

        using var relinked = await api.PatchJsonAsync($"/api/v1/customers/{customer}", new UpdateCustomerRequest { KimlikSubscriber = other });
        using var toNobody = await api.PatchJsonAsync($"/api/v1/customers/{customer}", new UpdateCustomerRequest { KimlikSubscriber = new KimlikSubscriber(KimlikSubscriberType.User, Guid.CreateVersion7()) });
        _kimlik.Fail(HttpStatusCode.ServiceUnavailable);
        using var unverified = await CustomerAsync(api, "later@example.com", new KimlikSubscriber(KimlikSubscriberType.User, Guid.CreateVersion7()));

        (nobody.StatusCode, await nobody.ProblemCodeAsync()).ShouldBe((HttpStatusCode.BadRequest, "customer.unknown_kimlik_subscriber"));
        (user.StatusCode, organization.StatusCode).ShouldBe((HttpStatusCode.Created, HttpStatusCode.Created));
        (locked.StatusCode, await locked.ProblemCodeAsync()).ShouldBe((HttpStatusCode.Conflict, "customer.kimlik_link_locked"));
        relinked.StatusCode.ShouldBe(HttpStatusCode.OK, "the subscription has ended");
        (await relinked.ReadAsync<CustomerResponse>()).KimlikSubscriber.ShouldBe(other);
        (toNobody.StatusCode, await toNobody.ProblemCodeAsync()).ShouldBe((HttpStatusCode.BadRequest, "customer.unknown_kimlik_subscriber"));
        unverified.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    [Trait("Requirement", "CUS-3")]
    public async Task ACheckoutForANewCustomer_NamesAKimlikSubscriberThatExists()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);

        using var refused = await api.PostJsonAsync("/api/v1/checkout-sessions", new CreateCheckoutSessionRequest
        {
            Mode = CheckoutMode.Subscription,
            Items = [new CheckoutItemRequest { Price = "team-monthly-try" }],
            CustomerDetails = new CheckoutCustomerDetails { Locale = "en", KimlikSubscriber = new KimlikSubscriber(KimlikSubscriberType.User, Guid.CreateVersion7()) },
            SuccessUrl = "https://shop.example.com/done",
        });

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await refused.ReadAsync<System.Text.Json.Nodes.JsonNode>();
        problem["code"]!.GetValue<string>().ShouldBe("customer.unknown_kimlik_subscriber");
        problem.ToJsonString().ShouldContain("customerDetails.kimlikSubscriber");
    }

    private static Task<HttpResponseMessage> ProductAsync(HttpClient api, string key, string plan) =>
        api.PostJsonAsync("/api/v1/products", new CreateProductRequest { Key = key, Name = key, KimlikPlan = plan });

    private static Task<HttpResponseMessage> CustomerAsync(HttpClient api, string email, KimlikSubscriber subscriber) =>
        api.PostJsonAsync("/api/v1/customers", new CreateCustomerRequest { Name = "Ada Lovelace", Email = email, Locale = "en", KimlikSubscriber = subscriber });

    private int PlanReads() => _kimlik.Requests.Count(request => request.Path.StartsWith("/api/v1/plans", StringComparison.Ordinal));

    private WebApplicationFactory<Program> Host() =>
        IsolatedHosts.Create(_server, _server.NewDatabase("kimlik_references"), _time, _kimlik.Register, KimlikStub.Settings);
}
