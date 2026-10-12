using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Application.Kimlik;
using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;

namespace Tahsil.Server.Tests.Kimlik;

/// <summary>Tahsil's client of Kimlik's Management API (KML-11), against <see cref="KimlikStub"/>.</summary>
public sealed class KimlikClientTests : IDisposable
{
    private static readonly DateTimeOffset April = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly TahsilServerFixture _server;
    private readonly FakeTimeProvider _time;
    private readonly KimlikStub _kimlik;

    public KimlikClientTests(TahsilServerFixture server)
    {
        _server = server;
        _time = new FakeTimeProvider(April);
        _kimlik = new KimlikStub(_time);
        _kimlik.AddPlan("pro");
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose() => _kimlik.Dispose();

    [Fact]
    [Trait("Requirement", "KML-11")]
    public async Task AToken_IsReusedUntilAMinuteBeforeItExpires_AndRenewedOnceForConcurrentCalls()
    {
        await using var host = Host();
        var client = Client(host);

        await Plans(client);
        await Plans(client);
        _time.Advance(TimeSpan.FromMinutes(9) - TimeSpan.FromSeconds(1));
        await Plans(client);
        var beforeRenewal = _kimlik.TokensIssued;
        _time.Advance(TimeSpan.FromSeconds(1));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Plans(client)));

        beforeRenewal.ShouldBe(1);
        _kimlik.TokensIssued.ShouldBe(2, "one renewal for every caller, a minute before the ten-minute token expires");
    }

    [Fact]
    [Trait("Requirement", "KML-11")]
    public async Task ARefusedToken_IsRenewedAndTheRequestSentOnceMore_AndRefusedCredentialsAreAnError()
    {
        await using var host = Host();
        var client = Client(host);
        await Plans(client);

        _kimlik.RevokeTokens();
        var retried = await client.ListPlansAsync(CancellationToken);
        _kimlik.RevokeTokens();
        _kimlik.AcceptsCredentials = false;
        var refused = await client.ListPlansAsync(CancellationToken);

        retried.IsSuccess.ShouldBeTrue();
        _kimlik.Requests.Count(request => request.Path.StartsWith("/api/v1/plans", StringComparison.Ordinal)).ShouldBe(4, "the first call, the refused one, its retry and the last refused one");
        refused.Error.ShouldBe(KimlikErrors.Unauthorized);
    }

    [Fact]
    [Trait("Requirement", "KML-11")]
    public async Task KimliksErrors_KeepTheirCodes_AndOutagesAreUnavailable()
    {
        await using var host = Host();
        var client = Client(host);
        var user = new KimlikSubscriber(KimlikSubscriberType.User, _kimlik.AddUser());
        var draft = new KimlikSubscriptionDraft(user, "pro", null, April.AddMonths(1), "sub_1");

        var created = await client.CreateSubscriptionAsync(draft, CancellationToken);
        var again = await client.CreateSubscriptionAsync(draft, CancellationToken);
        var missing = await client.GetSubscriptionAsync(Guid.CreateVersion7(), CancellationToken);
        _kimlik.Fail(HttpStatusCode.ServiceUnavailable);
        var unavailable = await client.GetSubscriptionAsync(created.Value.Id, CancellationToken);
        var found = await client.GetSubscriptionAsync(created.Value.Id, CancellationToken);

        (created.Value.Status, created.Value.ExternalReference, created.Value.CurrentPeriodEnd).ShouldBe((KimlikSubscriptionStatus.Active, "sub_1", April.AddMonths(1)));
        (again.Error!.Type, again.Error.Code).ShouldBe((ErrorType.Conflict, "subscription.exists"));
        (missing.Error!.Type, missing.Error.Code).ShouldBe((ErrorType.NotFound, "subscription.not_found"));
        unavailable.Error.ShouldBe(KimlikErrors.Unavailable);
        found.Value.SubscriberId.ShouldBe(user.Id);
        (await client.ListSubscriptionsAsync(user, CancellationToken)).Value.ShouldHaveSingleItem().Id.ShouldBe(created.Value.Id);
    }

    [Fact]
    [Trait("Requirement", "KML-11")]
    public async Task Requests_SendInstantsInUtc_AndChangesOnlyWhatChanges()
    {
        await using var host = Host();
        var client = Client(host);
        var istanbul = TimeSpan.FromHours(3);
        var user = new KimlikSubscriber(KimlikSubscriberType.User, _kimlik.AddUser());

        var trial = await client.CreateSubscriptionAsync(new KimlikSubscriptionDraft(user, "pro", April.AddDays(14).ToOffset(istanbul), null, "sub_1"), CancellationToken);
        var converted = await client.UpdateSubscriptionAsync(
            trial.Value.Id, new KimlikSubscriptionChange { Status = KimlikSubscriptionStatus.Active, CurrentPeriodEnd = April.AddMonths(1).ToOffset(istanbul) }, CancellationToken);
        var canceled = await client.CancelSubscriptionAsync(trial.Value.Id, CancellationToken);

        trial.Value.Status.ShouldBe(KimlikSubscriptionStatus.Trialing);
        _kimlik.Requests[0].Json!["trialEndsAt"]!.GetValue<string>().ShouldBe("2026-04-15T00:00:00Z");
        _kimlik.Requests[0].Json!.ContainsKey("currentPeriodEnd").ShouldBeFalse();
        _kimlik.Requests[1].Json!.Select(pair => pair.Key).ShouldBe(["status", "currentPeriodEnd"], ignoreOrder: true);
        _kimlik.Requests[1].Json!["currentPeriodEnd"]!.GetValue<string>().ShouldBe("2026-05-01T00:00:00Z");
        (converted.Value.Status, converted.Value.CurrentPeriodEnd).ShouldBe((KimlikSubscriptionStatus.Active, April.AddMonths(1)));
        (canceled.Value.Status, canceled.Value.IsInEffectAt(_time.GetUtcNow())).ShouldBe((KimlikSubscriptionStatus.Canceled, true));
    }

    [Fact]
    [Trait("Requirement", "KML-11")]
    public void TheConnector_NeedsItsClientAndWebhookSecret_WhenEnabled()
    {
        static KimlikOptions Valid() => new() { Enabled = true, Authority = KimlikStub.Authority, ClientId = "tahsil", ClientSecret = "secret", WebhookSecret = KimlikStub.WebhookSecret };

        Valid().IsValid().ShouldBeTrue();
        new KimlikOptions().IsValid().ShouldBeTrue("a disabled connector needs nothing");
        Changed(options => options.Authority = new Uri("https://kimlik.test/tenant")).ShouldBeFalse("without a trailing slash, relative paths would drop the last segment");
        Changed(options => options.WebhookSecret = "secret").ShouldBeFalse();
        Changed(options => options.ClientSecret = null).ShouldBeFalse();
        Changed(options => options.PastDueAccess = "never").ShouldBeFalse();
        Changed(options => options.SyncBuffer = TimeSpan.FromDays(8)).ShouldBeFalse();

        static bool Changed(Action<KimlikOptions> change)
        {
            var options = Valid();
            change(options);
            return options.IsValid();
        }
    }

    private static async Task Plans(IKimlikClient client) => (await client.ListPlansAsync(CancellationToken)).Value.ShouldContain(plan => plan.Key == "pro");

    private static IKimlikClient Client(WebApplicationFactory<Program> host) => host.Services.CreateScope().ServiceProvider.GetRequiredService<IKimlikClient>();

    private WebApplicationFactory<Program> Host() =>
        IsolatedHosts.Create(_server, _server.NewDatabase("kimlik_client"), _time, services =>
        {
            services.AddSingleton<TimeProvider>(_time);
            _kimlik.Register(services);
        }, KimlikStub.Settings);
}
