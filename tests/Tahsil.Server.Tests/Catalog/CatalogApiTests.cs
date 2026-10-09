using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Tahsil.Contracts;

namespace Tahsil.Server.Tests.Catalog;

public sealed class CatalogApiTests(TahsilServerFixture server)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "CAT-8")]
    [Trait("Requirement", "API-6")]
    public async Task TheExampleCatalog_IsCreatedThroughTheApi()
    {
        await using var host = Host();
        using var client = await CatalogClientAsync(host);

        var prices = await CreateExampleCatalogAsync(client);

        var monthly = prices[0];
        monthly.Product.ShouldBe(new ProductSummary(monthly.Product.Id, "pro", "Pro"));
        monthly.TaxRate.ShouldBe(new TaxRateSummary(monthly.TaxRate!.Id, "kdv-20", "KDV", 20m, true));
        (monthly.IntervalCount, monthly.UsageType, monthly.BillingScheme, monthly.TrialDays).ShouldBe((1, UsageType.Licensed, BillingScheme.PerUnit, 14));
        prices[2].Meter!.Key.ShouldBe("api_calls");
        prices[2].Tiers.ShouldBe([new PriceTier(10000, 0), new PriceTier(null, null, 0.5m)]);

        var listed = await (await client.GetPathAsync("/api/v1/prices?product=pro&type=recurring&currency=TRY")).ReadAsync<Page<PriceResponse>>();
        listed.Items.Select(price => price.Key).ShouldBe(["pro-monthly-try", "pro-yearly-try", "api-calls-try"]);
        (await (await client.GetPathAsync("/api/v1/products?q=PR")).ReadAsync<Page<ProductResponse>>()).Items.ShouldHaveSingleItem().Key.ShouldBe("pro");
    }

    [Fact]
    [Trait("Requirement", "API-4")]
    public async Task CatalogObjects_AreFoundByIdOrKey()
    {
        await using var host = Host();
        using var client = await CatalogClientAsync(host);
        var prices = await CreateExampleCatalogAsync(client);

        foreach (var path in new[] { $"/api/v1/prices/{prices[0].Id}", "/api/v1/prices/pro-monthly-try" })
        {
            (await (await client.GetPathAsync(path)).ReadAsync<PriceResponse>()).Id.ShouldBe(prices[0].Id);
        }

        (await (await client.GetPathAsync($"/api/v1/products/{prices[0].Product.Id}")).ReadAsync<ProductResponse>()).Key.ShouldBe("pro");
        (await (await client.GetPathAsync("/api/v1/tax-rates/kdv-20")).ReadAsync<TaxRateResponse>()).Percentage.ShouldBe(20m);
        (await (await client.GetPathAsync("/api/v1/meters/api_calls")).ReadAsync<MeterResponse>()).Aggregation.ShouldBe(MeterAggregation.Sum);
        using var missing = await client.GetPathAsync("/api/v1/prices/unknown");
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await missing.ProblemCodeAsync()).ShouldBe("price.not_found");
    }

    [Fact]
    [Trait("Requirement", "CAT-10")]
    public async Task ALookupKey_MovesToANewPrice_OnlyWhenAsked()
    {
        await using var host = Host();
        using var client = await CatalogClientAsync(host);
        var old = (await CreateExampleCatalogAsync(client))[0];
        var raise = MonthlyPrice() with { Key = "pro-monthly-try", UnitAmount = 34900 };

        using var taken = await client.PostJsonAsync("/api/v1/prices", raise);
        using var moved = await client.PostJsonAsync("/api/v1/prices", raise with { TransferKey = true });

        taken.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await taken.ProblemCodeAsync()).ShouldBe("price.key_taken");
        moved.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await (await client.GetPathAsync("/api/v1/prices/pro-monthly-try")).ReadAsync<PriceResponse>()).UnitAmount.ShouldBe(34900);
        (await (await client.GetPathAsync($"/api/v1/prices/{old.Id}")).ReadAsync<PriceResponse>()).Key.ShouldBeNull();
    }

    [Fact]
    [Trait("Requirement", "CAT-9")]
    public async Task APrice_ChangesOnlyItsKeyNameTrialArchiveAndMetadata()
    {
        await using var host = Host();
        using var client = await CatalogClientAsync(host);
        await CreateExampleCatalogAsync(client);

        using var amount = await PatchAsync(client, "/api/v1/prices/pro-monthly-try", new { unitAmount = 1 });
        using var unknown = await PatchAsync(client, "/api/v1/prices/pro-monthly-try", new { colour = "blue" });
        using var allowed = await PatchAsync(client, "/api/v1/prices/pro-monthly-try", new { name = "Aylık", trialDays = (int?)null, metadata = new { campaign = "autumn" } });
        var updated = await allowed.ReadAsync<PriceResponse>();

        amount.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await amount.ReadAsync<JsonNode>();
        problem["code"]!.GetValue<string>().ShouldBe("price.immutable");
        problem["errors"]!["unitAmount"].ShouldNotBeNull();
        (await unknown.ProblemCodeAsync()).ShouldBe("request.invalid");
        (updated.Name, updated.TrialDays, updated.UnitAmount, updated.Metadata["campaign"]).ShouldBe(("Aylık", null, 29900L, "autumn"));
    }

    [Fact]
    [Trait("Requirement", "CAT-6")]
    [Trait("Requirement", "CAT-11")]
    public async Task ArchivedProductsAndTaxRates_TakeNoNewPrices()
    {
        await using var host = Host();
        using var client = await CatalogClientAsync(host);
        await CreateExampleCatalogAsync(client);

        (await PatchAsync(client, "/api/v1/tax-rates/kdv-20", new { isArchived = true })).Dispose();
        using var withArchivedRate = await client.PostJsonAsync("/api/v1/prices", MonthlyPrice() with { Key = null });
        (await PatchAsync(client, "/api/v1/products/pro", new { isArchived = true })).Dispose();
        using var forArchivedProduct = await client.PostJsonAsync("/api/v1/prices", MonthlyPrice() with { Key = null, TaxRate = null });

        var rateProblem = await withArchivedRate.ReadAsync<JsonNode>();
        rateProblem["code"]!.GetValue<string>().ShouldBe("price.invalid_reference");
        rateProblem["errors"]!["taxRate"].ShouldNotBeNull();
        (await forArchivedProduct.ReadAsync<JsonNode>())["errors"]!["product"].ShouldNotBeNull();
        (await (await client.GetPathAsync("/api/v1/prices/pro-monthly-try")).ReadAsync<PriceResponse>()).IsArchived.ShouldBeFalse();
    }

    [Fact]
    [Trait("Requirement", "CAT-7")]
    public async Task AnUnusedProduct_IsDeletedWithItsPrices()
    {
        await using var host = Host();
        using var client = await CatalogClientAsync(host);
        await CreateExampleCatalogAsync(client);

        using var deleted = await client.DeleteAsync(new Uri("/api/v1/products/pro", UriKind.Relative), CancellationToken);

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetPathAsync("/api/v1/products/pro")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await (await client.GetPathAsync("/api/v1/prices")).ReadAsync<Page<PriceResponse>>()).Items.ShouldBeEmpty();
        (await client.GetPathAsync("/api/v1/tax-rates/kdv-20")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Requirement", "API-5")]
    [Trait("Requirement", "API-1")]
    public async Task Updates_AreMergePatches_GuardedByIfMatch()
    {
        await using var host = Host();
        using var client = await CatalogClientAsync(host);
        await CreateExampleCatalogAsync(client);
        using var read = await client.GetPathAsync("/api/v1/products/pro");
        var etag = read.Headers.ETag!;

        using var stale = await PatchAsync(client, "/api/v1/products/pro", new { name = "Pro+" }, "\"1\"");
        using var fresh = await PatchAsync(client, "/api/v1/products/pro", new { description = (string?)null }, etag.Tag);
        var updated = await fresh.ReadAsync<ProductResponse>();

        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await stale.ProblemCodeAsync()).ShouldBe("concurrency.precondition_failed");
        fresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        fresh.Headers.ETag.ShouldNotBe(etag);
        (updated.Name, updated.Description, updated.KimlikPlan).ShouldBe(("Pro", null, "pro"));
    }

    [Fact]
    [Trait("Requirement", "CAT-8")]
    public async Task InvalidRequests_NameTheProblem()
    {
        await using var host = Host();
        using var client = await CatalogClientAsync(host);
        await CreateExampleCatalogAsync(client);

        using var duplicate = await client.PostJsonAsync("/api/v1/products", new CreateProductRequest { Key = "pro", Name = "Pro again" });
        using var badKey = await client.PostJsonAsync("/api/v1/products", new CreateProductRequest { Key = "Pro Plan", Name = "Pro" });
        using var badTiers = await client.PostJsonAsync("/api/v1/prices", MonthlyPrice() with
        {
            Key = null,
            UnitAmount = null,
            BillingScheme = BillingScheme.Tiered,
            TiersMode = TiersMode.Volume,
            Tiers = [new PriceTier(100, 10)],
        });
        using var badRate = await client.PostJsonAsync("/api/v1/tax-rates", new CreateTaxRateRequest { Key = "odd", Name = "Odd", Percentage = 120, Inclusive = false });

        (await duplicate.ProblemCodeAsync()).ShouldBe("product.key_taken");
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await badKey.ProblemCodeAsync()).ShouldBe("catalog.invalid_key");
        var tiers = await badTiers.ReadAsync<JsonNode>();
        tiers["code"]!.GetValue<string>().ShouldBe("price.invalid");
        tiers["errors"]!["tiers"].ShouldNotBeNull();
        (await badRate.ProblemCodeAsync()).ShouldBe("tax_rate.invalid");
    }

    [Fact]
    [Trait("Requirement", "FND-27")]
    public async Task ReadingNeedsTheReadPermission_AndWritingTheWritePermission()
    {
        await using var host = Host();
        using var reader = await host.CreateClientWithAsync(TahsilPermissions.CatalogRead);

        using var list = await reader.GetPathAsync("/api/v1/products");
        using var create = await reader.PostJsonAsync("/api/v1/products", new CreateProductRequest { Key = "pro", Name = "Pro" });

        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        create.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>The catalog of the provisioning example, through the API; returns its prices.</summary>
    internal static async Task<List<PriceResponse>> CreateExampleCatalogAsync(HttpClient client)
    {
        await CreatedAsync<TaxRateResponse>(client, "/api/v1/tax-rates", new CreateTaxRateRequest { Key = "kdv-20", Name = "KDV", Percentage = 20, Inclusive = true, Country = "TR" });
        await CreatedAsync<MeterResponse>(client, "/api/v1/meters", new CreateMeterRequest { Key = "api_calls", Name = "API calls", Aggregation = MeterAggregation.Sum });
        await CreatedAsync<ProductResponse>(client, "/api/v1/products", new CreateProductRequest { Key = "pro", Name = "Pro", Description = "For growing teams", KimlikPlan = "pro" });

        return
        [
            await CreatedAsync<PriceResponse>(client, "/api/v1/prices", MonthlyPrice()),
            await CreatedAsync<PriceResponse>(client, "/api/v1/prices", new CreatePriceRequest
            {
                Product = "pro", Key = "pro-yearly-try", Name = "Yearly", Currency = "TRY", Type = PriceType.Recurring, Interval = PriceInterval.Year,
                UnitAmount = 299000, TaxRate = "kdv-20",
            }),
            await CreatedAsync<PriceResponse>(client, "/api/v1/prices", new CreatePriceRequest
            {
                Product = "pro", Key = "api-calls-try", Currency = "TRY", Type = PriceType.Recurring, Interval = PriceInterval.Month, UsageType = UsageType.Metered,
                Meter = "api_calls", BillingScheme = BillingScheme.Tiered, TiersMode = TiersMode.Graduated,
                Tiers = [new PriceTier(10000, 0), new PriceTier(null, null, 0.5m)],
            }),
        ];
    }

    internal static Task<HttpClient> CatalogClientAsync(WebApplicationFactory<Program> host) =>
        host.CreateClientWithAsync(TahsilPermissions.CatalogRead, TahsilPermissions.CatalogWrite);

    private static CreatePriceRequest MonthlyPrice() => new()
    {
        Product = "pro",
        Key = "pro-monthly-try",
        Name = "Monthly",
        Currency = "TRY",
        Type = PriceType.Recurring,
        Interval = PriceInterval.Month,
        UnitAmount = 29900,
        TaxRate = "kdv-20",
        TrialDays = 14,
    };

    private static async Task<T> CreatedAsync<T>(HttpClient client, string path, object body)
    {
        using var response = await client.PostJsonAsync(path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        response.Headers.ETag.ShouldNotBeNull();
        return await response.ReadAsync<T>();
    }

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, string path, object body, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body, options: ApiClients.Json) };
        if (ifMatch is not null)
        {
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(ifMatch));
        }

        return await client.SendAsync(request, CancellationToken);
    }

    private WebApplicationFactory<Program> Host() =>
        server.WithSettings(new Dictionary<string, string?> { ["ConnectionStrings:Tahsil"] = server.NewDatabase("catalogapi") });
}
