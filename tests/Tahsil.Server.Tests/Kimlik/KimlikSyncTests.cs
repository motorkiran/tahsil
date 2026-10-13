using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Application.Kimlik;
using Tahsil.Contracts;
using Tahsil.Domain.Common;
using Tahsil.Infrastructure.Kimlik;
using Tahsil.Infrastructure.Persistence;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;

namespace Tahsil.Server.Tests.Kimlik;

/// <summary>
/// Subscriptions mirrored to Kimlik (KML-16 to KML-20, SUB-2), against <see cref="KimlikStub"/>: monthly Team (Kimlik
/// plan <c>team</c>) from April 1, 2026, renewing May 1, and Pro (plan <c>pro</c>) with a 14-day trial, with the
/// default buffer of a day.
/// </summary>
public sealed class KimlikSyncTests : IDisposable
{
    private static readonly DateTimeOffset April = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset May = April.AddMonths(1);

    private readonly TahsilServerFixture _server;
    private readonly FakeTimeProvider _time;
    private readonly KimlikStub _kimlik;

    public KimlikSyncTests(TahsilServerFixture server)
    {
        _server = server;
        _time = new FakeTimeProvider(April);
        _kimlik = new KimlikStub(_time);
        _kimlik.AddPlan("team");
        _kimlik.AddPlan("pro");
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose() => _kimlik.Dispose();

    [Fact]
    [Trait("Requirement", "KML-13")]
    [Trait("Requirement", "KML-17")]
    public async Task ASubscription_IsMirroredWithTheBuffer_CanceledAtThePeriodsEnd_AndResumed()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        await PlansAsync(api);
        var (customer, user) = await LinkedCustomerAsync(api);

        var subscription = await SubscribeAsync(host, api, customer, "team-monthly-try");
        var created = _kimlik.Subscriptions.ShouldHaveSingleItem();
        var mirror = await MirrorAsync(host, subscription.Id);
        using (var cancel = await api.PostJsonAsync($"/api/v1/subscriptions/{subscription.Id}/cancel", new CancelSubscriptionRequest()))
        {
            cancel.EnsureSuccessStatusCode();
        }

        await host.RunWorkersUntilIdleAsync();
        var (canceledStatus, canceledEnd) = (created.Status, created.CurrentPeriodEnd);
        using (var resume = await api.PostPathAsync($"/api/v1/subscriptions/{subscription.Id}/resume"))
        {
            resume.EnsureSuccessStatusCode();
        }

        await host.RunWorkersUntilIdleAsync();

        (created.SubscriberId, created.Plan, created.ExternalReference).ShouldBe((user, "team", subscription.Id.ToString()));
        (mirror.KimlikSubscriptionId, mirror.Error).ShouldBe((created.Id, null));
        (canceledStatus, canceledEnd).ShouldBe(("canceled", May), "a cancellation at the period's end has no buffer");
        (created.Status, created.CurrentPeriodEnd).ShouldBe(("active", May.AddDays(1)));
        _kimlik.Requests.Where(request => request.Method != "GET").Select(request => $"{request.Method} {request.Json?.ToJsonString()}").ShouldBe(
        [
            $"POST {{\"subscriberType\":\"user\",\"subscriberId\":\"{user}\",\"plan\":\"team\",\"externalReference\":\"{subscription.Id}\",\"currentPeriodEnd\":\"2026-05-02T00:00:00Z\"}}",
            "PATCH {\"currentPeriodEnd\":\"2026-05-01T00:00:00Z\"}",
            "POST ",
            "PATCH {\"status\":\"active\",\"currentPeriodEnd\":\"2026-05-02T00:00:00Z\"}",
        ]);
    }

    [Fact]
    [Trait("Requirement", "KML-13")]
    [Trait("Requirement", "KML-17")]
    public async Task ATrial_ConvertsInKimlik_InOneRequest_WhenItsFirstPeriodIsPaid()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        await PlansAsync(api);
        var (customer, _) = await LinkedCustomerAsync(api);

        var subscription = await SubscribeAsync(host, api, customer, "pro-monthly-try");
        var trial = _kimlik.Subscriptions.ShouldHaveSingleItem();
        var (trialStatus, trialEnd) = (trial.Status, trial.TrialEndsAt);
        foreach (var step in new[] { TimeSpan.FromDays(14), TimeSpan.FromHours(1) })
        {
            // The trial ends, and its first period is charged an hour later.
            _time.Advance(step);
            await host.RunWorkersUntilIdleAsync();
        }

        (await GetSubscriptionAsync(api, subscription.Id)).Status.ShouldBe(SubscriptionStatus.Active);
        (trialStatus, trialEnd).ShouldBe(("trialing", April.AddDays(15)));
        (trial.Status, trial.CurrentPeriodEnd).ShouldBe(("active", April.AddDays(14).AddMonths(1).AddDays(1)));
        _kimlik.Requests.Last(request => request.Method == "PATCH").Json!.Select(pair => pair.Key).ShouldBe(["status", "currentPeriodEnd"], ignoreOrder: true);
    }

    [Fact]
    [Trait("Requirement", "KML-17")]
    [Trait("Requirement", "KML-18")]
    [Trait("Requirement", "SUB-2")]
    public async Task CancelingNow_EndsItInKimlikAtOnce_AndTheNextSubscriptionIsCreatedAnew()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        await PlansAsync(api);
        var (customer, _) = await LinkedCustomerAsync(api);
        var first = await SubscribeAsync(host, api, customer, "team-monthly-try");
        using var second = await api.PostJsonAsync("/api/v1/subscriptions", new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "pro-monthly-try" }] });
        using var both = await api.PatchJsonAsync(
            $"/api/v1/subscriptions/{first.Id}", new UpdateSubscriptionRequest { Items = [new() { Price = "team-monthly-try" }, new() { Price = "pro-monthly-try" }] });
        _time.Advance(TimeSpan.FromDays(10));

        using (var cancel = await api.PostJsonAsync($"/api/v1/subscriptions/{first.Id}/cancel", new CancelSubscriptionRequest { At = "now" }))
        {
            cancel.EnsureSuccessStatusCode();
        }

        await host.RunWorkersUntilIdleAsync();
        var ended = _kimlik.Subscriptions.ShouldHaveSingleItem();
        var next = await SubscribeAsync(host, api, customer, "pro-monthly-try");

        (second.StatusCode, await second.ProblemCodeAsync()).ShouldBe((HttpStatusCode.Conflict, "subscription.kimlik_plan_conflict"));
        (both.StatusCode, await both.ProblemCodeAsync()).ShouldBe((HttpStatusCode.Conflict, "subscription.kimlik_plan_conflict"));
        (ended.Status, ended.CurrentPeriodEnd).ShouldBe(("expired", April.AddDays(10)));
        (await MirrorAsync(host, first.Id)).KimlikSubscriptionId.ShouldBeNull();
        _kimlik.Subscriptions.Count.ShouldBe(2);
        (await MirrorAsync(host, next.Id)).KimlikSubscriptionId.ShouldBe(_kimlik.Subscriptions[1].Id);
    }

    [Fact]
    [Trait("Requirement", "KML-17")]
    public async Task AKimlikSubscriptionMadeElsewhere_IsAdopted_OrReportedAsAConflict()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        await PlansAsync(api);
        var (adopter, adopterUser) = await LinkedCustomerAsync(api);
        var (other, otherUser) = await LinkedCustomerAsync(api);
        var free = _kimlik.Seed(adopterUser, organization: false, "team", null);
        var managed = _kimlik.Seed(otherUser, organization: false, "team", May, "sub_1Nv0FGQ9RKHgCVdK");

        var adopted = await SubscribeAsync(host, api, adopter, "team-monthly-try");
        var conflicted = await SubscribeAsync(host, api, other, "team-monthly-try");

        (await MirrorAsync(host, adopted.Id)).KimlikSubscriptionId.ShouldBe(free.Id);
        (free.ExternalReference, free.CurrentPeriodEnd).ShouldBe((adopted.Id.ToString(), May.AddDays(1)));
        var conflict = await MirrorAsync(host, conflicted.Id);
        (conflict.KimlikSubscriptionId, conflict.Error).ShouldBe((null, KimlikSynchronizer.ManagedElsewhere));
        managed.ExternalReference.ShouldBe("sub_1Nv0FGQ9RKHgCVdK");
        (await AttentionAsync(host)).ShouldBe([(AttentionKind.KimlikConflict, conflicted.Id.ToString())]);
    }

    [Fact]
    [Trait("Requirement", "KML-19")]
    [Trait("Requirement", "KML-20")]
    public async Task FailedSyncs_AreRetried_ReportedAfterADay_AndReconciliationRestoresWhatKimlikLost()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        await PlansAsync(api);
        var (customer, _) = await LinkedCustomerAsync(api);
        _kimlik.Down = true;

        var subscription = await SubscribeAsync(host, api, customer, "team-monthly-try");
        var failing = await MirrorAsync(host, subscription.Id);
        var attentionEarly = await AttentionAsync(host);
        for (var round = 0; round < 5; round++)
        {
            _time.Advance(TimeSpan.FromHours(6));
            await ReconcileAsync(host);
        }

        var attentionLate = await AttentionAsync(host);
        _kimlik.Down = false;
        await ReconcileAsync(host);
        var restored = _kimlik.Subscriptions.ShouldHaveSingleItem();
        restored.Status = "expired";
        await ReconcileAsync(host);

        (failing.KimlikSubscriptionId, failing.Error, failing.FailingSince).ShouldBe((null, KimlikErrors.Unavailable.Message, April));
        attentionEarly.ShouldBeEmpty();
        attentionLate.ShouldBe([(AttentionKind.KimlikSyncFailing, subscription.Id.ToString())]);
        _kimlik.Subscriptions.Count.ShouldBe(2, "an expired Kimlik subscription is created anew");
        var mirror = await MirrorAsync(host, subscription.Id);
        (mirror.KimlikSubscriptionId, mirror.Error, mirror.FailingSince).ShouldBe((_kimlik.Subscriptions[1].Id, null, null));
        _kimlik.Subscriptions[1].ExternalReference.ShouldBe(subscription.Id.ToString());
    }

    [Fact]
    [Trait("Requirement", "KML-16")]
    public async Task ASubscriptionWithoutAKimlikLinkOrPlan_NeverReachesKimlik()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        await PlansAsync(api);
        using var created = await api.PostJsonAsync("/api/v1/customers", new CreateCustomerRequest { Name = "Grace Hopper", Email = "grace@example.com", Locale = "en" });
        var unlinked = (await created.ReadAsync<CustomerResponse>()).Id;
        await SaveCardAsync(api, unlinked, "4242424242424242");
        var (linked, _) = await LinkedCustomerAsync(api);

        await SubscribeAsync(host, api, unlinked, "team-monthly-try");
        await SubscribeAsync(host, api, linked, "growth-monthly-try");

        _kimlik.Requests.ShouldBeEmpty();
        (await host.QueryAsync<long>($"SELECT count(*) AS \"Value\" FROM tahsil.kimlik_mirrors")).ShouldBe([0L]);
    }

    private async Task<(Guid Customer, Guid KimlikUser)> LinkedCustomerAsync(HttpClient api)
    {
        var user = _kimlik.AddUser();
        using var created = await api.PostJsonAsync("/api/v1/customers", new CreateCustomerRequest
        {
            Name = "Ada Lovelace",
            Email = $"{user:N}@example.com",
            Locale = "en",
            KimlikSubscriber = new KimlikSubscriber(KimlikSubscriberType.User, user),
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(CancellationToken));
        var customer = (await created.ReadAsync<CustomerResponse>()).Id;
        await SaveCardAsync(api, customer, "4242424242424242");
        _kimlik.Requests.Clear();
        return (customer, user);
    }

    private static async Task<SubscriptionResponse> SubscribeAsync(WebApplicationFactory<Program> host, HttpClient api, Guid customer, string price)
    {
        var subscription = await CreateAsync(api, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = price }] });
        await host.RunWorkersUntilIdleAsync();
        return subscription;
    }

    private static async Task<Domain.Subscriptions.KimlikMirror> MirrorAsync(WebApplicationFactory<Program> host, Guid subscription)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TahsilDbContext>().KimlikMirrors.AsNoTracking()
            .SingleAsync(mirror => mirror.SubscriptionId == subscription, CancellationToken);
    }

    private static async Task<List<(AttentionKind Kind, string SubjectId)>> AttentionAsync(WebApplicationFactory<Program> host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var items = await scope.ServiceProvider.GetRequiredService<TahsilDbContext>().AttentionItems.AsNoTracking()
            .Where(item => item.Kind == AttentionKind.KimlikConflict || item.Kind == AttentionKind.KimlikSyncFailing)
            .ToListAsync(CancellationToken);
        return [.. items.Select(item => (item.Kind, item.SubjectId.ToString()))];
    }

    private static async Task ReconcileAsync(WebApplicationFactory<Program> host)
    {
        (await host.Services.GetServices<IHostedService>().OfType<KimlikReconciliation>().Single().RunOnceAsync(CancellationToken)).ShouldBeTrue();
        await host.RunWorkersUntilIdleAsync();
    }

    private static async Task PlansAsync(HttpClient api)
    {
        foreach (var (product, plan) in new[] { ("team", "team"), ("pro", "pro") })
        {
            using var updated = await api.PatchJsonAsync($"/api/v1/products/{product}", new UpdateProductRequest { KimlikPlan = plan });
            updated.StatusCode.ShouldBe(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync(CancellationToken));
        }
    }

    private WebApplicationFactory<Program> Host() =>
        IsolatedHosts.Create(_server, _server.NewDatabase("kimlik_sync"), _time, services =>
        {
            services.AddSingleton<TimeProvider>(_time);
            _kimlik.Register(services);
        }, KimlikStub.Settings);
}
