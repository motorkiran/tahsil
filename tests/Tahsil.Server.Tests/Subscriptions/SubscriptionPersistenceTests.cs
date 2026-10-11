using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Domain.Catalog;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Subscriptions;
using Tahsil.Infrastructure.Persistence;
using TahsilPermissions = Tahsil.Contracts.TahsilPermissions;

namespace Tahsil.Server.Tests.Subscriptions;

public sealed class SubscriptionPersistenceTests(TahsilServerFixture server)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "DOM-11")]
    [Trait("Requirement", "ENG-10")]
    public async Task ASubscription_RoundTripsWithItsItems_AndKeepsItsProductsFromDeletion()
    {
        var now = FakeBillingClock.Start;
        await using var host = IsolatedHosts.Create(server, server.NewDatabase("subscriptions"), new FakeTimeProvider(now));
        Guid id;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<TahsilDbContext>();
            var customer = Customer.Create("user-42", new CustomerDetails { Email = "ayse@example.com", Locale = "tr" }, (_, _) => [], now).Value;
            var product = Product.Create("team", "Team", null, null, null, now).Value;
            var price = Price.Create(
                new PriceDraft { Key = "team-monthly-try", Currency = "TRY", Type = PriceType.Recurring, Interval = PriceInterval.Month, IntervalCount = 1, UsageType = UsageType.Licensed, UnitAmount = 30_000 },
                product,
                null,
                null,
                now).Value;
            var subscription = Subscription.Create(
                customer.Id,
                [new SubscriptionItemSpec(price, product, 5)],
                new SubscriptionTerms { TrialEnd = now.AddDays(14), TrialEndBehavior = TrialEndBehavior.CreateInvoice, Metadata = new Dictionary<string, string> { ["plan_source"] = "sales" } },
                TimeZoneInfo.Utc,
                TimeSpan.FromHours(23),
                now).Value;
            subscription.ScheduleCancellation(now.AddDays(14), CancellationReason.RequestedByCustomer, CancellationFeedback.TooExpensive, null, now).IsSuccess.ShouldBeTrue();
            context.AddRange(customer, product, price, subscription);
            await context.SaveChangesAsync(CancellationToken);
            id = subscription.Id;
        }

        await using (var read = host.Services.CreateAsyncScope())
        {
            var stored = await read.ServiceProvider.GetRequiredService<TahsilDbContext>().Subscriptions.Include(subscription => subscription.Items)
                .SingleAsync(subscription => subscription.Id == id, CancellationToken);
            (stored.Status, stored.TrialEnd, stored.NextCycleAt, stored.Metadata["plan_source"]).ShouldBe((SubscriptionStatus.Trialing, now.AddDays(14), now.AddDays(14), "sales"));
            stored.Items.ShouldHaveSingleItem().Quantity.ShouldBe(5);
            stored.Version.ShouldNotBe(0u);
        }

        var columns = await host.QueryAsync<string>($"""
            SELECT status || ' ' || interval || ' ' || collection_method || ' ' || trial_end_behavior || ' ' || cancellation_reason || ' ' || cancellation_feedback AS "Value"
            FROM tahsil.subscriptions
            """);
        columns.ShouldBe(["trialing month charge_automatically create_invoice requested_by_customer too_expensive"]);

        using var app = await host.CreateClientWithAsync(TahsilPermissions.CatalogWrite);
        using var deleted = await app.DeleteAsync(new Uri("/api/v1/products/team", UriKind.Relative), CancellationToken);
        deleted.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await deleted.ProblemCodeAsync()).ShouldBe("product.in_use");
    }
}
