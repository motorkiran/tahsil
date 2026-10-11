using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Tahsil.Contracts;

namespace Tahsil.Server.Tests.Coupons;

/// <summary>Coupons and promotion codes through the API (CPN §8).</summary>
public sealed class CouponApiTests(TahsilServerFixture server)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "CPN-1")]
    [Trait("Requirement", "API-4")]
    public async Task ACoupon_IsCreatedForProducts_AndFoundByIdOrKey()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var product = await ProductAsync(client, "pro");

        var created = await CreatedAsync<CouponResponse>(client, "/api/v1/coupons", new CreateCouponRequest
        {
            Key = "launch",
            Name = "Launch discount",
            PercentOff = 50,
            Duration = CouponDuration.Repeating,
            DurationInCycles = 3,
            Products = ["pro"],
            MaxRedemptions = 500,
        });

        created.Products.ShouldBe([new ProductSummary(product, "pro", "Pro")]);
        (created.PercentOff, created.AmountOff, created.Duration, created.DurationInCycles, created.TimesRedeemed).ShouldBe((50m, null, CouponDuration.Repeating, 3, 0));
        (await GetAsync<CouponResponse>(client, $"/api/v1/coupons/{created.Id}")).Key.ShouldBe("launch");
        (await GetAsync<CouponResponse>(client, "/api/v1/coupons/launch")).Id.ShouldBe(created.Id);
        (await GetAsync<Page<CouponResponse>>(client, "/api/v1/coupons?archived=false")).Items.ShouldHaveSingleItem().Id.ShouldBe(created.Id);
        (await host.QueryAsync<string>($"SELECT action AS \"Value\" FROM tahsil.audit_events WHERE subject_type = 'coupon'")).ShouldBe(["coupon.created"]);
    }

    [Theory]
    [Trait("Requirement", "CPN-1")]
    [InlineData("""{ "key": "taken", "name": "Again", "percentOff": 10, "duration": "once" }""", HttpStatusCode.Conflict, "coupon.key_taken")]
    [InlineData("""{ "key": "both", "name": "Both", "percentOff": 10, "amountOff": 1000, "currency": "TRY", "duration": "once" }""", HttpStatusCode.BadRequest, "coupon.invalid")]
    [InlineData("""{ "key": "cycles", "name": "Cycles", "percentOff": 10, "duration": "repeating" }""", HttpStatusCode.BadRequest, "coupon.invalid")]
    [InlineData("""{ "key": "nowhere", "name": "Nowhere", "percentOff": 10, "duration": "once", "products": ["missing"] }""", HttpStatusCode.BadRequest, "coupon.invalid")]
    [InlineData("""{ "key": "Bad Key", "name": "Bad", "percentOff": 10, "duration": "once" }""", HttpStatusCode.BadRequest, "catalog.invalid_key")]
    public async Task AnInvalidCoupon_IsRefusedWithItsReason(string body, HttpStatusCode status, string code)
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        await CreatedAsync<CouponResponse>(client, "/api/v1/coupons", new CreateCouponRequest { Key = "taken", Name = "Taken", AmountOff = 10_000, Currency = "TRY", Duration = CouponDuration.Forever });

        using var response = await client.PostAsync(
            new Uri("/api/v1/coupons", UriKind.Relative), new StringContent(body, System.Text.Encoding.UTF8, "application/json"), CancellationToken);

        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(CancellationToken));
        (await response.ProblemCodeAsync()).ShouldBe(code);
    }

    [Fact]
    [Trait("Requirement", "CPN-2")]
    [Trait("Requirement", "API-5")]
    public async Task ACoupon_ChangesItsNameAndLimits_ButNeverItsDiscount()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var coupon = await CreatedAsync<CouponResponse>(client, "/api/v1/coupons", new CreateCouponRequest { Key = "launch", Name = "Launch", PercentOff = 20, Duration = CouponDuration.Once });
        await host.ExecuteSqlAsync($"UPDATE tahsil.coupons SET times_redeemed = 5 WHERE id = {coupon.Id}");
        using var current = await client.GetPathAsync("/api/v1/coupons/launch");

        using var discount = await client.PatchJsonAsync("/api/v1/coupons/launch", new { percentOff = 30 });
        using var belowRedeemed = await client.PatchJsonAsync("/api/v1/coupons/launch", new { maxRedemptions = 4 });
        using var renamed = await client.PatchJsonAsync("/api/v1/coupons/launch", new { name = "Spring", maxRedemptions = 5, isArchived = true }, current.Headers.ETag!.Tag);
        using var stale = await client.PatchJsonAsync("/api/v1/coupons/launch", new { name = "Summer" }, current.Headers.ETag.Tag);

        (discount.StatusCode, await discount.ProblemCodeAsync()).ShouldBe((HttpStatusCode.BadRequest, "coupon.immutable"));
        (belowRedeemed.StatusCode, await belowRedeemed.ProblemCodeAsync()).ShouldBe((HttpStatusCode.BadRequest, "coupon.invalid"));
        renamed.StatusCode.ShouldBe(HttpStatusCode.OK, await renamed.Content.ReadAsStringAsync(CancellationToken));
        var updated = await renamed.ReadAsync<CouponResponse>();
        (updated.Name, updated.MaxRedemptions, updated.IsArchived, updated.PercentOff).ShouldBe(("Spring", 5, true, 20m));
        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    [Trait("Requirement", "CPN-4")]
    [Trait("Requirement", "CPN-5")]
    public async Task Codes_AreStoredInUppercase_UniqueAndFoundIgnoringCase()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        await CreatedAsync<CouponResponse>(client, "/api/v1/coupons", new CreateCouponRequest { Key = "launch", Name = "Launch", PercentOff = 50, Duration = CouponDuration.Once });
        await CreatedAsync<CouponResponse>(client, "/api/v1/coupons", new CreateCouponRequest { Key = "spring", Name = "Spring", PercentOff = 10, Duration = CouponDuration.Once });
        var customer = await CustomerAsync(client);

        var code = await CreatedAsync<PromotionCodeResponse>(client, "/api/v1/promotion-codes", new CreatePromotionCodeRequest
        {
            Coupon = "launch",
            Code = "launch50",
            MaxRedemptions = 100,
            FirstTimeOnly = true,
            MinimumAmount = 50_000,
            MinimumAmountCurrency = "TRY",
            Customer = customer,
        });
        using var sameInAnotherCase = await client.PostJsonAsync("/api/v1/promotion-codes", new CreatePromotionCodeRequest { Coupon = "spring", Code = "Launch50" });
        using var unknownCoupon = await client.PostJsonAsync("/api/v1/promotion-codes", new CreatePromotionCodeRequest { Coupon = "summer", Code = "SUMMER" });
        using var badCode = await client.PostJsonAsync("/api/v1/promotion-codes", new CreatePromotionCodeRequest { Coupon = "spring", Code = "no spaces" });

        (code.Code, code.Coupon.Key, code.IsActive, code.FirstTimeOnly, code.MinimumAmount, code.CustomerId).ShouldBe(("LAUNCH50", "launch", true, true, 50_000L, customer));
        (sameInAnotherCase.StatusCode, await sameInAnotherCase.ProblemCodeAsync()).ShouldBe((HttpStatusCode.Conflict, "promotion_code.code_taken"));
        (unknownCoupon.StatusCode, await unknownCoupon.ProblemCodeAsync()).ShouldBe((HttpStatusCode.NotFound, "coupon.not_found"));
        (badCode.StatusCode, await badCode.ProblemCodeAsync()).ShouldBe((HttpStatusCode.BadRequest, "promotion_code.invalid"));
        (await GetAsync<Page<PromotionCodeResponse>>(client, "/api/v1/promotion-codes?code=LaUnCh50")).Items.ShouldHaveSingleItem().Id.ShouldBe(code.Id);
        (await GetAsync<Page<PromotionCodeResponse>>(client, "/api/v1/promotion-codes?coupon=spring")).Items.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Requirement", "CPN-6")]
    public async Task ACode_IsDeactivatedAndReactivated_AndNothingElseChanges()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        await CreatedAsync<CouponResponse>(client, "/api/v1/coupons", new CreateCouponRequest { Key = "launch", Name = "Launch", PercentOff = 50, Duration = CouponDuration.Once });
        var code = await CreatedAsync<PromotionCodeResponse>(client, "/api/v1/promotion-codes", new CreatePromotionCodeRequest { Coupon = "launch", Code = "LAUNCH50" });

        using var deactivated = await client.PatchJsonAsync($"/api/v1/promotion-codes/{code.Id}", new { isActive = false });
        var inactive = await GetAsync<Page<PromotionCodeResponse>>(client, "/api/v1/promotion-codes?active=false");
        using var limit = await client.PatchJsonAsync($"/api/v1/promotion-codes/{code.Id}", new { maxRedemptions = 10 });
        using var reactivated = await client.PatchJsonAsync($"/api/v1/promotion-codes/{code.Id}", new { isActive = true });

        (await deactivated.ReadAsync<PromotionCodeResponse>()).IsActive.ShouldBeFalse();
        inactive.Items.ShouldHaveSingleItem().Id.ShouldBe(code.Id);
        (limit.StatusCode, await limit.ProblemCodeAsync()).ShouldBe((HttpStatusCode.BadRequest, "promotion_code.invalid"));
        (await reactivated.ReadAsync<PromotionCodeResponse>()).IsActive.ShouldBeTrue();
        (await host.QueryAsync<string>($"SELECT action AS \"Value\" FROM tahsil.audit_events WHERE subject_type = 'promotion_code' ORDER BY occurred_at, id"))
            .ShouldBe(["promotion_code.created", "promotion_code.updated", "promotion_code.updated"]);
    }

    private static async Task<Guid> ProductAsync(HttpClient client, string key) =>
        (await CreatedAsync<ProductResponse>(client, "/api/v1/products", new CreateProductRequest { Key = key, Name = "Pro" })).Id;

    private static async Task<Guid> CustomerAsync(HttpClient client) =>
        (await CreatedAsync<CustomerResponse>(client, "/api/v1/customers", new CreateCustomerRequest { Name = "Deniz Kaya", Email = "deniz@example.com" })).Id;

    private static async Task<T> CreatedAsync<T>(HttpClient client, string path, object body)
    {
        using var response = await client.PostJsonAsync(path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<T>();
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string path)
    {
        using var response = await client.GetPathAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<T>();
    }

    private static Task<HttpClient> ClientAsync(WebApplicationFactory<Program> host) => host.CreateClientWithAsync(
        TahsilPermissions.CouponsRead, TahsilPermissions.CouponsWrite, TahsilPermissions.CatalogWrite, TahsilPermissions.CustomersWrite);

    private WebApplicationFactory<Program> Host() =>
        server.WithSettings(new Dictionary<string, string?> { ["ConnectionStrings:Tahsil"] = server.NewDatabase("coupons") });
}
