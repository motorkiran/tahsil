using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Application.Notifications;
using Tahsil.Application.Payments;
using Tahsil.Contracts;
using Tahsil.Domain.Common;
using Tahsil.Infrastructure.Persistence;
using static Tahsil.Server.Tests.Payments.PaymentScenarios;
using static Tahsil.Server.Tests.Subscriptions.SubscriptionScenarios;

namespace Tahsil.Server.Tests.Notifications;

/// <summary>Each scenario sends exactly its emails, once, however often the workers run (NTF-11, NTF-12, DUN §7).</summary>
public sealed class NotificationTests(TahsilServerFixture server)
{
    private static readonly DateTimeOffset April = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FirstFailure = new(2026, 5, 1, 1, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(April);
    private readonly CapturingEmailSender _email = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "DUN-6")]
    [Trait("Requirement", "NTF-12")]
    public async Task ARenewalThatIsNeverPaid_SendsTheFailureTheLastReminderAndTheCancellation_OnceEach()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CustomerAsync(client);
        var card = await SaveCardAsync(client, customer, "4242424242424242");
        await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try" }] });
        await host.RunWorkersUntilIdleAsync();
        Drain().ShouldHaveSingleItem().ShouldStartWith("Ödemeniz alındı:");
        await SetBehaviorAsync(client, card, "insufficient_funds");

        // The renewal, its failure, the retries of days 1, 3 and 5, the last reminder on day 6, the final action on day 7.
        await AdvanceToAsync(host, FirstFailure.AddHours(-1));
        foreach (var day in new[] { 0, 1, 3, 5, 6, 7, 8 })
        {
            await AdvanceToAsync(host, FirstFailure.AddDays(day));
            await host.RunWorkersUntilIdleAsync();
        }

        Drain().Select(Subject).ShouldBe(["Ödemeniz alınamadı", "Son hatırlatma", "Aboneliğiniz sona erdi"]);
    }

    [Fact]
    [Trait("Requirement", "NTF-12")]
    public async Task APaidRenewal_SendsAReceipt_AndARefundItsNotice()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CustomerAsync(client);
        await SaveCardAsync(client, customer, "4242424242424242");
        var subscription = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try" }] });
        await host.RunWorkersUntilIdleAsync();
        var payment = (await GetAsync<Page<PaymentResponse>>(client, $"/api/v1/payments?invoice={subscription.LatestInvoice!.Id}")).Items.Single();

        using (var refunded = await client.PostJsonAsync("/api/v1/refunds", new CreateRefundRequest { Payment = payment.Id }))
        {
            refunded.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        await host.RunWorkersUntilIdleAsync();
        await host.RunWorkersUntilIdleAsync();

        var number = (await GetAsync<InvoiceResponse>(client, $"/api/v1/invoices/{subscription.LatestInvoice.Id}")).Number;
        var sent = Drain();
        sent.Count.ShouldBe(2);
        sent[0].ShouldBe($"Ödemeniz alındı: {number}");
        sent[1].ShouldEndWith(": ₺300,00", Case.Sensitive, "the refund's notice");
    }

    [Fact]
    [Trait("Requirement", "SUB-34")]
    public async Task AHigherPriceForAnIndividual_IsAnnounced()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CustomerAsync(client);
        await SaveCardAsync(client, customer, "4242424242424242");
        var subscription = await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try" }] });
        await host.RunWorkersUntilIdleAsync();
        Drain();

        using var scheduled = await client.PatchJsonAsync($"/api/v1/subscriptions/{subscription.Id}", new
        {
            items = new[] { new { price = "team-plus-monthly-try", quantity = 1 } },
            effective = "period_end",
            effectiveAt = April.AddMonths(2),
        });
        await host.RunWorkersUntilIdleAsync();

        scheduled.StatusCode.ShouldBe(HttpStatusCode.OK, await scheduled.Content.ReadAsStringAsync(CancellationToken));
        var announcement = _email.Sent.ShouldHaveSingleItem();
        announcement.Subject.ShouldBe("Abonelik ücretiniz değişiyor");
        announcement.Text.ShouldContain("1 Haziran 2026 tarihinden itibaren ₺300,00 yerine ₺600,00");
    }

    [Fact]
    [Trait("Requirement", "NTF-1")]
    public async Task ATypeTurnedOff_SendsNothing_AndIsRecordedAsSkipped()
    {
        await using var host = Host(new Dictionary<string, string?> { ["Tahsil:Notifications:Types:receipt:Enabled"] = "false" });
        using var client = await ClientAsync(host);
        var customer = await CustomerAsync(client);
        await SaveCardAsync(client, customer, "4242424242424242");
        await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try" }] });

        await host.RunWorkersUntilIdleAsync();

        Drain().ShouldBeEmpty();
        (await host.QueryAsync<string>($"SELECT reason AS \"Value\" FROM tahsil.notification_log WHERE type = 'receipt'")).ShouldBe(["disabled"]);
    }

    [Fact]
    [Trait("Requirement", "NTF-9")]
    public async Task AnOverride_ReplacesTheBuiltInTemplate_AndAnInvalidOneStopsTahsil()
    {
        var directory = Directory.CreateTempSubdirectory("tahsil-templates").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "receipt.tr.liquid"), """
                {%- capture subject -%}Makbuz {{ invoice.number }}{%- endcapture -%}
                {%- capture html -%}<p>Teşekkürler</p>{%- endcapture -%}
                {%- capture text -%}Teşekkürler{%- endcapture -%}
                """, CancellationToken);
            await using (var host = Host(new Dictionary<string, string?> { ["Tahsil:Notifications:TemplatesPath"] = directory }))
            {
                using var client = await ClientAsync(host);
                var customer = await CustomerAsync(client);
                await SaveCardAsync(client, customer, "4242424242424242");
                await CreateAsync(client, new CreateSubscriptionRequest { Customer = customer, Items = [new() { Price = "team-monthly-try" }] });
                await host.RunWorkersUntilIdleAsync();

                Drain().ShouldHaveSingleItem().ShouldStartWith("Makbuz INV-");
            }

            await File.WriteAllTextAsync(Path.Combine(directory, "receipt.en.liquid"), "{% if %}", CancellationToken);
            await using var broken = Host(new Dictionary<string, string?> { ["Tahsil:Notifications:TemplatesPath"] = directory });

            Should.Throw<InvalidOperationException>(() => broken.CreateClient()).Message.ShouldContain("receipt.en.liquid");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Requirement", "NTF-7")]
    public async Task TheOperator_HearsOfEachAttentionItemOnce_AndOfOpenOnesDaily()
    {
        await using var host = Host(new Dictionary<string, string?> { ["Tahsil:Notifications:OperatorEmail"] = "ops@example.com" });
        await RaiseAsync(host, Guid.CreateVersion7());
        await RaiseAsync(host, Guid.CreateVersion7());
        await host.RunWorkersUntilIdleAsync();
        var raised = _email.Sent.Select(message => (message.To, message.Subject)).ToList();
        Drain();

        await AdvanceToAsync(host, April.AddHours(9));
        await QueueDigestAsync(host);
        await QueueDigestAsync(host);
        await host.RunWorkersUntilIdleAsync();

        raised.ShouldBe([("ops@example.com", "İlgilenilmesi gereken kayıt: merchant_failures"), ("ops@example.com", "İlgilenilmesi gereken kayıt: merchant_failures")]);
        Drain().ShouldBe(["2 kayıt hâlâ ilgi bekliyor"]);
    }

    private static string Subject(string subject) => subject.Split(':')[0];

    private static async Task RaiseAsync(WebApplicationFactory<Program> host, Guid subject)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<Attention>().RaiseAsync(
            AttentionKind.MerchantFailures, Domain.Auditing.AuditSubjects.Connection, subject, null, new Dictionary<string, string> { ["failureCode"] = "provider_unavailable" }, April, CancellationToken);
        await scope.ServiceProvider.GetRequiredService<TahsilDbContext>().SaveChangesAsync(CancellationToken);
    }

    private static async Task QueueDigestAsync(WebApplicationFactory<Program> host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OperatorDigest>().QueueIfDueAsync(CancellationToken);
        await host.RunWorkersUntilIdleAsync();
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

    private async Task AdvanceToAsync(WebApplicationFactory<Program> host, DateTimeOffset instant)
    {
        if (instant > _time.GetUtcNow())
        {
            _time.Advance(instant - _time.GetUtcNow());
        }

        await host.RunWorkersUntilIdleAsync();
    }

    private WebApplicationFactory<Program> Host(Dictionary<string, string?>? settings = null) =>
        IsolatedHosts.Create(server, server.NewDatabase("notifications"), _time, services => services.AddSingleton<IEmailSender>(_email), settings);
}
