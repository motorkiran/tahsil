using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Tahsil.Application.Payments;
using Tahsil.Contracts;
using Tahsil.Providers.Abstractions;
using Tahsil.Providers.Simulator;

namespace Tahsil.Server.Tests.Payments;

/// <summary>The steps payment tests share: customers, finalized invoices, and paying on the simulator's card page.</summary>
internal static partial class PaymentScenarios
{
    private static readonly Uri ReturnUrl = new("https://shop.example.com/return");
    private static readonly Uri CancelUrl = new("https://shop.example.com/cancel");

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public static StartInvoicePayment Start(Guid invoice) =>
        new(invoice, ReturnUrl, CancelUrl, SaveCard: true, new CardConsent("Save my card for future payments", IPAddress.Loopback, "Tests"), "127.0.0.1");

    public static async Task<Guid> CustomerAsync(HttpClient client)
    {
        using var response = await client.PostJsonAsync("/api/v1/customers", new CreateCustomerRequest { Email = "ayse@example.com", Name = "Ayşe Yılmaz" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await response.ReadAsync<CustomerResponse>()).Id;
    }

    public static async Task<Guid> FinalizedInvoiceAsync(
        HttpClient client, Guid customer, string currency = "TRY", long amount = 12_500, CollectionMethod collection = CollectionMethod.ChargeAutomatically)
    {
        using var draft = await client.PostJsonAsync("/api/v1/invoices", new CreateInvoiceRequest
        {
            Customer = customer,
            Currency = currency,
            CollectionMethod = collection,
            Lines = [new InvoiceLineRequest { Description = "Consulting", UnitAmount = amount }],
        });
        draft.StatusCode.ShouldBe(HttpStatusCode.Created, await draft.Content.ReadAsStringAsync(CancellationToken));
        var id = (await draft.ReadAsync<InvoiceResponse>()).Id;
        using var finalized = await client.PostPathAsync($"/api/v1/invoices/{id}/finalize");
        finalized.StatusCode.ShouldBe(HttpStatusCode.OK, await finalized.Content.ReadAsStringAsync(CancellationToken));
        return id;
    }

    /// <summary>Starts a payment on the page, pays with a test card (approving 3D Secure) and completes it, as a customer's return would.</summary>
    public static async Task<Domain.Payments.Payment> PayOnPageAsync(WebApplicationFactory<Program> host, Guid invoice, string card)
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

    /// <summary>Starts a customer-present payment and leaves it waiting for the customer.</summary>
    public static async Task<Guid> StartOnPageAsync(WebApplicationFactory<Program> host, Guid invoice)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<CustomerPayments>().StartAsync(Start(invoice), CancellationToken)).Value.Payment.Id;
    }

    public static async Task<Domain.Payments.Payment> ApplyAsync(WebApplicationFactory<Program> host, Guid payment, PaymentOutcome outcome)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentOutcomes>().ApplyAsync(payment, outcome, CancellationToken);
    }

    [GeneratedRegex("simulator/pay/(?<token>[A-Za-z0-9_-]+)")]
    private static partial Regex TokenPattern();
}
