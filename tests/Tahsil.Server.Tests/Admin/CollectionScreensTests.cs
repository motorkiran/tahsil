using Bunit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Admin.Components.Pages.Invoices;
using Tahsil.Contracts;
using Tahsil.Infrastructure.Persistence;
using static Tahsil.Server.Tests.Payments.PaymentScenarios;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;
using InvoicesPage = Tahsil.Admin.Components.Pages.Invoices.Invoices;

namespace Tahsil.Server.Tests.Admin;

/// <summary>The admin panel's collection screens (ADM §3): recording payments, emailing invoices again, and dunning filters.</summary>
[Trait("Requirement", "ADM-1")]
public sealed class CollectionScreensTests(TahsilServerFixture server)
{
    private static readonly string[] Permissions =
    [
        TahsilPermissions.CustomersWrite, TahsilPermissions.CustomersRead, TahsilPermissions.InvoicesWrite, TahsilPermissions.InvoicesRead,
        TahsilPermissions.PaymentsRead, TahsilPermissions.TestWrite,
    ];

    private readonly FakeTimeProvider _time = new(FakeBillingClock.Start);

    private static CancellationToken CancellationToken => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "INV-14")]
    [Trait("Requirement", "INV-22")]
    public async Task ABankTransfer_IsRecordedInPart_AndTheInvoiceEmailedAgain()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var invoice = await FinalizedInvoiceAsync(client, await CustomerAsync(client), collection: CollectionMethod.SendInvoice);
        await using var admin = new AdminComponents(host);
        var page = admin.Render<InvoiceDetail>(parameters => parameters.Add(detail => detail.Id, invoice));

        page.WaitForElement("#record-payment").Click();
        admin.Fill("Amount", "50");
        admin.Fill("Reference", "EFT 4471");
        admin.Confirm("Record");
        admin.WaitForNotification("The payment was recorded.");
        page.WaitForAssertion(() => page.Find("#amount-remaining").TextContent.ShouldBe("75.00 TRY"));
        page.Find("#send").Click();

        admin.WaitForNotification("The invoice was emailed again.");
        var payment = await QueryAsync(host, context => context.Payments.SingleAsync(row => row.InvoiceId == invoice, CancellationToken));
        (payment.Amount, payment.OutOfBand!.Reference, payment.Actor.Id).ShouldBe((5_000L, "EFT 4471", admin.Subject));
    }

    [Fact]
    [Trait("Requirement", "DUN-2")]
    [Trait("Requirement", "INV-13")]
    public async Task TheInvoiceList_FiltersInvoicesInDunning_AndOverdueOnes()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var paying = await CustomerAsync(client);
        var card = await SaveCardAsync(client, paying, "4242424242424242");
        var charged = await CreateAsync(client, new CreateSubscriptionRequest { Customer = paying, Items = [new() { Price = "team-monthly-try" }] });
        var invoiced = await SendingInvoicesAsync(client, await CustomerAsync(client), daysUntilDue: 30);
        await host.RunWorkersUntilIdleAsync();
        await SetBehaviorAsync(client, card, "insufficient_funds");

        // The renewal of November 1 fails and the invoice due at the end of October 31 is overdue.
        for (var day = 1; day <= 33; day++)
        {
            _time.Advance(TimeSpan.FromDays(1));
            await host.RunWorkersUntilIdleAsync();
        }

        var failing = await NumberAsync(client, (await GetSubscriptionAsync(client, charged.Id)).LatestInvoice!.Id);
        var overdue = await NumberAsync(client, invoiced.LatestInvoice!.Id);
        await using var admin = new AdminComponents(host);
        var page = admin.Render<InvoicesPage>();
        page.WaitForAssertion(() => page.FindAll(".invoices tbody tr").Count.ShouldBe(4));

        await CheckAsync(page, "In dunning", true);
        page.WaitForAssertion(() => Numbers(page).ShouldBe([failing, overdue], ignoreOrder: true));
        await CheckAsync(page, "Overdue", true);
        page.WaitForAssertion(() => Numbers(page).ShouldBe([overdue]));
        (await GetAsync<Page<InvoiceResponse>>(client, "/api/v1/invoices?dunning=true")).Items.Select(invoice => invoice.Number).ShouldBe([failing, overdue], ignoreOrder: true);
        (await GetAsync<Page<InvoiceResponse>>(client, "/api/v1/invoices?overdue=true")).Items.ShouldHaveSingleItem().Number.ShouldBe(overdue);
    }

    private static async Task CheckAsync(IRenderedComponent<InvoicesPage> page, string label, bool value)
    {
        var box = page.FindComponents<MudBlazor.MudCheckBox<bool>>().Single(candidate => candidate.Instance.Label == label);
        await box.InvokeAsync(() => box.Instance.ValueChanged.InvokeAsync(value));
        await page.InvokeAsync(() => page.Find("#search-button").Click());
    }

    private static List<string> Numbers(IRenderedComponent<InvoicesPage> page) =>
        [.. page.FindAll(".invoices tbody tr").Select(row => row.QuerySelector("td")!.TextContent.Trim())];

    private static async Task<string> NumberAsync(HttpClient client, Guid invoice) => (await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{invoice}")).Number!;

    private static async Task<T> QueryAsync<T>(WebApplicationFactory<Program> host, Func<TahsilDbContext, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TahsilDbContext>());
    }

    private WebApplicationFactory<Program> Host() => IsolatedHosts.Create(server, server.NewDatabase("admin_collection"), _time);
}
