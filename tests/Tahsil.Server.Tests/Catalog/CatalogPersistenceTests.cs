using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Domain.Catalog;
using Tahsil.Infrastructure.Persistence;

namespace Tahsil.Server.Tests.Catalog;

[Trait("Requirement", "DOM-4")]
public sealed class CatalogPersistenceTests(TahsilServerFixture server)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ACatalog_RoundTrips_WithItsTiersMetadataAndEnums()
    {
        var now = FakeBillingClock.Start;
        await using var host = IsolatedHosts.Create(server, server.NewDatabase("catalog"), new FakeTimeProvider(now));
        var tiers = new PriceTier[] { new(1000, 10, null, null), new(null, null, 0.125m, 500) };
        Guid priceId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<TahsilDbContext>();
            var taxRate = TaxRate.Create("kdv-20", "KDV", null, 20m, isInclusive: true, "TR", now).Value;
            var meter = Meter.Create("active_users", "Active users", MeterAggregation.UniqueCount, "user_id", now).Value;
            var product = Product.Create("pro", "Pro", null, "pro", new Dictionary<string, string> { ["team"] = "growth" }, now).Value;
            var price = Price.Create(
                new PriceDraft
                {
                    Key = "pro-usage-try",
                    Currency = "TRY",
                    Type = PriceType.Recurring,
                    Interval = PriceInterval.Month,
                    IntervalCount = 1,
                    UsageType = UsageType.Metered,
                    BillingScheme = BillingScheme.Tiered,
                    TiersMode = TiersMode.Graduated,
                    Tiers = tiers,
                },
                product,
                taxRate,
                meter,
                now).Value;
            context.AddRange(taxRate, meter, product, price);
            await context.SaveChangesAsync(CancellationToken);
            priceId = price.Id;
        }

        await using var read = host.Services.CreateAsyncScope();
        var stored = await read.ServiceProvider.GetRequiredService<TahsilDbContext>().Prices.SingleAsync(price => price.Id == priceId, CancellationToken);
        var columns = await host.QueryAsync<string>($"""
            SELECT p.type || ' ' || p.usage_type || ' ' || p.billing_scheme || ' ' || p.tiers_mode || ' ' || m.aggregation || ' ' || (p.tiers->1->>'unitAmountDecimal') || ' ' || p.metadata::text || ' ' || pr.metadata::text AS "Value"
            FROM tahsil.prices p JOIN tahsil.meters m ON m.id = p.meter_id JOIN tahsil.products pr ON pr.id = p.product_id
            """);

        stored.Tiers.ShouldBe(tiers);
        stored.Version.ShouldNotBe(0u);
        columns.ShouldBe(["recurring metered tiered graduated unique_count 0.125 {} {\"team\": \"growth\"}"]);
    }
}
