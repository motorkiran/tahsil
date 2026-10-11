using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Application.Notifications;
using Tahsil.Contracts;
using Tahsil.Server.Tests.Notifications;
using static Tahsil.Server.Tests.Payments.PaymentScenarios;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;

namespace Tahsil.Server.Tests.Invoices;

/// <summary>
/// Invoices sent for payment (INV-12, INV-13, INV-22, DUN-11): a subscription's first invoice of April 1 is due at the
/// end of April 15, so its reminders come on April 12, 15, 18 and 22, it is overdue on April 16, and the final action
/// comes on April 23, a day after the last reminder.
/// </summary>
public sealed class InvoiceCollectionTests(TahsilServerFixture server)
{
    private static readonly DateTimeOffset April = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(April);
    private readonly CapturingEmailSender _email = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "INV-12")]
    [Trait("Requirement", "INV-13")]
    [Trait("Requirement", "DUN-11")]
    [Trait("Requirement", "NTF-12")]
    public async Task RemindersComeOnceEach_TheDueDatePassingMakesItPastDue_AndTheFinalActionFollowsTheLastReminder()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var subscription = await SendingInvoicesAsync(client, await CustomerAsync(client), daysUntilDue: 14);

        var sent = new List<(DateTimeOffset, string)>();
        for (var day = 0; day <= 25; day++)
        {
            AdvanceTo(April.AddDays(day));
            await host.RunWorkersUntilIdleAsync();
            sent.AddRange(Drain().Select(subject => (April.AddDays(day), subject)));
        }

        var number = (await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{subscription.LatestInvoice!.Id}")).Number;
        sent.ShouldBe(
        [
            (April, $"Faturanız: {number} (₺300,00)"),
            (April.AddDays(11), $"Hatırlatma: {number} 3 gün içinde ödenmeli"),
            (April.AddDays(14), $"Son ödeme günü bugün: {number}"),
            (April.AddDays(17), $"Gecikmiş fatura: {number}"),
            (April.AddDays(21), $"Gecikmiş fatura: {number}"),
        ]);
        var ended = await GetSubscriptionAsync(client, subscription.Id);
        (ended.Status, ended.CancellationReason, ended.EndedAt, ended.PastDueSince).ShouldBe(
            (SubscriptionStatus.Canceled, CancellationReason.PaymentFailed, April.AddDays(22), April.AddDays(15)));
        (await host.QueryAsync<string>($"SELECT action AS \"Value\" FROM tahsil.audit_events WHERE action IN ('invoice.overdue', 'subscription.past_due') ORDER BY action"))
            .ShouldBe(["invoice.overdue", "subscription.past_due"]);
    }

    [Fact]
    [Trait("Requirement", "INV-12")]
    [Trait("Requirement", "DUN-12")]
    public async Task APaidInvoice_IsRemindedOfNoMore()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var subscription = await SendingInvoicesAsync(client, await CustomerAsync(client), daysUntilDue: 14);
        AdvanceTo(April.AddDays(13));
        await host.RunWorkersUntilIdleAsync();

        await PayOnPageAsync(host, subscription.LatestInvoice!.Id, "4242424242424242");
        Drain();
        for (var day = 14; day <= 25; day++)
        {
            AdvanceTo(April.AddDays(day));
            await host.RunWorkersUntilIdleAsync();
        }

        Drain().ShouldBeEmpty();
        (await GetSubscriptionAsync(client, subscription.Id)).Status.ShouldBe(SubscriptionStatus.Active);
    }

    [Fact]
    [Trait("Requirement", "INV-15")]
    [Trait("Requirement", "DUN-10")]
    public async Task ABankTransferForAnOverdueInvoice_MakesTheSubscriptionActiveAgain()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var subscription = await SendingInvoicesAsync(client, await CustomerAsync(client), daysUntilDue: 14);
        AdvanceTo(April.AddDays(16));
        await host.RunWorkersUntilIdleAsync();
        (await GetSubscriptionAsync(client, subscription.Id)).Status.ShouldBe(SubscriptionStatus.PastDue);

        using var paid = await client.PostJsonAsync($"/api/v1/invoices/{subscription.LatestInvoice!.Id}/pay", new PayInvoiceRequest
        {
            OutOfBand = new OutOfBandPaymentRequest { Reference = "EFT 9001" },
        });

        paid.StatusCode.ShouldBe(HttpStatusCode.Created, await paid.Content.ReadAsStringAsync(CancellationToken));
        var active = await GetSubscriptionAsync(client, subscription.Id);
        (active.Status, active.PastDueSince).ShouldBe((SubscriptionStatus.Active, null));
    }

    [Fact]
    [Trait("Requirement", "INV-22")]
    public async Task AnInvoice_IsEmailedAgainOnRequest_AtMostOnceInTenMinutes()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var subscription = await SendingInvoicesAsync(client, await CustomerAsync(client), daysUntilDue: 14);
        await host.RunWorkersUntilIdleAsync();
        Drain().ShouldHaveSingleItem();
        _time.Advance(TimeSpan.FromMinutes(11));

        using var again = await client.PostPathAsync($"/api/v1/invoices/{subscription.LatestInvoice!.Id}/send");
        await host.RunWorkersUntilIdleAsync();
        using var tooSoon = await client.PostPathAsync($"/api/v1/invoices/{subscription.LatestInvoice.Id}/send");

        again.StatusCode.ShouldBe(HttpStatusCode.OK, await again.Content.ReadAsStringAsync(CancellationToken));
        Drain().ShouldHaveSingleItem().ShouldStartWith("Faturanız:");
        (tooSoon.StatusCode, await tooSoon.ProblemCodeAsync()).ShouldBe((HttpStatusCode.TooManyRequests, "invoice.send_rate_limited"));
    }

    private List<string> Drain()
    {
        var subjects = new List<string>();
        while (_email.Sent.TryDequeue(out var message))
        {
            subjects.Add(message.Subject);
        }

        return subjects;
    }

    private void AdvanceTo(DateTimeOffset instant)
    {
        if (instant > _time.GetUtcNow())
        {
            _time.Advance(instant - _time.GetUtcNow());
        }
    }

    private WebApplicationFactory<Program> Host() =>
        IsolatedHosts.Create(server, server.NewDatabase("collection"), _time, services => services.AddSingleton<IEmailSender>(_email));
}
