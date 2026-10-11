using Bunit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Admin.Components.Pages.Customers;
using Tahsil.Admin.Components.Pages.Subscriptions;
using Tahsil.Admin.Security;
using Tahsil.Contracts;
using Tahsil.Infrastructure.Persistence;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;
using SubscriptionsPage = Tahsil.Admin.Components.Pages.Subscriptions.Subscriptions;

namespace Tahsil.Server.Tests.Admin;

/// <summary>The admin panel's subscription screens (ADM §3): list, detail, and the actions on a subscription.</summary>
[Trait("Requirement", "ADM-1")]
public sealed class SubscriptionScreensTests(TahsilServerFixture server)
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));

    private static CancellationToken CancellationToken => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task Subscriptions_AreListed_FilteredByStatus_AndShownOnTheCustomersPage()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var (customer, _) = await CompanyWithTeamAsync(client);
        await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "pro-monthly-try" }] });
        await using var admin = new AdminComponents(host);

        var list = admin.Render<SubscriptionsPage>();
        var customerPage = admin.Render<CustomerDetail>(parameters => parameters.Add(page => page.Id, customer));

        list.WaitForAssertion(() => list.Find(".subscriptions").TextContent.ShouldContain("Team (Aylık) × 3"));
        list.Find(".subscriptions").TextContent.ShouldContain("Pro (Aylık) × 1");
        customerPage.WaitForAssertion(() => customerPage.Find(".subscriptions").TextContent.ShouldContain("trialing"));
    }

    [Fact]
    [Trait("Requirement", "SUB-24")]
    [Trait("Requirement", "SUB-28")]
    [Trait("Requirement", "SUB-33")]
    public async Task APlan_IsChangedAfterAPreview_ThenCanceledAtThePeriodEnd_AndResumed()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var (_, subscription) = await CompanyWithTeamAsync(client);
        _time.Advance(TimeSpan.FromDays(15));
        await using var admin = new AdminComponents(host);
        var page = admin.Render<SubscriptionDetail>(parameters => parameters.Add(detail => detail.Id, subscription));

        page.WaitForElement("#change-plan").Click();
        admin.Fill("Quantity", "5");
        admin.Confirm("Preview");
        admin.Dialogs.WaitForAssertion(() => admin.Dialogs.Find("#preview-due").TextContent.ShouldBe("300.00 TRY"));
        admin.Confirm("Change");
        admin.WaitForNotification("The plan was changed.");
        page.WaitForAssertion(() => page.Find("#items").TextContent.ShouldContain("Team (Aylık) × 5"));
        page.Find(".invoices").TextContent.ShouldContain("paid");

        page.Find("#cancel-subscription").Click();
        admin.Confirm("Cancel the subscription");
        admin.WaitForNotification("The cancellation was scheduled.");
        page.WaitForElement("#resume").Click();
        admin.Confirm("Resume");
        admin.WaitForNotification("The subscription was resumed.");

        page.WaitForAssertion(() => page.Find("#ends").TextContent.ShouldStartWith("–"));
        await AuditedByAsync(host, admin, "subscription.updated");
    }

    [Fact]
    [Trait("Requirement", "SUB-17")]
    public async Task ATrial_IsEndedNow()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CompanyAsync(client);
        await SaveCardAsync(client, customer, "4242424242424242");
        var trial = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "pro-monthly-try" }] });
        await using var admin = new AdminComponents(host);
        var page = admin.Render<SubscriptionDetail>(parameters => parameters.Add(detail => detail.Id, trial.Id));

        page.WaitForElement("#trial-end").Click();
        admin.Confirm("End it now");

        admin.WaitForNotification("The trial ended.");
        page.WaitForAssertion(() => page.FindAll("#trial-end").ShouldBeEmpty());
        var ended = await QueryAsync(host, context => context.Subscriptions.SingleAsync(row => row.Id == trial.Id, CancellationToken));
        ended.CurrentPeriodStart.ShouldBe(_time.GetUtcNow());
    }

    [Fact]
    [Trait("Requirement", "ADM-4")]
    public async Task Viewers_SeeASubscription_WithoutItsActions()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var (_, subscription) = await CompanyWithTeamAsync(client);
        await using var viewer = new AdminComponents(host, AdminRoles.Viewer);

        var page = viewer.Render<SubscriptionDetail>(parameters => parameters.Add(detail => detail.Id, subscription));

        page.WaitForAssertion(() => page.Find("h1").TextContent.ShouldBe("Team (Aylık) × 3"));
        page.FindAll("#change-plan, #cancel-subscription, #resume, #trial-end, #retry-payment").ShouldBeEmpty();
    }

    /// <summary>A company paying for three Team seats with a saved card.</summary>
    private static async Task<(Guid Customer, Guid Subscription)> CompanyWithTeamAsync(HttpClient client)
    {
        var customer = await CompanyAsync(client);
        await SaveCardAsync(client, customer, "4242424242424242");
        var subscription = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try", Quantity = 3 }] });
        return (customer, subscription.Id);
    }

    private static async Task AuditedByAsync(WebApplicationFactory<Program> host, AdminComponents admin, string action)
    {
        var actors = await QueryAsync(host, context => context.AuditEvents
            .Where(auditEvent => auditEvent.Action == action && auditEvent.ActorId == admin.Subject)
            .Select(auditEvent => auditEvent.ActorType)
            .ToListAsync(CancellationToken));
        actors.ShouldNotBeEmpty($"{action} by the administrator");
    }

    private static async Task<T> QueryAsync<T>(WebApplicationFactory<Program> host, Func<TahsilDbContext, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TahsilDbContext>());
    }

    private WebApplicationFactory<Program> Host() => IsolatedHosts.Create(server, server.NewDatabase("admin_subscriptions"), _time);
}
