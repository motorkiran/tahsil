using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Tahsil.Application.Payments;
using Tahsil.Contracts;
using Tahsil.Providers.Abstractions;
using Tahsil.Providers.Simulator;
using PaymentStatus = Tahsil.Contracts.PaymentStatus;

namespace Tahsil.Server.Tests.Payments;

/// <summary>Payments of invoices through the simulator: customer-present, off session, and every way an outcome can arrive.</summary>
public sealed partial class PaymentTests(TahsilServerFixture server)
{
    private static readonly string[] Permissions =
    [
        TahsilPermissions.InvoicesRead, TahsilPermissions.InvoicesWrite, TahsilPermissions.CustomersRead, TahsilPermissions.CustomersWrite,
        TahsilPermissions.PaymentsRead, TahsilPermissions.ConnectionsWrite, TahsilPermissions.AuditRead,
    ];

    private static readonly Uri ReturnUrl = new("https://shop.example.com/return");

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "PAY-8")]
    [Trait("Requirement", "PAY-12")]
    [Trait("Requirement", "CUS-13")]
    public async Task ACustomerPresentPayment_PaysTheInvoice_AndSavesTheCardAsTheDefault()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var customer = await CustomerAsync(client);
        var invoice = await InvoiceAsync(client, customer);

        var payment = await PayOnPageAsync(host, invoice, "4000000000003220");
        await using var scope = host.Services.CreateAsyncScope();
        var again = await scope.ServiceProvider.GetRequiredService<CustomerPayments>().CompleteAsync(payment.Id, new Dictionary<string, string>(), CancellationToken);
        var paid = await ReadAsync<InvoiceResponse>(await client.GetPathAsync($"/api/v1/invoices/{invoice}"));
        var stored = await ReadAsync<PaymentResponse>(await client.GetPathAsync($"/api/v1/payments/{payment.Id}"));
        var defaultMethod = await host.QueryAsync<Guid?>($"SELECT default_payment_method_id AS \"Value\" FROM tahsil.customers WHERE id = {customer}");

        (stored.Status, stored.Initiator, stored.ThreeDSecure, stored.Reference).ShouldBe((PaymentStatus.Succeeded, PaymentInitiator.Customer, true, payment.Id.ToString("N")));
        stored.ProviderPaymentId.ShouldStartWith("sim_pay_");
        again.Value.Status.ShouldBe(Domain.Payments.PaymentStatus.Succeeded);
        (paid.Status, paid.AmountPaid, paid.AmountRemaining).ShouldBe((InvoiceStatus.Paid, 12_500, 0));
        stored.PaymentMethodId.ShouldNotBeNull();
        defaultMethod.ShouldBe([stored.PaymentMethodId]);
        (await host.QueryAsync<string>($"SELECT type AS \"Value\" FROM tahsil.consent_records WHERE customer_id = {customer}")).ShouldBe(["card_storage"]);
        (await host.QueryAsync<string>($"SELECT action AS \"Value\" FROM tahsil.audit_events WHERE action IN ('payment.succeeded', 'invoice.paid') ORDER BY id"))
            .ShouldBe(["payment.succeeded", "invoice.paid"], "PAY-13: completing again applies nothing");
    }

    [Fact]
    [Trait("Requirement", "PAY-12")]
    [Trait("Requirement", "PAY-26")]
    public async Task ADeclinedPayment_FailsWithANormalizedCode_AndTheInvoiceCanBePaidAgain()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var customer = await CustomerAsync(client);
        var invoice = await InvoiceAsync(client, customer);

        var declined = await PayOnPageAsync(host, invoice, "4000000000009995");
        var retried = await PayOnPageAsync(host, invoice, "4242424242424242");

        (declined.Status, declined.FailureCode, declined.FailureCategory).ShouldBe((Domain.Payments.PaymentStatus.Failed, "insufficient_funds", "retry"));
        declined.FailureMessage.ShouldBe("Your card has insufficient funds or limit.");
        declined.PaymentMethodId.ShouldBeNull();
        retried.Status.ShouldBe(Domain.Payments.PaymentStatus.Succeeded);
    }

    [Fact]
    [Trait("Requirement", "DOM-17")]
    [Trait("Requirement", "PAY-4")]
    public async Task OnePaymentIsInFlightPerInvoice_AndUnroutedCurrenciesTakeNone()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var customer = await CustomerAsync(client);
        var invoice = await InvoiceAsync(client, customer);
        var pounds = await InvoiceAsync(client, customer, "GBP");
        await using var scope = host.Services.CreateAsyncScope();
        var payments = scope.ServiceProvider.GetRequiredService<CustomerPayments>();

        var first = await payments.StartAsync(Start(invoice), CancellationToken);
        var second = await payments.StartAsync(Start(invoice), CancellationToken);
        var unrouted = await payments.StartAsync(Start(pounds), CancellationToken);

        first.Value.Payment.Status.ShouldBe(Domain.Payments.PaymentStatus.RequiresAction);
        first.Value.Presentation.ShouldBeOfType<Presentation.Embed>();
        second.Error!.Code.ShouldBe("invoice.payment_in_progress");
        unrouted.Error!.Code.ShouldBe("payment.currency_not_routed");
    }

    [Theory]
    [Trait("Requirement", "PAY-10")]
    [Trait("Requirement", "INV-10")]
    [InlineData("4242424242424242", PaymentStatus.Succeeded, null, InvoiceStatus.Paid)]
    [InlineData("4000000000000341", PaymentStatus.Failed, "card_declined", InvoiceStatus.Open)]
    [InlineData("4000002500003155", PaymentStatus.Failed, "authentication_required", InvoiceStatus.Open)]
    [InlineData("4000000000001000", PaymentStatus.Processing, null, InvoiceStatus.Open)]
    public async Task PayingWithASavedCard_ChargesItOffSession(string card, PaymentStatus status, string? failure, InvoiceStatus invoiceStatus)
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var customer = await CustomerAsync(client);
        var method = (await PayOnPageAsync(host, await InvoiceAsync(client, customer), card)).PaymentMethodId!.Value;
        var invoice = await InvoiceAsync(client, customer, amount: 9_900);

        using var response = await client.PostJsonAsync($"/api/v1/invoices/{invoice}/pay", new PayInvoiceRequest { PaymentMethod = method });
        var payment = await ReadAsync<PaymentResponse>(response, HttpStatusCode.Created);
        var after = await ReadAsync<InvoiceResponse>(await client.GetPathAsync($"/api/v1/invoices/{invoice}"));

        (payment.Status, payment.FailureCode, payment.Initiator, payment.PaymentMethodId).ShouldBe((status, failure, PaymentInitiator.Merchant, method));
        response.Headers.Location!.ToString().ShouldBe($"/api/v1/payments/{payment.Id}");
        after.Status.ShouldBe(invoiceStatus);
        var failedEvents = await host.QueryAsync<string>($"SELECT data->>'failureCode' AS \"Value\" FROM tahsil.audit_events WHERE action = 'invoice.payment_failed'");
        failedEvents.ShouldBe(failure is null ? [] : [failure]);
    }

    [Fact]
    [Trait("Requirement", "INV-10")]
    public async Task AnotherCustomersCard_CannotPayTheInvoice()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var owner = await CustomerAsync(client);
        var method = (await PayOnPageAsync(host, await InvoiceAsync(client, owner), "4242424242424242")).PaymentMethodId!.Value;
        var stranger = await CustomerAsync(client);
        var invoice = await InvoiceAsync(client, stranger);

        using var response = await client.PostJsonAsync($"/api/v1/invoices/{invoice}/pay", new PayInvoiceRequest { PaymentMethod = method });

        (await response.ProblemCodeAsync()).ShouldBe("invoice.payment_method_unusable");
        (await host.QueryAsync<int>($"SELECT count(*)::int AS \"Value\" FROM tahsil.payments WHERE invoice_id = {invoice}")).ShouldBe([0]);
    }

    [Fact]
    [Trait("Requirement", "PAY-15")]
    public async Task AChargeOfAnotherAmount_IsRecorded_ButLeftForAnOperator()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var customer = await CustomerAsync(client);
        var invoice = await InvoiceAsync(client, customer);
        var payment = await StartedAsync(host, invoice);

        var applied = await ApplyAsync(host, payment, new PaymentOutcome { Status = Providers.Abstractions.PaymentStatus.Succeeded, ChargedAmount = new Amount(12_000, "TRY", 2) });
        var after = await ReadAsync<InvoiceResponse>(await client.GetPathAsync($"/api/v1/invoices/{invoice}"));

        applied.Status.ShouldBe(Domain.Payments.PaymentStatus.Succeeded);
        (after.Status, after.AmountPaid).ShouldBe((InvoiceStatus.Open, 0));
        (await host.QueryAsync<string>($"SELECT kind AS \"Value\" FROM tahsil.attention_items WHERE subject_id = {payment}")).ShouldBe(["amount_mismatch"]);
    }

    [Fact]
    [Trait("Requirement", "PAY-14")]
    public async Task ALateSuccess_PaysTheInvoiceIfItStillNeedsIt_OtherwiseItGoesToAnOperator()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var customer = await CustomerAsync(client);
        var invoice = await InvoiceAsync(client, customer);
        var first = (await PayOnPageAsync(host, invoice, "4000000000000002")).Id;
        var second = (await PayOnPageAsync(host, invoice, "4000000000000002")).Id;

        var lateFirst = await ApplyAsync(host, first, new PaymentOutcome { Status = Providers.Abstractions.PaymentStatus.Succeeded });
        var paid = await ReadAsync<InvoiceResponse>(await client.GetPathAsync($"/api/v1/invoices/{invoice}"));
        var lateSecond = await ApplyAsync(host, second, new PaymentOutcome { Status = Providers.Abstractions.PaymentStatus.Succeeded });
        var unchanged = await ReadAsync<InvoiceResponse>(await client.GetPathAsync($"/api/v1/invoices/{invoice}"));

        (lateFirst.Status, lateSecond.Status).ShouldBe((Domain.Payments.PaymentStatus.Succeeded, Domain.Payments.PaymentStatus.Succeeded));
        (paid.Status, paid.AmountPaid).ShouldBe((InvoiceStatus.Paid, 12_500));
        unchanged.AmountPaid.ShouldBe(12_500, "the customer is never charged twice");
        (await host.QueryAsync<string>($"SELECT kind AS \"Value\" FROM tahsil.attention_items WHERE subject_id = {second}")).ShouldBe(["late_success"]);
        (await host.QueryAsync<string>($"SELECT data->>'lateSuccess' AS \"Value\" FROM tahsil.audit_events WHERE action = 'payment.succeeded' ORDER BY id")).ShouldBe(["true", "true"]);
    }

    [Fact]
    [Trait("Requirement", "PAY-13")]
    public async Task AFailureReportedForASucceededPayment_ChangesNothing_ButIsReported()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var customer = await CustomerAsync(client);
        var payment = (await PayOnPageAsync(host, await InvoiceAsync(client, customer), "4242424242424242")).Id;

        var applied = await ApplyAsync(host, payment, PaymentOutcome.Failure(FailureCode.CardDeclined));
        await ApplyAsync(host, payment, PaymentOutcome.Failure(FailureCode.CardDeclined));

        applied.Status.ShouldBe(Domain.Payments.PaymentStatus.Succeeded);
        (await host.QueryAsync<string>($"SELECT kind AS \"Value\" FROM tahsil.attention_items WHERE subject_id = {payment}")).ShouldBe(["conflicting_outcome"]);
    }

    [Fact]
    [Trait("Requirement", "CUS-12")]
    public async Task ATokenIsStoredOnce_AndNeverForAnotherCustomer()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        var customer = await CustomerAsync(client);
        var stranger = await CustomerAsync(client);
        var saved = new SavedCard { Token = "sim_card_shared", Brand = "Visa", Funding = "credit", Last4 = "4242", ExpMonth = 12, ExpYear = 2030 };
        var outcome = new PaymentOutcome { Status = Providers.Abstractions.PaymentStatus.Succeeded, SavedCard = saved };

        var first = await ApplyAsync(host, await StartedAsync(host, await InvoiceAsync(client, customer)), outcome);
        var second = await ApplyAsync(host, await StartedAsync(host, await InvoiceAsync(client, customer)), outcome with { SavedCard = saved with { Last4 = "4243" } });
        var foreign = await ApplyAsync(host, await StartedAsync(host, await InvoiceAsync(client, stranger)), outcome);

        second.PaymentMethodId.ShouldBe(first.PaymentMethodId);
        foreign.PaymentMethodId.ShouldBeNull();
        (await host.QueryAsync<string>($"SELECT last4 AS \"Value\" FROM tahsil.payment_methods")).ShouldBe(["4243"]);
        (await host.QueryAsync<string>($"SELECT brand AS \"Value\" FROM tahsil.payment_methods")).ShouldBe(["visa"]);
    }

    [Fact]
    [Trait("Requirement", "DOM-9")]
    public async Task AConnectionThatTookPayments_CannotBeDeleted()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(Permissions);
        await PayOnPageAsync(host, await InvoiceAsync(client, await CustomerAsync(client)), "4242424242424242");

        using var response = await client.DeleteAsync(new Uri("/api/v1/provider-connections/simulator", UriKind.Relative), CancellationToken);

        (await response.ProblemCodeAsync()).ShouldBe("connection.in_use");
    }

    /// <summary>Starts a payment on the page, pays with a test card (approving 3D Secure) and completes it, as a customer's return would.</summary>
    private static async Task<Domain.Payments.Payment> PayOnPageAsync(WebApplicationFactory<Program> host, Guid invoice, string card)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var payments = scope.ServiceProvider.GetRequiredService<CustomerPayments>();
        var started = (await payments.StartAsync(Start(invoice), CancellationToken)).Value;
        var token = TokenPattern().Match(started.Presentation.ShouldBeOfType<Presentation.Embed>().Html).Groups["token"].Value;
        var bank = host.Services.GetRequiredService<SimulatorBank>();
        if (await bank.SubmitCardAsync(token, new CardEntryForm(card, "12/30", "123", SaveCard: true), CancellationToken) is CardPageResult.Authenticate)
        {
            await bank.AuthenticateAsync(token, AuthenticationDecision.Approve, CancellationToken);
        }

        return (await payments.CompleteAsync(started.Payment.Id, new Dictionary<string, string>(), CancellationToken)).Value;
    }

    private static async Task<Guid> StartedAsync(WebApplicationFactory<Program> host, Guid invoice)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<CustomerPayments>().StartAsync(Start(invoice), CancellationToken)).Value.Payment.Id;
    }

    private static async Task<Domain.Payments.Payment> ApplyAsync(WebApplicationFactory<Program> host, Guid payment, PaymentOutcome outcome)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentOutcomes>().ApplyAsync(payment, outcome, CancellationToken);
    }

    private static StartInvoicePayment Start(Guid invoice) => new(
        invoice, ReturnUrl, new Uri("https://shop.example.com/cancel"), SaveCard: true, new CardConsent("Save my card for future payments", IPAddress.Loopback, "Tests"), "127.0.0.1");

    private static async Task<Guid> CustomerAsync(HttpClient client) =>
        (await ReadAsync<CustomerResponse>(await client.PostJsonAsync("/api/v1/customers", new CreateCustomerRequest { Email = "ayse@example.com", Name = "Ayşe Yılmaz" }), HttpStatusCode.Created)).Id;

    private static async Task<Guid> InvoiceAsync(HttpClient client, Guid customer, string currency = "TRY", long amount = 12_500)
    {
        var draft = await ReadAsync<InvoiceResponse>(await client.PostJsonAsync("/api/v1/invoices", new CreateInvoiceRequest
        {
            Customer = customer,
            Currency = currency,
            Lines = [new InvoiceLineRequest { Description = "Consulting", UnitAmount = amount }],
        }), HttpStatusCode.Created);
        return (await ReadAsync<InvoiceResponse>(await client.PostPathAsync($"/api/v1/invoices/{draft.Id}/finalize"))).Id;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<T>();
    }

    private WebApplicationFactory<Program> Host() =>
        server.WithSettings(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Tahsil"] = server.NewDatabase("payments"),

            // The payment worker would race the payments these tests make; worker tests drive it themselves.
            ["Tahsil:Workers:Enabled"] = "false",
        });

    [GeneratedRegex("simulator/pay/(?<token>[A-Za-z0-9_-]+)")]
    private static partial Regex TokenPattern();
}
