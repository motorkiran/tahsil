using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Contracts;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;

namespace Tahsil.Server.Tests.Kimlik;

/// <summary>Kimlik's webhooks at <c>/webhooks/kimlik</c> (KML-21 to KML-24), from <see cref="KimlikStub"/>.</summary>
public sealed class KimlikWebhookTests : IDisposable
{
    private static readonly DateTimeOffset April = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly TahsilServerFixture _server;
    private readonly FakeTimeProvider _time;
    private readonly KimlikStub _kimlik;

    public KimlikWebhookTests(TahsilServerFixture server)
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
    [Trait("Requirement", "KML-23")]
    [Trait("Requirement", "KML-24")]
    public async Task ADeletedSubscriber_EndsItsCustomersSubscriptions_WithoutRefundOrInvoice_AndKeepsTheCustomer()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        var (customer, user, subscription) = await MirroredAsync(host, api);
        _time.Advance(TimeSpan.FromDays(10));
        var invoices = await CountAsync(host, "invoices");
        _kimlik.DeleteUser(user);

        using var deleted = await api.SendAsync(KimlikStub.Webhook("user.deleted", "user", user, "user", Guid.CreateVersion7().ToString(), _time.GetUtcNow()), CancellationToken);
        await host.RunWorkersUntilIdleAsync();
        using var again = await api.SendAsync(KimlikStub.Webhook("user.deleted", "user", user, "user", null, _time.GetUtcNow()), CancellationToken);
        await host.RunWorkersUntilIdleAsync();

        (deleted.StatusCode, again.StatusCode).ShouldBe((HttpStatusCode.NoContent, HttpStatusCode.NoContent));
        (await GetSubscriptionAsync(api, subscription)).Status.ShouldBe(SubscriptionStatus.Canceled);
        (await host.QueryAsync<string>($"SELECT cancellation_reason AS \"Value\" FROM tahsil.subscriptions WHERE id = {subscription}")).ShouldBe(["kimlik_subscriber_deleted"]);
        (await CountAsync(host, "refunds"), await CountAsync(host, "invoices")).ShouldBe((0L, invoices));
        var kept = await GetAsync<CustomerResponse>(api, $"/api/v1/customers/{customer}");
        (kept.Email, kept.KimlikSubscriber).ShouldBe(($"{user:N}@example.com", null));
        (await host.QueryAsync<long>($"SELECT count(*) AS \"Value\" FROM tahsil.audit_events WHERE action = 'subscription.canceled'")).ShouldBe([1L], "the second delivery changes nothing");
        _kimlik.Subscriptions.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Requirement", "KML-21")]
    public async Task AChangeMadeInKimlik_IsUndone_WhileTahsilsOwnChangesAreLeftAlone()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        var (_, _, subscription) = await MirroredAsync(host, api);
        var mirrored = _kimlik.Subscriptions.ShouldHaveSingleItem();
        mirrored.Plan = "pro";
        mirrored.Status = "canceled";
        mirrored.CurrentPeriodEnd = April.AddDays(20);

        using var drifted = await api.SendAsync(
            KimlikStub.Webhook("subscription.updated", "subscription", mirrored.Id, "user", Guid.CreateVersion7().ToString(), _time.GetUtcNow()), CancellationToken);
        await host.RunWorkersUntilIdleAsync();
        var requests = _kimlik.Requests.Count;
        using var own = await api.SendAsync(KimlikStub.Webhook("subscription.updated", "subscription", mirrored.Id, "client", KimlikStub.ClientId, _time.GetUtcNow()), CancellationToken);
        await host.RunWorkersUntilIdleAsync();

        (drifted.StatusCode, own.StatusCode).ShouldBe((HttpStatusCode.NoContent, HttpStatusCode.NoContent));
        (mirrored.Plan, mirrored.Status, mirrored.CurrentPeriodEnd).ShouldBe(("team", "active", April.AddMonths(1).AddDays(1)));
        (await host.QueryAsync<string>($"SELECT subject_id AS \"Value\" FROM tahsil.audit_events WHERE action = 'kimlik.drift_corrected'")).ShouldBe([subscription.ToString()]);
        _kimlik.Requests.Count.ShouldBe(requests, "Tahsil's own change needs no sync");
    }

    [Fact]
    [Trait("Requirement", "KML-22")]
    [Trait("Requirement", "KML-24")]
    public async Task OnlyRecentWebhooksSignedWithTheEndpointsSecret_AreAccepted()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        var user = Guid.CreateVersion7();
        var now = _time.GetUtcNow();

        using var otherSecret = await api.SendAsync(
            KimlikStub.Webhook("user.deleted", "user", user, "user", null, now, secret: "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))), CancellationToken);
        using var stale = await api.SendAsync(KimlikStub.Webhook("user.deleted", "user", user, "user", null, now.AddMinutes(-6)), CancellationToken);
        using var tampered = await api.SendAsync(KimlikStub.Webhook("user.deleted", "user", user, "user", null, now, tamperedBody: "{\"type\":\"organization.deleted\"}"), CancellationToken);
        using var unknown = await api.SendAsync(KimlikStub.Webhook("user.mfa_enabled", "user", user, "user", null, now), CancellationToken);
        using var recent = await api.SendAsync(KimlikStub.Webhook("user.deleted", "user", user, "user", null, now.AddMinutes(-4)), CancellationToken);
        await using var disabledHost = IsolatedHosts.Create(_server, _server.NewDatabase("kimlik_webhooks_off"), _time);
        using var disabled = disabledHost.CreateClient();
        using var off = await disabled.SendAsync(KimlikStub.Webhook("user.deleted", "user", user, "user", null, now), CancellationToken);

        (otherSecret.StatusCode, stale.StatusCode, tampered.StatusCode).ShouldBe((HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized));
        (unknown.StatusCode, recent.StatusCode).ShouldBe((HttpStatusCode.NoContent, HttpStatusCode.NoContent));
        (await host.QueryAsync<long>($"SELECT count(*) AS \"Value\" FROM tahsil.outbox_messages WHERE type = 'kimlik.event_received'")).ShouldBe([1L], "unknown types are acknowledged and ignored");
        off.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>A customer linked to a Kimlik user, with a monthly Team subscription mirrored to Kimlik.</summary>
    private async Task<(Guid Customer, Guid User, Guid Subscription)> MirroredAsync(WebApplicationFactory<Program> host, HttpClient api)
    {
        using (var product = await api.PatchJsonAsync("/api/v1/products/team", new UpdateProductRequest { KimlikPlan = "team" }))
        {
            product.EnsureSuccessStatusCode();
        }

        var user = _kimlik.AddUser();
        using var created = await api.PostJsonAsync("/api/v1/customers", new CreateCustomerRequest
        {
            Name = "Ada Lovelace",
            Email = $"{user:N}@example.com",
            Locale = "en",
            KimlikSubscriber = new KimlikSubscriber(KimlikSubscriberType.User, user),
        });
        var customer = (await created.ReadAsync<CustomerResponse>()).Id;
        await SaveCardAsync(api, customer, "4242424242424242");
        var subscription = await CreateAsync(api, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try" }] });
        await host.RunWorkersUntilIdleAsync();
        _kimlik.Subscriptions.ShouldHaveSingleItem();
        return (customer, user, subscription.Id);
    }

    private static async Task<long> CountAsync(WebApplicationFactory<Program> host, string table) =>
        (await host.QueryAsync<long>(System.Runtime.CompilerServices.FormattableStringFactory.Create($"SELECT count(*) AS \"Value\" FROM tahsil.{table}"))).Single();

    private WebApplicationFactory<Program> Host() =>
        IsolatedHosts.Create(_server, _server.NewDatabase("kimlik_webhooks"), _time, services =>
        {
            services.AddSingleton<TimeProvider>(_time);
            _kimlik.Register(services);
        }, KimlikStub.Settings);
}
