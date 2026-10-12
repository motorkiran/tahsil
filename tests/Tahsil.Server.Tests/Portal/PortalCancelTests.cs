using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Application.Notifications;
using Tahsil.Contracts;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Server.Tests.Notifications;
using static Tahsil.Server.Tests.Checkout.CheckoutBrowser;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;
using ConsentType = Tahsil.Domain.Customers.ConsentType;

namespace Tahsil.Server.Tests.Portal;

/// <summary>
/// Cancellation, withdrawal and billing details in the portal (PRT §4, §5), with the simulator: a monthly Team
/// subscription of ₺300.00 from April 1, 2026, ended on April 16, halfway through its 30 days.
/// </summary>
public sealed class PortalCancelTests(TahsilServerFixture server)
{
    private static readonly DateTimeOffset April = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(April);
    private readonly CapturingEmailSender _email = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "PRT-8")]
    [Trait("Requirement", "PRT-9")]
    [Trait("Requirement", "SUB-35")]
    public async Task AnIndividual_ChoosesThePeriodsEnd_IsToldAtOnce_AndCanKeepTheSubscription()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        var (customer, subscription) = await SubscriberAsync(api, "ada@example.com");
        _time.Advance(TimeSpan.FromDays(15));
        using var browser = Open(host);
        var (session, cancel) = await CancelUrlAsync(api, customer, subscription.Id);

        var page = await ParseAsync(await browser.GetAsync(cancel, CancellationToken));
        using var canceled = await PostHandlerAsync(browser, cancel, string.Empty, ("Choice", "PeriodEnd"), ("Feedback", "TooExpensive"));
        await host.RunWorkersUntilIdleAsync();
        var scheduled = await GetSubscriptionAsync(api, subscription.Id);
        var overview = await ParseAsync(await browser.GetAsync(new Uri($"{session.Url}?done=overview"), CancellationToken));
        using var resumed = await PostHandlerAsync(browser, new Uri($"{session.Url}?done=overview"), "Resume", ("subscription", subscription.Id.ToString()));

        page.QuerySelectorAll("#choices .choice").Select(choice => choice.TextContent.Trim()).ShouldBe(
            ["At the end of the period (May 1, 2026)", "Now, with a refund of ₺150.00 for the unused time"]);
        page.QuerySelector("#feedback").ShouldNotBeNull("one optional question");
        (canceled.StatusCode, canceled.Headers.Location!.OriginalString).ShouldBe((HttpStatusCode.Redirect, $"{session.Url.AbsolutePath}?done=cancel_scheduled"));
        (scheduled.Status, scheduled.CancelAt, scheduled.CancellationReason, scheduled.CancellationFeedback).ShouldBe(
            (SubscriptionStatus.Active, April.AddMonths(1), CancellationReason.RequestedByCustomer, CancellationFeedback.TooExpensive));
        var told = _email.Sent.Single(message => message.Subject == "Your subscription ends on May 1, 2026");
        told.Text.ShouldContain("/portal/ps_");
        var consent = await QueryAsync(host, context => context.ConsentRecords.SingleAsync(record => record.Type == ConsentType.Cancellation, CancellationToken));
        (consent.TextShown, consent.Context["choice"]).ShouldBe(("At the end of the period (May 1, 2026)", "PeriodEnd"));
        overview.QuerySelector("#subscriptions .ends")!.TextContent.ShouldBe("Ends on May 1, 2026.");
        resumed.Headers.Location!.OriginalString.ShouldEndWith("?done=resumed");
        (await GetSubscriptionAsync(api, subscription.Id)).CancelAt.ShouldBeNull();
    }

    [Fact]
    [Trait("Requirement", "PRT-8")]
    [Trait("Requirement", "SUB-31")]
    public async Task CancellingNow_RefundsTheUnusedTime_AndCompaniesGetThePeriodsEndOnly()
    {
        await using var host = Host();
        using var api = await ClientAsync(host);
        var (customer, subscription) = await SubscriberAsync(api, "ada@example.com");
        var (company, theirs) = await SubscriberAsync(api, "billing@acme.example", CustomerType.Company);
        _time.Advance(TimeSpan.FromDays(15));
        using var browser = Open(host);
        var (_, cancel) = await CancelUrlAsync(api, customer, subscription.Id);
        var (_, companyCancel) = await CancelUrlAsync(api, company, theirs.Id);

        var companyPage = await ParseAsync(await browser.GetAsync(companyCancel, CancellationToken));
        using var canceled = await PostHandlerAsync(browser, cancel, string.Empty, ("Choice", "Now"));
        await host.RunWorkersUntilIdleAsync();
        await host.RunWorkersUntilIdleAsync();

        companyPage.QuerySelectorAll("#choices .choice").Select(choice => choice.TextContent.Trim()).ShouldBe(["At the end of the period (May 1, 2026)"]);
        canceled.Headers.Location!.OriginalString.ShouldEndWith("?done=canceled");
        var ended = await GetSubscriptionAsync(api, subscription.Id);
        (ended.Status, ended.EndedAt).ShouldBe((SubscriptionStatus.Canceled, April.AddDays(15)));
        (await host.QueryAsync<long>($"SELECT amount AS \"Value\" FROM tahsil.refunds")).ShouldBe([15_000L]);
        _email.Sent.Select(message => message.Subject).ShouldContain("Your subscription ended");
    }

    [Fact]
    [Trait("Requirement", "PRT-10")]
    public async Task AWithdrawal_RefundsEverythingPaid_WithinFourteenDays()
    {
        await using var host = Host(new() { ["Tahsil:Portal:Features:withdrawal"] = "true" });
        using var api = await ClientAsync(host);
        var (customer, subscription) = await SubscriberAsync(api, "ada@example.com");
        var (late, lateOne) = await SubscriberAsync(api, "grace@example.com");
        _time.Advance(TimeSpan.FromDays(10));
        using var browser = Open(host);
        var (_, cancel) = await CancelUrlAsync(api, customer, subscription.Id);

        var page = await ParseAsync(await browser.GetAsync(cancel, CancellationToken));
        using var withdrew = await PostHandlerAsync(browser, cancel, string.Empty, ("Choice", "Withdrawal"));
        await host.RunWorkersUntilIdleAsync();
        await host.RunWorkersUntilIdleAsync();
        _time.Advance(TimeSpan.FromDays(5));
        var (_, lateCancel) = await CancelUrlAsync(api, late, lateOne.Id);
        var afterward = await ParseAsync(await browser.GetAsync(lateCancel, CancellationToken));

        var choices = page.QuerySelectorAll("#choices .choice");
        choices[choices.Length - 1].TextContent.Trim().ShouldBe("I withdraw: end my subscription now and refund the ₺300.00 I paid");
        withdrew.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await GetSubscriptionAsync(api, subscription.Id)).Status.ShouldBe(SubscriptionStatus.Canceled);
        (await host.QueryAsync<long>($"SELECT amount AS \"Value\" FROM tahsil.refunds")).ShouldBe([30_000L]);
        (await QueryAsync(host, context => context.ConsentRecords.CountAsync(record => record.Type == ConsentType.Withdrawal, CancellationToken))).ShouldBe(1);
        _email.Sent.Single(message => message.Subject == "Your withdrawal is confirmed").Text.ShouldContain("Everything you paid for it, ₺300.00, is refunded");
        afterward.QuerySelectorAll("#choices .choice").Count.ShouldBe(2, "the fourteen days are over");
    }

    [Fact]
    [Trait("Requirement", "PRT-8")]
    public async Task ARetentionOffer_IsDeclinedWithOneClick_OrKeepsTheSubscriptionDiscounted()
    {
        await using var host = Host(new() { ["Tahsil:Portal:RetentionCoupon"] = "stay" });
        using var api = await ClientAsync(host);
        using (var created = await api.PostJsonAsync("/api/v1/coupons", new CreateCouponRequest { Key = "stay", Name = "Stay with us", PercentOff = 20, Duration = CouponDuration.Repeating, DurationInCycles = 3 }))
        {
            created.EnsureSuccessStatusCode();
        }

        var (customer, subscription) = await SubscriberAsync(api, "ada@example.com");
        using var browser = Open(host);
        var (_, cancel) = await CancelUrlAsync(api, customer, subscription.Id);

        var offer = await ParseAsync(await browser.GetAsync(cancel, CancellationToken));
        var declined = await ParseAsync(await browser.GetAsync(new Uri($"{cancel}?declined=true"), CancellationToken));
        using var stayed = await PostHandlerAsync(browser, cancel, "Stay");

        offer.QuerySelector("#retention")!.TextContent.ShouldContain("Stay with us (−20%, 3 months)");
        (offer.QuerySelector("#stay")!.ClassName, offer.QuerySelector("#decline-offer")!.ClassName).ShouldBe(("button", "button"), "equal prominence");
        offer.QuerySelector("#choices").ShouldBeNull();
        declined.QuerySelector("#choices").ShouldNotBeNull();
        stayed.Headers.Location!.OriginalString.ShouldEndWith("?done=kept");
        (await GetSubscriptionAsync(api, subscription.Id)).Discount!.Coupon.Key.ShouldBe("stay");
    }

    [Fact]
    [Trait("Requirement", "PRT-3")]
    [Trait("Requirement", "PRT-7")]
    public async Task BillingDetails_AreKeptUpToDate_AndNoActionReachesAnotherCustomer()
    {
        await using var host = Host(new() { ["Tahsil:Portal:Products:0:Product"] = "team" });
        using var api = await ClientAsync(host);
        var (customer, subscription) = await SubscriberAsync(api, "ada@example.com");
        var (other, theirs) = await SubscriberAsync(api, "grace@example.com");
        using var browser = Open(host);
        var (session, _) = await CancelUrlAsync(api, customer, subscription.Id);
        var details = new Uri($"{session.Url}/billing-details");

        using var invalid = await PostHandlerAsync(browser, details, string.Empty, ("Details.Name", "Ada Lovelace"), ("Details.TaxId", "123"));
        using var saved = await PostHandlerAsync(
            browser, details, string.Empty, ("Details.Name", "Ada King"), ("Details.AddressLine1", "Bağdat Cd. 1"), ("Details.City", "İstanbul"), ("Details.Phone", "+90 555 000 00 00"));
        var plus = (await GetAsync<PriceResponse>(api, "/api/v1/prices/team-plus-monthly-try")).Id;
        using var upgraded = await PostHandlerAsync(browser, new Uri($"{session.Url}/subscriptions/{subscription.Id}/change"), "Confirm", ("Price", plus.ToString()), ("Quantity", "1"));
        await host.RunWorkersUntilIdleAsync();
        using var theirCancel = await browser.GetAsync(new Uri($"{session.Url}/subscriptions/{theirs.Id}/cancel"), CancellationToken);
        using var theirCancelPosted = await PostHandlerAsync(browser, new Uri($"{session.Url}/subscriptions/{subscription.Id}/cancel"), string.Empty, ("Choice", "Now"), ("id", theirs.Id.ToString()));

        // With their own cancellation scheduled, the overview has its form to keep the subscription.
        using var scheduled = await PostHandlerAsync(browser, new Uri($"{session.Url}/subscriptions/{subscription.Id}/cancel"), string.Empty, ("Choice", "PeriodEnd"));
        using var theirResume = await PostHandlerAsync(browser, new Uri($"{session.Url}?done=overview"), "Resume", ("subscription", theirs.Id.ToString()));

        (invalid.StatusCode, (await ParseAsync(invalid)).QuerySelector("#portal-error")!.TextContent).ShouldBe(
            (HttpStatusCode.BadRequest, "For Turkey, individuals have an 11-digit TCKN and companies a 10-digit VKN (or the owner's TCKN); elsewhere at most 32 characters."));
        saved.Headers.Location!.OriginalString.ShouldEndWith("?done=details");
        var updated = await GetAsync<CustomerResponse>(api, $"/api/v1/customers/{customer}");
        (updated.Name, updated.Address!.City, updated.Phone, updated.Email).ShouldBe(("Ada King", "İstanbul", "+905550000000", "ada@example.com"));
        (theirCancel.StatusCode, theirCancelPosted.StatusCode, theirResume.StatusCode).ShouldBe((HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound));
        (await GetSubscriptionAsync(api, theirs.Id)).Status.ShouldBe(SubscriptionStatus.Active);
        (await GetAsync<CustomerResponse>(api, $"/api/v1/customers/{other}")).Name.ShouldBe("Ada Lovelace");
        upgraded.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var changed = _email.Sent.Single(message => message.Subject == "Your plan changed");
        changed.Text.ShouldContain("from Team (Aylık) to Team (Plus Aylık)");
        changed.Text.ShouldContain("₺300.00 was charged");
    }

    /// <summary>An individual, unless said otherwise, with a monthly Team subscription paid by card.</summary>
    private static async Task<(Guid Customer, SubscriptionResponse Subscription)> SubscriberAsync(HttpClient api, string email, CustomerType type = CustomerType.Individual)
    {
        using var created = await api.PostJsonAsync("/api/v1/customers", new CreateCustomerRequest { Name = "Ada Lovelace", Email = email, Locale = "en", Type = type });
        var customer = (await created.ReadAsync<CustomerResponse>()).Id;
        await SaveCardAsync(api, customer, "4242424242424242");
        return (customer, await CreateAsync(api, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try" }] }));
    }

    private static async Task<(PortalSessionResponse Session, Uri Cancel)> CancelUrlAsync(HttpClient api, Guid customer, Guid subscription)
    {
        using var response = await api.PostJsonAsync("/api/v1/portal-sessions", new CreatePortalSessionRequest { Customer = customer, ReturnUrl = "https://app.example.com/billing", Locale = "en" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        var session = await response.ReadAsync<PortalSessionResponse>();
        return (session, new Uri($"{session.Url}/subscriptions/{subscription}/cancel"));
    }

    private static async Task<T> QueryAsync<T>(WebApplicationFactory<Program> host, Func<TahsilDbContext, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TahsilDbContext>());
    }

    private WebApplicationFactory<Program> Host(Dictionary<string, string?>? settings = null) =>
        IsolatedHosts.Create(server, server.NewDatabase("portal_cancel"), _time, services => services.AddSingleton<IEmailSender>(_email), settings);
}
