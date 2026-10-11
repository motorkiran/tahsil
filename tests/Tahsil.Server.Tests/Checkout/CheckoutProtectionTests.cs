using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Application.Checkout;
using Tahsil.Contracts;
using Tahsil.Domain.Common;
using Tahsil.Infrastructure.Persistence;
using static Tahsil.Server.Tests.Checkout.CheckoutBrowser;
using PaymentFailure = Tahsil.Domain.Payments.PaymentFailure;

namespace Tahsil.Server.Tests.Checkout;

/// <summary>Card testing protection (CHK-21, CHK-22): attempt limits, decline spikes, and pausing a connection's checkouts.</summary>
public sealed class CheckoutProtectionTests(TahsilServerFixture server)
{
    private static readonly string[] Permissions =
    [
        TahsilPermissions.CheckoutWrite, TahsilPermissions.CatalogWrite, TahsilPermissions.ConnectionsWrite, TahsilPermissions.CustomersWrite,
    ];

    private static readonly string[] Documents = ["pre-information", "distance-sales"];

    private readonly FakeTimeProvider _time = new(FakeBillingClock.Start);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [Trait("Requirement", "CHK-21")]
    [Trait("Requirement", "SEC-6")]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::/64")]
    public void Networks_AreAddresses_OrIPv6Slash64s(string address, string network) =>
        CheckoutProtection.NetworkOf(IPAddress.Parse(address)).ShouldBe(network);

    [Fact]
    [Trait("Requirement", "CHK-21")]
    public async Task ASession_AllowsItsAttempts_ThenRefusesMore()
    {
        await using var host = Host(new() { ["Tahsil:Checkout:MaxPaymentAttempts"] = "2" });
        var session = await SessionAsync(host);
        using var browser = Open(host);

        using var first = await SubmitAsync(browser, session.Url!, Form("ayse@example.com", "Ayşe", Documents));
        using var second = await SubmitAsync(browser, session.Url!, Form("ayse@example.com", "Ayşe", Documents));
        using var third = await SubmitAsync(browser, session.Url!, Form("ayse@example.com", "Ayşe", Documents));

        (first.StatusCode, second.StatusCode, third.StatusCode).ShouldBe((HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests));
        (await ParseAsync(third)).QuerySelector("#checkout-error")!.TextContent.ShouldBe("Çok fazla ödeme denemesi yapıldı. Lütfen daha sonra tekrar deneyin.");
        (await QueryAsync(host, context => context.Payments.CountAsync(CancellationToken))).ShouldBe(2, "a refused attempt never reaches the provider");
    }

    [Fact]
    [Trait("Requirement", "CHK-21")]
    public async Task ANetwork_IsLimitedAcrossSessions_ForAnHour()
    {
        await using var host = Host(new() { ["Tahsil:Checkout:MaxAttemptsPerAddressPerHour"] = "2" }, IPAddress.Parse("2001:db8:1:2::99"));
        using var browser = Open(host);

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var response = await SubmitAsync(browser, (await SessionAsync(host, $"order-{attempt}")).Url!, Form($"buyer{attempt}@example.com", "Buyer", Documents));
            statuses.Add(response.StatusCode);
        }

        _time.Advance(TimeSpan.FromHours(1));
        using var later = await SubmitAsync(browser, (await SessionAsync(host, "order-later")).Url!, Form("later@example.com", "Buyer", Documents));

        statuses.ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
        later.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Requirement", "CHK-21")]
    public async Task ACustomer_IsLimitedInFailedAttemptsPerDay()
    {
        await using var host = Host(new() { ["Tahsil:Checkout:MaxFailedAttemptsPerCustomerPerDay"] = "2" });
        using var api = await host.CreateClientWithAsync(Permissions);
        using var customer = await api.PostJsonAsync("/api/v1/customers", new CreateCustomerRequest { Email = "ayse@example.com", Name = "Ayşe" });
        var customerId = (await customer.ReadAsync<CustomerResponse>()).Id;
        using var browser = Open(host);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var declined = await SessionAsync(host, $"declined-{attempt}", customerId);
            using var submitted = await SubmitAsync(browser, declined.Url!, Form("ayse@example.com", "Ayşe", Documents));
            (await PayOnTheEmbedAsync(host, browser, submitted, "4000000000000002")).Dispose();
        }

        using var refused = await SubmitAsync(browser, (await SessionAsync(host, "third", customerId)).Url!, Form("ayse@example.com", "Ayşe", Documents));

        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    [Trait("Requirement", "CHK-22")]
    public async Task ADeclineSpike_NeedsAnOperator_WhoCanPauseCheckouts()
    {
        await using var host = Host();
        var session = await SessionAsync(host);
        using var browser = Open(host);
        using var submitted = await SubmitAsync(browser, session.Url!, Form("ayse@example.com", "Ayşe", Documents));
        await QueryAsync(host, async context =>
        {
            // The last 15 minutes on the connection: 10 failed and 9 succeeded checkout payments, before the 20th.
            var pending = await context.Payments.SingleAsync(CancellationToken);
            var now = FakeBillingClock.Start;
            for (var index = 0; index < 19; index++)
            {
                var payment = Domain.Payments.Payment.ForCustomer(
                    pending.CustomerId, null, session.Id, pending.ConnectionId!.Value, 1_000, "TRY", Domain.Payments.PaymentPurpose.Charge, false, null, now.AddMinutes(30), Actor.System, now);
                if (index < 10)
                {
                    payment.Fail(new PaymentFailure("card_declined", "new_payment_method", "Declined."), null, null, now);
                }
                else
                {
                    payment.Succeed($"sim-{index}", new Dictionary<string, string>(), null, now);
                }

                context.Payments.Add(payment);
            }

            return await context.SaveChangesAsync(CancellationToken);
        });

        (await PayOnTheEmbedAsync(host, browser, submitted, "4000000000000002")).Dispose();
        var openBefore = await SessionAsync(host, "opened-before-the-pause");
        using var api = await host.CreateClientWithAsync(Permissions);
        using var paused = await PatchAsync(api, "/api/v1/provider-connections/simulator", new UpdateConnectionRequest { CheckoutsPaused = true });
        using var refusedPage = await SubmitAsync(browser, openBefore.Url!, Form("ayse@example.com", "Ayşe", Documents));
        using var refusedApi = await api.PostJsonAsync("/api/v1/checkout-sessions", Payment("after-spike-api"));

        (await host.QueryAsync<string>($"SELECT kind AS \"Value\" FROM tahsil.attention_items WHERE subject_type = 'connection'")).ShouldBe(["decline_spike"]);
        (await paused.ReadAsync<ConnectionResponse>()).CheckoutsPaused.ShouldBeTrue();
        (await ParseAsync(refusedPage)).QuerySelector("#checkout-error")!.TextContent.ShouldBe("Ödemeler şu an için durduruldu. Lütfen daha sonra tekrar deneyin.");
        (refusedApi.StatusCode, await refusedApi.ProblemCodeAsync()).ShouldBe((HttpStatusCode.Conflict, "checkout.paused"));
    }

    private WebApplicationFactory<Program> Host(Dictionary<string, string?>? settings = null, IPAddress? clientAddress = null)
    {
        var host = IsolatedHosts.Create(server, server.NewDatabase("protection"), _time, services =>
        {
            if (clientAddress is not null)
            {
                services.AddSingleton<IStartupFilter>(new ClientAddress(clientAddress));
            }
        });
        return settings is null ? host : host.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings)));
    }

    /// <summary>A payment session of a ₺299.00 price, on a simulator that notifies only when told; the catalog is created once per host.</summary>
    private static async Task<CheckoutSessionResponse> SessionAsync(WebApplicationFactory<Program> host, string reference = "order", Guid? customer = null)
    {
        using var api = await host.CreateClientWithAsync(Permissions);
        if (!await QueryAsync(host, context => context.Prices.AnyAsync(price => price.Key == "credits-1000", CancellationToken)))
        {
            (await PatchAsync(api, "/api/v1/provider-connections/simulator", new { settings = new { notifications = false } })).Dispose();
            (await api.PostJsonAsync("/api/v1/products", new CreateProductRequest { Key = "credits", Name = "1,000 credits" })).EnsureSuccessStatusCode().Dispose();
            (await api.PostJsonAsync("/api/v1/prices", new CreatePriceRequest { Product = "credits", Key = "credits-1000", Currency = "TRY", Type = PriceType.OneTime, UnitAmount = 29_900 }))
                .EnsureSuccessStatusCode().Dispose();
        }

        using var created = await api.PostJsonAsync("/api/v1/checkout-sessions", Payment(reference) with { Customer = customer });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(CancellationToken));
        return await created.ReadAsync<CheckoutSessionResponse>();
    }

    private static CreateCheckoutSessionRequest Payment(string reference) => new()
    {
        Mode = CheckoutMode.Payment,
        Items = [new CheckoutItemRequest { Price = "credits-1000" }],
        Locale = "tr",
        ClientReferenceId = reference,
        SuccessUrl = "https://shop.example.com/done",
    };

    private static async Task<HttpResponseMessage> PatchAsync<T>(HttpClient client, string path, T body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body, options: ApiClients.Json) };
        var response = await client.SendAsync(request, CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return response;
    }

    private static async Task<T> QueryAsync<T>(WebApplicationFactory<Program> host, Func<TahsilDbContext, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TahsilDbContext>());
    }

    /// <summary>Gives every request of the in-memory server one client address, as a reverse proxy would report it.</summary>
    private sealed class ClientAddress(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = address;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
