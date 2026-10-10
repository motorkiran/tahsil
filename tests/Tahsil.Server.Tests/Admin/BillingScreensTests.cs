using Bunit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tahsil.Admin.Components.Pages.Customers;
using Tahsil.Admin.Components.Pages.Invoices;
using Tahsil.Admin.Components.Pages.Operations;
using Tahsil.Admin.Components.Pages.Payments;
using Tahsil.Admin.Components.Pages.Webhooks;
using Tahsil.Admin.Security;
using Tahsil.Contracts;
using Tahsil.Infrastructure.Persistence;
using static Tahsil.Server.Tests.Payments.PaymentScenarios;
using ConnectionsPage = Tahsil.Admin.Components.Pages.Integrations.Connections;
using InvoicesPage = Tahsil.Admin.Components.Pages.Invoices.Invoices;
using InvoiceStatus = Tahsil.Domain.Invoices.InvoiceStatus;
using PaymentsPage = Tahsil.Admin.Components.Pages.Payments.Payments;
using PaymentStatus = Tahsil.Domain.Payments.PaymentStatus;
using WebhooksPage = Tahsil.Admin.Components.Pages.Webhooks.Webhooks;

namespace Tahsil.Server.Tests.Admin;

/// <summary>
/// The admin panel's M2 screens: connections, invoices, payments and refunds, webhooks, attention and Test mode. Tests
/// that change billing state run on a database of their own with the workers off, so nothing races the screens.
/// </summary>
[Trait("Requirement", "ADM-1")]
public sealed class BillingScreensTests(TahsilServerFixture server)
{
    private static readonly string[] Permissions =
    [
        TahsilPermissions.CustomersWrite, TahsilPermissions.CustomersRead, TahsilPermissions.InvoicesWrite, TahsilPermissions.InvoicesRead,
        TahsilPermissions.PaymentsRead,
    ];

    private static CancellationToken CancellationToken => Xunit.TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(typeof(ConnectionsPage), "Provider connections")]
    [InlineData(typeof(InvoicesPage), "Invoices")]
    [InlineData(typeof(PaymentsPage), "Payments")]
    [InlineData(typeof(WebhooksPage), "Webhooks")]
    [InlineData(typeof(Attention), "Attention")]
    [InlineData(typeof(TestMode), "Test mode")]
    public async Task Screen_Renders(Type page, string title)
    {
        await using var admin = new AdminComponents(server);

        var rendered = admin.Render(builder =>
        {
            builder.OpenComponent(0, page);
            builder.CloseComponent();
        });

        rendered.WaitForAssertion(() => rendered.Find("h1").TextContent.Trim().ShouldBe(title));
    }

    [Fact]
    [Trait("Requirement", "ADM-4")]
    public async Task Viewers_SeeConnectionsAndWebhooks_WithoutTheirActions()
    {
        await using var viewer = new AdminComponents(server, AdminRoles.Viewer);

        var connections = viewer.Render<ConnectionsPage>();
        var webhooks = viewer.Render<WebhooksPage>();
        var layout = viewer.Render<Tahsil.Admin.Components.Layout.NavMenu>();

        connections.WaitForAssertion(() => connections.Markup.ShouldContain("simulator"));
        connections.FindAll("#create-connection, #edit-routing, button.test, button.toggle").ShouldBeEmpty();
        webhooks.WaitForAssertion(() => webhooks.Find("h1").TextContent.Trim().ShouldBe("Webhooks"));
        webhooks.FindAll("#create-endpoint").ShouldBeEmpty();
        layout.Markup.ShouldContain("Payments");
        layout.Markup.ShouldNotContain("Test mode");
    }

    [Fact]
    [Trait("Requirement", "PAY-1")]
    public async Task AConnection_IsCreatedFromTheAdaptersFields_Tested_AndDisabled()
    {
        await using var host = Host();
        await TahsilServerFixture.WaitUntilReadyAsync(host);
        await using var admin = new AdminComponents(host);
        var page = admin.Render<ConnectionsPage>();

        page.WaitForElement("#create-connection").Click();
        admin.Dialogs.WaitForAssertion(() => admin.Dialogs.FindComponents<MudBlazor.MudSelectItem<string>>().ShouldNotBeEmpty());
        var provider = admin.Dialogs.FindComponents<MudBlazor.MudSelect<string>>().First(select => select.Instance.Label == "Provider");
        await provider.InvokeAsync(() => provider.Instance.ValueChanged.InvokeAsync(Application.Connections.ProviderRegistry.Simulator));
        admin.Fill("Key", "simulator-two");
        admin.Fill("Name", "Second simulator");
        admin.Confirm("Create");

        admin.WaitForNotification("The connection was created.");
        admin.WaitForNotification("simulator-two works.");
        page.WaitForAssertion(() => page.FindAll("#connections tbody tr").Single(row => row.TextContent.Contains("simulator-two")).TextContent.ShouldContain("succeeded"));
        page.FindAll("#connections tbody tr").Single(row => row.TextContent.Contains("simulator-two")).QuerySelector("button.toggle")!.Click();
        admin.Confirm("Disable");

        admin.WaitForNotification("The connection was disabled.");
        (await QueryAsync(host, context => context.ProviderConnections.SingleAsync(connection => connection.Key == "simulator-two", CancellationToken))).IsEnabled.ShouldBeFalse();
        await AuditedByAsync(host, admin, "connection.created");
    }

    [Fact]
    [Trait("Requirement", "INV-7")]
    [Trait("Requirement", "SIM-7")]
    public async Task AnInvoice_IsDraftedOnTheCustomersPage_Finalized_AndPaidWithATestCard()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var customer = await CustomerAsync(client);
        await using var admin = new AdminComponents(host);
        var detail = admin.Render<CustomerDetail>(parameters => parameters.Add(page => page.Id, customer));

        detail.WaitForElement("#add-test-card").Click();
        admin.Confirm("Save");
        admin.WaitForNotification("The test card was saved.");
        detail.WaitForAssertion(() => detail.Find("#payment-methods").TextContent.ShouldContain("4242"));
        detail.Find("#create-invoice").Click();
        admin.Confirm("Create");
        admin.WaitForNotification("The draft was created.");
        var invoiceId = await QueryAsync(host, context => context.Invoices.Where(invoice => invoice.CustomerId == customer).Select(invoice => invoice.Id).SingleAsync(CancellationToken));

        var invoicePage = admin.Render<InvoiceDetail>(parameters => parameters.Add(page => page.Id, invoiceId));
        invoicePage.WaitForElement("#add-line").Click();
        admin.Fill("Description", "Consulting");
        admin.Fill("Unit amount", "125");
        admin.Confirm("Add");
        admin.WaitForNotification("The line was added.");
        invoicePage.WaitForAssertion(() => invoicePage.Find("#lines").TextContent.ShouldContain("Consulting"));
        invoicePage.Find("#finalize").Click();
        admin.Confirm("Finalize");
        admin.WaitForNotification("The invoice was finalized.");
        invoicePage.WaitForElement("#pay").Click();
        admin.Confirm("Charge");

        admin.WaitForNotification("The payment succeeded.");
        invoicePage.WaitForAssertion(() => invoicePage.Find("#amount-remaining").TextContent.ShouldBe("0.00 TRY"));
        var paid = await QueryAsync(host, context => context.Invoices.SingleAsync(invoice => invoice.Id == invoiceId, CancellationToken));
        paid.Status.ShouldBe(InvoiceStatus.Paid);
        invoicePage.Find(".payments").TextContent.ShouldContain("succeeded");
        await AuditedByAsync(host, admin, "invoice.finalized");
        await AuditedByAsync(host, admin, "payment_method.attached");
    }

    [Fact]
    [Trait("Requirement", "PAY-18")]
    public async Task APayment_IsRefundedInPart_FromItsPage()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var invoice = await FinalizedInvoiceAsync(client, await CustomerAsync(client));
        var payment = await PayOnPageAsync(host, invoice, "4242424242424242");
        await using var admin = new AdminComponents(host);
        var page = admin.Render<PaymentDetail>(parameters => parameters.Add(detail => detail.Id, payment.Id));

        page.WaitForElement("#refund").Click();
        admin.Fill("Amount", "25");
        admin.Fill("Note", "Partial goodwill refund");
        admin.Confirm("Refund");

        admin.WaitForNotification("The refund succeeded.");
        page.WaitForAssertion(() => page.Find("#refunds").TextContent.ShouldContain("25.00 TRY"));
        var refunded = await QueryAsync(host, context => context.Payments.SingleAsync(candidate => candidate.Id == payment.Id, CancellationToken));
        refunded.AmountRefunded.ShouldBe(2_500);
        page.Find("#history").TextContent.ShouldContain("payment.succeeded");
        await AuditedByAsync(host, admin, "refund.succeeded");
    }

    [Fact]
    [Trait("Requirement", "ENG-16")]
    public async Task AnUncertainPayment_IsResolvedWithEvidence_AndItsAttentionItemClosed()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var customer = await CustomerAsync(client);
        var saved = await PayOnPageAsync(host, await FinalizedInvoiceAsync(client, customer), "4000000000001000");
        var invoice = await FinalizedInvoiceAsync(client, customer);
        using var paying = await client.PostJsonAsync($"/api/v1/invoices/{invoice}/pay", new PayInvoiceRequest { PaymentMethod = saved.PaymentMethodId!.Value });
        var uncertain = await paying.ReadAsync<PaymentResponse>();
        uncertain.Status.ShouldBe(Contracts.PaymentStatus.Processing);
        await QueryAsync(host, async context =>
        {
            context.AttentionItems.Add(Domain.Common.AttentionItem.Raise(
                Domain.Common.AttentionKind.ReconciliationOverdue, "payment", uncertain.Id, customer, new Dictionary<string, string>(), DateTimeOffset.UtcNow));
            return await context.SaveChangesAsync(CancellationToken);
        });
        await using var admin = new AdminComponents(host);
        var attention = admin.Render<Attention>();
        attention.WaitForAssertion(() => attention.Find("#attention-items").TextContent.ShouldContain("reconciliation_overdue"));

        var page = admin.Render<PaymentDetail>(parameters => parameters.Add(detail => detail.Id, uncertain.Id));
        page.WaitForElement("#resolve-payment").Click();
        admin.Fill("Provider payment ID", "sim_pay_seen_in_dashboard");
        admin.Fill("Evidence", "Captured in the provider's dashboard");
        admin.Confirm("Resolve");

        admin.WaitForNotification("The payment was resolved.");
        var resolved = await QueryAsync(host, context => context.Payments.SingleAsync(payment => payment.Id == uncertain.Id, CancellationToken));
        (resolved.Status, resolved.ProviderPaymentId).ShouldBe((PaymentStatus.Succeeded, "sim_pay_seen_in_dashboard"));
        (await QueryAsync(host, context => context.Invoices.SingleAsync(candidate => candidate.Id == invoice, CancellationToken))).Status.ShouldBe(InvoiceStatus.Paid);
        (await QueryAsync(host, context => context.AttentionItems.AnyAsync(item => item.ResolvedAt == null, CancellationToken))).ShouldBeFalse();
        await AuditedByAsync(host, admin, "payment.resolved");
    }

    [Fact]
    [Trait("Requirement", "PAY-15")]
    public async Task AnAttentionItem_IsResolvedWithWhatWasDone()
    {
        await using var host = Host();
        await TahsilServerFixture.WaitUntilReadyAsync(host);
        var subject = Guid.CreateVersion7();
        await QueryAsync(host, async context =>
        {
            context.AttentionItems.Add(Domain.Common.AttentionItem.Raise(
                Domain.Common.AttentionKind.AmountMismatch, "payment", subject, null, new Dictionary<string, string> { ["chargedAmount"] = "100" }, DateTimeOffset.UtcNow));
            return await context.SaveChangesAsync(CancellationToken);
        });
        await using var admin = new AdminComponents(host);
        var page = admin.Render<Attention>();

        page.WaitForElement("button.resolve").Click();
        admin.Fill("Resolution", "Refunded the difference at the provider");
        admin.Confirm("Resolve");

        admin.WaitForNotification("The item was resolved.");
        page.WaitForAssertion(() => page.Find("#attention-items").TextContent.ShouldContain("Nothing needs attention."));
        var item = await QueryAsync(host, context => context.AttentionItems.SingleAsync(candidate => candidate.SubjectId == subject, CancellationToken));
        item.Resolution.ShouldBe("Refunded the difference at the provider");
        await AuditedByAsync(host, admin, "attention.resolved");
    }

    [Fact]
    [Trait("Requirement", "EVT-8")]
    [Trait("Requirement", "EVT-14")]
    public async Task AWebhookEndpoint_IsAdded_ItsSecretShownOnce_AndATestEventSent()
    {
        await using var host = Host();
        await TahsilServerFixture.WaitUntilReadyAsync(host);
        await using var admin = new AdminComponents(host);
        var list = admin.Render<WebhooksPage>();

        list.WaitForElement("#create-endpoint").Click();
        admin.Fill("URL", "https://hooks.example.com/tahsil");
        admin.Confirm("Add");

        admin.WaitForNotification("The endpoint was added.");
        admin.Dialogs.WaitForAssertion(() => admin.Dialogs.Find(".secret input").GetAttribute("value")!.ShouldStartWith("whsec_"));
        var endpoint = await QueryAsync(host, context => context.WebhookEndpoints.SingleAsync(CancellationToken));
        var detail = admin.Render<WebhookEndpointDetail>(parameters => parameters.Add(page => page.Id, endpoint.Id));
        detail.WaitForElement("#send-test").Click();
        admin.WaitForNotification("A test event is on its way.");
        detail.WaitForAssertion(() => detail.Find("#deliveries").TextContent.ShouldContain("webhook.test"));
        await AuditedByAsync(host, admin, "webhook_endpoint.created");
    }

    [Fact]
    [Trait("Requirement", "ENG-23")]
    [Trait("Requirement", "SIM-8")]
    public async Task TimeTravel_ShowsItsSummary_AndASavedCardsBehaviorChanges()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var customer = await CustomerAsync(client);
        var saved = await PayOnPageAsync(host, await FinalizedInvoiceAsync(client, customer), "4242424242424242");
        await using var admin = new AdminComponents(host);
        var page = admin.Render<TestMode>();

        page.WaitForElement("input#advance-by").Change("P1D");
        page.Find("#advance").Click();
        admin.WaitForNotification("Time travel is done.");
        page.WaitForAssertion(() => page.Find("#time-travel-summary").TextContent.ShouldContain("Steps"));
        page.Find("#billing-now").TextContent.ShouldContain("1 days");
        page.Find("input#payment-method-id").Change(saved.PaymentMethodId!.Value.ToString());
        var behavior = page.FindComponents<MudBlazor.MudSelect<string>>().Single(select => select.Instance.Label == "Behavior");
        await behavior.InvokeAsync(() => behavior.Instance.ValueChanged.InvokeAsync("insufficient_funds"));
        page.Find("#set-behavior").Click();

        admin.WaitForNotification("The card now answers insufficient_funds.");
        var invoice = await FinalizedInvoiceAsync(client, customer);
        using var paying = await client.PostJsonAsync($"/api/v1/invoices/{invoice}/pay", new PayInvoiceRequest { PaymentMethod = saved.PaymentMethodId!.Value });
        (await paying.ReadAsync<PaymentResponse>()).FailureCode.ShouldBe("insufficient_funds");
        await AuditedByAsync(host, admin, "test_clock.advanced");
    }

    private WebApplicationFactory<Program> Host() =>
        server.WithSettings(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Tahsil"] = server.NewDatabase("admin_billing"),
            ["Tahsil:Workers:Enabled"] = "false",
        });

    private static async Task<T> QueryAsync<T>(WebApplicationFactory<Program> host, Func<TahsilDbContext, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TahsilDbContext>());
    }

    private static async Task AuditedByAsync(WebApplicationFactory<Program> host, AdminComponents admin, string action)
    {
        var actors = await QueryAsync(host, context => context.AuditEvents
            .Where(auditEvent => auditEvent.Action == action && auditEvent.ActorId == admin.Subject)
            .Select(auditEvent => auditEvent.ActorType)
            .ToListAsync(CancellationToken));
        actors.ShouldNotBeEmpty($"{action} by the administrator");
        actors.ShouldAllBe(type => type == Domain.Common.ActorType.Admin);
    }
}
