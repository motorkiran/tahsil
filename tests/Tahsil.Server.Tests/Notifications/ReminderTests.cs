using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Tahsil.Application.Notifications;
using Tahsil.Contracts;
using static Tahsil.Server.Tests.Payments.PaymentScenarios;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;

namespace Tahsil.Server.Tests.Notifications;

/// <summary>Reminders through time travel (ENG-20, ENG-23): each once per occurrence, at its notice before the target.</summary>
public sealed class ReminderTests(TahsilServerFixture server)
{
    private readonly CapturingEmailSender _email = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "SUB-16")]
    public async Task ACardTrial_IsRemindedOfItsEndOnce_ThreeDaysAhead_WithTheFirstCharge()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CustomerAsync(client);
        await SaveCardAsync(client, customer, "4242424242424242");
        var trial = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "pro-monthly-try" }] });

        await TravelAsync(client, "P20D");

        var reminder = Sent("Deneme süreniz").ShouldHaveSingleItem();
        reminder.Text.ShouldContain("₺500,00 tahsil edilecek");
        (await host.QueryAsync<DateTimeOffset>($"SELECT occurred_at AS \"Value\" FROM tahsil.audit_events WHERE action = 'subscription.trial_will_end'"))
            .ShouldHaveSingleItem().ShouldBeInRange(trial.TrialEnd!.Value.AddDays(-3), trial.TrialEnd.Value.AddDays(-3).AddSeconds(2));
    }

    [Fact]
    [Trait("Requirement", "NTF-6")]
    public async Task AYearlySubscription_IsRemindedOfItsRenewal_TwoWeeksAhead_AMonthlyOneIsNot()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CompanyAsync(client);
        await SaveCardAsync(client, customer, "4242424242424242");
        var yearly = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-yearly-try" }] });
        await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try" }] });

        await TravelAsync(client, "P370D");

        var reminder = Sent("Aboneliğiniz").Where(message => message.Subject.Contains("yenileniyor", StringComparison.Ordinal)).ShouldHaveSingleItem();
        reminder.Text.ShouldContain("Team (Yıllık) × 1");
        (await host.QueryAsync<DateTimeOffset>($"SELECT (data->>'renewsAt')::timestamptz AS \"Value\" FROM tahsil.audit_events WHERE action = 'subscription.will_renew'"))
            .ShouldHaveSingleItem().ShouldBe(yearly.CurrentPeriodEnd, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    [Trait("Requirement", "CUS-15")]
    public async Task ACardASubscriptionUses_IsRemindedOfOnce_AMonthBeforeItExpires()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CompanyAsync(client);
        var nextMonth = TimeProvider.System.GetUtcNow().AddMonths(1);
        var card = await SaveExpiringCardAsync(client, customer, nextMonth.Month, nextMonth.Year);
        var unused = await SaveExpiringCardAsync(client, await CustomerAsync(client), nextMonth.Month, nextMonth.Year);
        await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try" }], DefaultPaymentMethod = card });

        await TravelAsync(client, "P75D");

        Sent("Kartınızın süresi doluyor").ShouldHaveSingleItem().Text.ShouldContain("Team (Aylık) × 1");
        (await host.QueryAsync<string>($"SELECT subject_id AS \"Value\" FROM tahsil.audit_events WHERE action = 'payment_method.will_expire'")).ShouldBe([card.ToString()]);
        (await host.QueryAsync<string>($"SELECT id::text AS \"Value\" FROM tahsil.payment_methods WHERE id = {unused} AND status = 'expired'")).ShouldHaveSingleItem();
    }

    private List<EmailMessage> Sent(string subjectStart) => [.. _email.Sent.Where(message => message.Subject.StartsWith(subjectStart, StringComparison.Ordinal))];

    private static async Task TravelAsync(HttpClient client, string duration)
    {
        using var travel = await client.PostAsJsonAsync(new Uri("/api/v1/test/clock", UriKind.Relative), new AdvanceClockRequest { AdvanceBy = duration }, ApiClients.Json, CancellationToken);
        travel.StatusCode.ShouldBe(HttpStatusCode.OK, await travel.Content.ReadAsStringAsync(CancellationToken));
    }

    private static async Task<Guid> SaveExpiringCardAsync(HttpClient client, Guid customer, int month, int year)
    {
        using var response = await client.PostJsonAsync("/api/v1/test/simulator/payment-methods", new CreateSimulatorPaymentMethodRequest
        {
            Customer = customer,
            Card = "4242424242424242",
            ExpMonth = month,
            ExpYear = year,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return (await response.ReadAsync<PaymentMethodResponse>()).Id;
    }

    private WebApplicationFactory<Program> Host() =>
        server.WithSettings(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Tahsil"] = server.NewDatabase("reminders"),

                // Time travel runs the workers itself.
                ["Tahsil:Workers:Enabled"] = "false",
            },
            services => services.AddSingleton<IEmailSender>(_email));
}
