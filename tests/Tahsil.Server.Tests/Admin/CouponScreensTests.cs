using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Tahsil.Admin.Components.Pages.Coupons;
using Tahsil.Admin.Security;
using Tahsil.Contracts;
using CouponsPage = Tahsil.Admin.Components.Pages.Coupons.Coupons;

namespace Tahsil.Server.Tests.Admin;

/// <summary>The admin panel's coupon screens (ADM §2): coupons with their redemptions, and their promotion codes.</summary>
[Trait("Requirement", "ADM-1")]
public sealed class CouponScreensTests(TahsilServerFixture server)
{
    [Fact]
    [Trait("Requirement", "CPN-1")]
    public async Task ACoupon_IsCreatedAndListed_WithItsDiscount()
    {
        await using var host = Host();
        await using var admin = new AdminComponents(host);
        var page = admin.Render<CouponsPage>();

        page.WaitForElement("#create-coupon").Click();
        admin.Fill("Key", "launch");
        admin.Fill("Name shown to customers", "Launch discount");
        admin.Fill("Percentage", "50");
        admin.Fill("Maximum redemptions", "500");
        admin.Confirm("Create");

        admin.WaitForNotification("The coupon was created.");
        page.WaitForAssertion(() => admin.Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("/admin/coupons/launch"));
        var list = admin.Render<CouponsPage>();
        list.WaitForAssertion(() => list.Find("#coupons tbody tr").TextContent.ShouldContain("50% off once"));
        list.Find("#coupons tbody tr").TextContent.ShouldContain("0 of 500");
    }

    [Fact]
    [Trait("Requirement", "CPN-4")]
    [Trait("Requirement", "CPN-6")]
    public async Task ACodeIsAdded_InUppercase_AndDeactivated()
    {
        await using var host = Host();
        using (var client = await host.CreateClientWithAsync(TahsilPermissions.CouponsWrite))
        using (var created = await client.PostJsonAsync("/api/v1/coupons", new CreateCouponRequest { Key = "welcome", Name = "Welcome", AmountOff = 10_000, Currency = "TRY", Duration = CouponDuration.Forever }))
        {
            created.EnsureSuccessStatusCode();
        }

        await using var admin = new AdminComponents(host);
        var page = admin.Render<CouponDetail>(parameters => parameters.Add(detail => detail.Key, "welcome"));
        page.WaitForAssertion(() => page.Find("#discount").TextContent.ShouldBe("100.00 TRY off forever"));

        page.Find("#create-code").Click();
        admin.Fill("Code", "welcome100");
        admin.Fill("Minimum subtotal", "299");
        admin.Confirm("Create");
        admin.WaitForNotification("The code was created.");
        page.WaitForAssertion(() => page.Find("#codes tbody tr").TextContent.ShouldContain("WELCOME100"));
        page.Find("#codes tbody tr").TextContent.ShouldContain("at least 299.00 TRY");
        page.Find("button.toggle-code").Click();

        admin.WaitForNotification("The code was deactivated.");
        page.WaitForAssertion(() => page.Find("#codes tbody tr").TextContent.ShouldContain("Inactive"));
    }

    [Fact]
    [Trait("Requirement", "ADM-4")]
    public async Task Viewers_SeeCoupons_WithoutTheirActions()
    {
        await using var host = Host();
        await using var viewer = new AdminComponents(host, AdminRoles.Viewer);

        var page = viewer.Render<CouponsPage>();

        page.WaitForAssertion(() => page.Find("h1").TextContent.Trim().ShouldBe("Coupons"));
        page.FindAll("#create-coupon").ShouldBeEmpty();
    }

    private WebApplicationFactory<Program> Host() =>
        server.WithSettings(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Tahsil"] = server.NewDatabase("admin_coupons"),
            ["Tahsil:Workers:Enabled"] = "false",
        });
}
