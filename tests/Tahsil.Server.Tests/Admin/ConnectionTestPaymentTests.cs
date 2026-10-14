using System.Net;
using Bunit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tahsil.Application.Connections;
using Tahsil.Contracts;
using Tahsil.Infrastructure.Persistence;
using static Tahsil.Server.Tests.Checkout.CheckoutBrowser;
using ConnectionsPage = Tahsil.Admin.Components.Pages.Integrations.Connections;

namespace Tahsil.Server.Tests.Admin;

/// <summary>The operator's test payment of a connection (ADP-18), on the simulator: paid on its page, refunded at once, remembered.</summary>
[Trait("Requirement", "ADP-18")]
public sealed class ConnectionTestPaymentTests(TahsilServerFixture server)
{
    private static readonly string[] Permissions = [TahsilPermissions.InvoicesRead, TahsilPermissions.PaymentsRead, TahsilPermissions.CustomersRead, TahsilPermissions.ConnectionsWrite];

    private static CancellationToken CancellationToken => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task ATestPayment_IsPaidOnItsPage_RefundedAtOnce_AndRemembered()
    {
        await using var host = Host();
        await TahsilServerFixture.WaitUntilReadyAsync(host);
        using var api = await host.CreateClientWithAsync(Permissions);
        await using var admin = new AdminComponents(host);
        var page = admin.Render<ConnectionsPage>();

        // An abandoned test, then the one that is paid: the first is voided when the second starts.
        var abandoned = await StartAsync(admin, page);
        var payment = await StartAsync(admin, page);
        using var browser = Open(host);
        using var started = await PostHandlerAsync(browser, payment, "Pay");
        using var returned = await PayOnTheEmbedAsync(host, browser, started, "4242424242424242");
        await host.RunWorkersUntilIdleAsync();

        returned.Headers.Location!.ToString().ShouldEndWith("?paid=true");
        var customer = await QueryAsync(host, context => context.Customers.SingleAsync(row => row.ExternalId == StartConnectionTestPaymentHandler.CustomerExternalId, CancellationToken));
        var invoices = (await GetAsync<Page<InvoiceResponse>>(api, $"/api/v1/invoices?customer={customer.Id}")).Items.OrderBy(invoice => invoice.Number).ToList();
        invoices.Select(invoice => (invoice.Status, invoice.Total, invoice.Currency, Url: invoice.HostedInvoiceUrl)).ShouldBe(
            [(InvoiceStatus.Void, 100L, "EUR", abandoned), (InvoiceStatus.Paid, 100L, "EUR", payment)], "the first of the currencies routed to the connection");
        invoices[1].Lines.ShouldHaveSingleItem().Description.ShouldBe("Connection test payment");
        var paid = (await GetAsync<Page<PaymentResponse>>(api, $"/api/v1/payments?invoice={invoices[1].Id}")).Items.ShouldHaveSingleItem();
        var refund = (await GetAsync<Page<RefundResponse>>(api, $"/api/v1/refunds?payment={paid.Id}")).Items.ShouldHaveSingleItem();
        (refund.Amount, refund.Reason, refund.Status).ShouldBe((100L, RefundReason.ConnectionTest, RefundStatus.Succeeded));

        var connection = await QueryAsync(host, context => context.ProviderConnections.SingleAsync(CancellationToken));
        connection.LastTestPaymentAt.ShouldNotBeNull();
        (await QueryAsync(host, context => context.AuditEvents.Where(row => row.SubjectId == connection.Id.ToString() && row.Action.StartsWith("connection.test_payment")).OrderBy(row => row.OccurredAt).Select(row => row.Action).ToListAsync(CancellationToken)))
            .ShouldBe(["connection.test_payment_started", "connection.test_payment_started", "connection.test_payment_succeeded"]);
        var again = admin.Render<ConnectionsPage>();
        again.WaitForAssertion(() => again.Find(".test-payment-passed").TextContent.ShouldStartWith("Test payment passed"));
    }

    [Fact]
    public async Task ATestPayment_NeedsAnEnabledConnectionWithACurrencyRoutedToIt()
    {
        await using var host = Host();
        await TahsilServerFixture.WaitUntilReadyAsync(host);
        using var api = await host.CreateClientWithAsync(Permissions);
        using (var created = await api.PostJsonAsync("/api/v1/provider-connections", new { key = "spare", provider = "simulator", name = "Spare", environment = "sandbox", credentials = new { } }))
        {
            created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(CancellationToken));
        }

        await using var admin = new AdminComponents(host);
        var page = admin.Render<ConnectionsPage>();
        page.WaitForAssertion(() => page.FindAll("#connections tbody tr").Count.ShouldBe(2));

        page.FindAll("#connections tbody tr").Single(row => row.TextContent.Contains("spare")).QuerySelector("button.test-payment").ShouldBeNull("nothing is routed to it");
        var unrouted = await admin.Services.GetRequiredService<Tahsil.Admin.Security.AdminOperations>()
            .RunAsync<StartConnectionTestPaymentHandler, Domain.Common.Result<ConnectionTestPayment>>(handler => handler.HandleAsync("spare", null, CancellationToken));
        var otherCurrency = await admin.Services.GetRequiredService<Tahsil.Admin.Security.AdminOperations>()
            .RunAsync<StartConnectionTestPaymentHandler, Domain.Common.Result<ConnectionTestPayment>>(handler => handler.HandleAsync("simulator", "GBP", CancellationToken));
        (unrouted.Error!.Code, otherCurrency.Error!.Code).ShouldBe(("connection.not_routed", "connection.not_routed"));

        await using var viewer = new AdminComponents(host, Tahsil.Admin.Security.AdminRoles.Viewer);
        var denied = await viewer.Services.GetRequiredService<Tahsil.Admin.Security.AdminOperations>()
            .RunAsync<StartConnectionTestPaymentHandler, Domain.Common.Result<ConnectionTestPayment>>(handler => handler.HandleAsync("simulator", null, CancellationToken));
        denied.Error!.Type.ShouldBe(Domain.Common.ErrorType.Forbidden);
    }

    /// <summary>Starts a test payment of the simulator connection in the panel and returns the page to pay it on.</summary>
    private static async Task<Uri> StartAsync(AdminComponents admin, IRenderedComponent<ConnectionsPage> page)
    {
        page.WaitForElement("button.test-payment").Click();
        admin.Confirm("Create the test payment");
        admin.WaitForNotification("The test payment is ready to pay.");
        admin.Dialogs.WaitForElement("#test-payment-url");
        var url = new Uri(admin.Dialogs.Find("#test-payment-url").GetAttribute("href")!);
        admin.Confirm("Done");
        await Task.CompletedTask;
        return url;
    }

    private static async Task<T> GetAsync<T>(HttpClient api, string path)
    {
        using var response = await api.GetPathAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<T>();
    }

    private static async Task<T> QueryAsync<T>(WebApplicationFactory<Program> host, Func<TahsilDbContext, Task<T>> query)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<TahsilDbContext>());
    }

    private WebApplicationFactory<Program> Host() =>
        server.WithSettings(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Tahsil"] = server.NewDatabase("connection_test_payment"),
            ["Tahsil:Workers:Enabled"] = "false",
        });
}
