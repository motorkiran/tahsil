using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Application.Notifications;
using Tahsil.Contracts;
using Tahsil.Infrastructure.Persistence;
using static Tahsil.Server.Tests.Checkout.CheckoutBrowser;

namespace Tahsil.Server.Tests.Notifications;

/// <summary>The order confirmation (NTF-3): sent once after a checkout, with the accepted documents attached.</summary>
public sealed class OrderConfirmationTests(TahsilServerFixture server)
{
    private static readonly string[] Permissions = [TahsilPermissions.CheckoutWrite, TahsilPermissions.CatalogWrite, TahsilPermissions.ConnectionsWrite];

    private readonly FakeTimeProvider _time = new(FakeBillingClock.Start);
    private readonly CapturingEmailSender _email = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "NTF-3")]
    [Trait("Requirement", "NTF-8")]
    [Trait("Requirement", "NTF-11")]
    [Trait("Requirement", "NTF-12")]
    public async Task APurchase_IsConfirmedOnce_WithTheAcceptedDocumentsAttached()
    {
        await using var host = Host(_email);
        var session = await PurchaseAsync(host);
        await host.RunWorkersUntilIdleAsync();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<Application.Common.IOutboxMessageHandler<SendOrderConfirmation>>().HandleAsync(new SendOrderConfirmation(session), CancellationToken);
        }

        var email = _email.Sent.ShouldHaveSingleItem("a second intent sends nothing");
        var consents = await QueryAsync(host, context => context.ConsentRecords.Where(record => record.CheckoutSessionId == session && record.DocumentKey != null)
            .OrderBy(record => record.DocumentKey).ToListAsync(CancellationToken));
        var number = await QueryAsync(host, context => context.Invoices.Select(invoice => invoice.Number).SingleAsync(CancellationToken));

        (email.To, email.ToName).ShouldBe(("ayse@example.com", "Ayşe Yılmaz"));
        email.Subject.ShouldBe($"Siparişiniz alındı ({number})");
        email.Html.ShouldContain("1,000 credits × 2");
        email.Html.ShouldContain("₺598,00");
        email.Text.ShouldContain("Ödenen tutar: ₺598,00");
        email.Html.ShouldContain("Acme Yazılım A.Ş.");
        email.Attachments.Select(attachment => attachment.FileName).ShouldBe(["distance-sales-2026-10.html", "pre-information-2026-10.html"]);
        email.Attachments.Select(attachment => Encoding.UTF8.GetString(attachment.Content)).ShouldBe(consents.Select(record => record.ReadDocument()!), "identical to the consent records' copies");
        (await QueryAsync(host, context => context.NotificationLogs.Select(log => log.Status).ToListAsync(CancellationToken)))
            .ShouldBe([Domain.Notifications.NotificationStatus.Sent]);
    }

    [Fact]
    [Trait("Requirement", "NTF-13")]
    public async Task WithoutSmtp_TheConfirmationIsRecordedAsSkipped()
    {
        await using var host = Host(email: null);
        await PurchaseAsync(host);
        await host.RunWorkersUntilIdleAsync();

        var log = await QueryAsync(host, context => context.NotificationLogs.SingleAsync(CancellationToken));
        (log.Type, log.Status, log.Reason).ShouldBe(("order_confirmation", Domain.Notifications.NotificationStatus.Skipped, "email_not_configured"));
        (await host.QueryAsync<int>($"SELECT count(*)::int AS \"Value\" FROM tahsil.outbox_messages WHERE status <> 'processed'")).ShouldBe([0], "skipping is no failure to retry");
    }

    private WebApplicationFactory<Program> Host(CapturingEmailSender? email) =>
        IsolatedHosts.Create(server, server.NewDatabase("notifications"), _time, services =>
        {
            if (email is not null)
            {
                services.AddSingleton<IEmailSender>(email);
            }
        });

    /// <summary>A customer buys two of a ₺299.00 price with KDV inside on the checkout page, in Turkish.</summary>
    private static async Task<Guid> PurchaseAsync(WebApplicationFactory<Program> host)
    {
        using var api = await host.CreateClientWithAsync(Permissions);
        using (var request = new HttpRequestMessage(HttpMethod.Patch, new Uri("/api/v1/provider-connections/simulator", UriKind.Relative))
        {
            Content = JsonContent.Create(new { settings = new { notifications = false } }, options: ApiClients.Json),
        })
        {
            (await api.SendAsync(request, CancellationToken)).EnsureSuccessStatusCode().Dispose();
        }

        (await api.PostJsonAsync("/api/v1/tax-rates", new CreateTaxRateRequest { Key = "kdv-20", Name = "KDV", Percentage = 20, Inclusive = true })).EnsureSuccessStatusCode().Dispose();
        (await api.PostJsonAsync("/api/v1/products", new CreateProductRequest { Key = "credits", Name = "1,000 credits" })).EnsureSuccessStatusCode().Dispose();
        (await api.PostJsonAsync("/api/v1/prices", new CreatePriceRequest { Product = "credits", Key = "credits-1000", Currency = "TRY", Type = PriceType.OneTime, UnitAmount = 29_900, TaxRate = "kdv-20" }))
            .EnsureSuccessStatusCode().Dispose();
        using var created = await api.PostJsonAsync("/api/v1/checkout-sessions", new CreateCheckoutSessionRequest
        {
            Mode = CheckoutMode.Payment,
            Items = [new CheckoutItemRequest { Price = "credits-1000", Quantity = 2 }],
            Locale = "tr",
            SuccessUrl = "https://shop.example.com/done",
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var session = await created.ReadAsync<CheckoutSessionResponse>();

        using var browser = Open(host);
        using var submitted = await SubmitAsync(browser, session.Url!, Form("ayse@example.com", "Ayşe Yılmaz", ["pre-information", "distance-sales"]));
        using var returned = await PayOnTheEmbedAsync(host, browser, submitted, "4242424242424242");
        returned.StatusCode.ShouldBe(HttpStatusCode.OK);
        return session.Id;
    }

    private static async Task<T> QueryAsync<T>(WebApplicationFactory<Program> host, Func<TahsilDbContext, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TahsilDbContext>());
    }
}
