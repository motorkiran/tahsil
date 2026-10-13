using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using Tahsil.Infrastructure.Persistence;
using static Tahsil.Server.Tests.Checkout.CheckoutBrowser;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;

namespace Tahsil.Server.Tests.Kimlik;

/// <summary>
/// The M7 exit scenario against <see cref="KimlikStub"/>, as the Notes sample runs it against Kimlik: a customer linked
/// to a Kimlik user buys Pro (Kimlik plan <c>pro</c>, monthly ₺500.00) through the hosted checkout on April 1, 2026, and
/// Kimlik gives the plan; a cancellation at the period's end, and a renewal that keeps failing, take it away exactly
/// when Tahsil ends the subscription. Renewals run on May 1 at midnight and their first payment at 01:00.
/// </summary>
[Trait("Suite", "Exit")]
public sealed class KimlikExitTests : IDisposable
{
    private static readonly DateTimeOffset April = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset May = April.AddMonths(1);
    private static readonly string[] Documents = ["pre-information", "distance-sales"];

    private readonly TahsilServerFixture _server;
    private readonly FakeTimeProvider _time;
    private readonly KimlikStub _kimlik;

    public KimlikExitTests(TahsilServerFixture server)
    {
        _server = server;
        _time = new FakeTimeProvider(April);
        _kimlik = new KimlikStub(_time);
        _kimlik.AddPlan("free");
        _kimlik.AddPlan("pro");
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose() => _kimlik.Dispose();

    [Fact]
    [Trait("Requirement", "KML-13")]
    [Trait("Requirement", "KML-17")]
    [Trait("Requirement", "KML-26")]
    public async Task APlanBoughtAtCheckout_IsGivenInKimlik_AndEndsWithTheCancelledPeriod()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        await ProPlanAsync(api);
        var (customer, user) = await LinkedCustomerAsync(api);

        using var created = await api.PostJsonAsync("/api/v1/checkout-sessions", new CreateCheckoutSessionRequest
        {
            Mode = CheckoutMode.Subscription,
            Customer = customer,
            Items = [new CheckoutItemRequest { Price = "pro-monthly-try" }],
            Subscription = new CheckoutSubscriptionData { TrialDays = 0 },
            SuccessUrl = "https://notes.example.com/?upgraded",
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(CancellationToken));
        var session = await created.ReadAsync<CheckoutSessionResponse>();
        using var browser = Open(host);
        var form = Form("ada@example.com", "Ada Lovelace", Documents);
        form["Form.CardStorage"] = "true";
        using (var paid = await PayOnTheEmbedAsync(host, browser, await SubmitAsync(browser, session.Url!, form), "4242424242424242"))
        {
            paid.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await host.RunWorkersUntilIdleAsync();
        var subscription = await QueryAsync(host, context => context.CheckoutSessions.Where(row => row.Id == session.Id).Select(row => row.SubscriptionId!.Value).SingleAsync(CancellationToken));
        var given = _kimlik.Subscriptions.ShouldHaveSingleItem();
        var (status, end) = (given.Status, given.CurrentPeriodEnd);

        _time.Advance(TimeSpan.FromDays(10));
        using (var cancel = await api.PostJsonAsync($"/api/v1/subscriptions/{subscription}/cancel", new CancelSubscriptionRequest { Reason = CancellationReason.RequestedByCustomer }))
        {
            cancel.EnsureSuccessStatusCode();
        }

        await host.RunWorkersUntilIdleAsync();
        await AdvanceToAsync(host, May.AddMinutes(-1));
        var lastMinute = given.IsInEffectAt(_time.GetUtcNow());
        await AdvanceToAsync(host, May);

        (given.SubscriberId, given.Plan, given.ExternalReference).ShouldBe((user, "pro", subscription.ToString()));
        (status, end).ShouldBe(("active", May.AddDays(1)), "a renewal that runs late keeps access for a day");
        lastMinute.ShouldBeTrue("paid until the period's end");
        (await GetSubscriptionAsync(api, subscription)).Status.ShouldBe(SubscriptionStatus.Canceled);
        (given.CurrentPeriodEnd, given.IsInEffectAt(_time.GetUtcNow())).ShouldBe((May, false));
        _kimlik.Subscriptions.ShouldHaveSingleItem("the plan ends; nothing new is created");
    }

    [Fact]
    [Trait("Requirement", "KML-13")]
    [Trait("Requirement", "KML-15")]
    [Trait("Requirement", "KML-18")]
    public async Task AFailedRenewal_KeepsThePlanUntilDunningsFinalAction_ThenEndsItThere()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        await ProPlanAsync(api);
        var (customer, _) = await LinkedCustomerAsync(api);
        var card = await SaveCardAsync(api, customer, "4242424242424242");
        var subscription = await CreateAsync(api, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "pro-monthly-try" }], TrialDays = 0 });
        await host.RunWorkersUntilIdleAsync();
        var given = _kimlik.Subscriptions.ShouldHaveSingleItem();
        await SetBehaviorAsync(api, card, "insufficient_funds");

        // The renewal at midnight, its first payment at 01:00, and the retries a day, three, five and seven days later.
        var firstFailure = May.AddHours(1);
        await AdvanceToAsync(host, May);
        await AdvanceToAsync(host, firstFailure);
        var pastDue = await GetSubscriptionAsync(api, subscription.Id);
        var duringDunning = given.CurrentPeriodEnd;
        foreach (var day in new[] { 1, 3, 5 })
        {
            await AdvanceToAsync(host, firstFailure.AddDays(day));
        }

        var beforeFinalAction = given.IsInEffectAt(_time.GetUtcNow());
        await AdvanceToAsync(host, firstFailure.AddDays(7));

        pastDue.Status.ShouldBe(SubscriptionStatus.PastDue);
        duringDunning.ShouldBe(May.AddMonths(1).AddDays(1), "past due keeps the plan: the renewal moved the period on, and dunning ends it sooner");
        beforeFinalAction.ShouldBeTrue();
        var ended = await GetSubscriptionAsync(api, subscription.Id);
        (ended.Status, ended.EndedAt).ShouldBe((SubscriptionStatus.Canceled, firstFailure.AddDays(7)));
        (given.Status, given.CurrentPeriodEnd).ShouldBe(("expired", firstFailure.AddDays(7)), "Kimlik's access ends with the subscription, not with the buffer");
    }

    private async Task<(Guid Customer, Guid KimlikUser)> LinkedCustomerAsync(HttpClient api)
    {
        var user = _kimlik.AddUser();
        using var created = await api.PutAsJsonAsync(new Uri($"/api/v1/customers/by-external-id/{user}", UriKind.Relative), new UpdateCustomerRequest
        {
            Email = "ada@example.com",
            Name = "Ada Lovelace",
            KimlikSubscriber = new KimlikSubscriber(KimlikSubscriberType.User, user),
        }, ApiClients.Json, CancellationToken);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(CancellationToken));
        return ((await created.ReadAsync<CustomerResponse>()).Id, user);
    }

    private static async Task ProPlanAsync(HttpClient api)
    {
        using var updated = await api.PatchJsonAsync("/api/v1/products/pro", new UpdateProductRequest { KimlikPlan = "pro" });
        updated.StatusCode.ShouldBe(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync(CancellationToken));
    }

    private async Task AdvanceToAsync(WebApplicationFactory<Program> host, DateTimeOffset instant)
    {
        _time.Advance(instant - _time.GetUtcNow());
        await host.RunWorkersUntilIdleAsync();
    }

    private static async Task<T> QueryAsync<T>(WebApplicationFactory<Program> host, Func<TahsilDbContext, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TahsilDbContext>());
    }

    private WebApplicationFactory<Program> Host() =>
        IsolatedHosts.Create(_server, _server.NewDatabase("kimlik_exit"), _time, services =>
        {
            services.AddSingleton<TimeProvider>(_time);
            _kimlik.Register(services);
        }, KimlikStub.Settings);
}
