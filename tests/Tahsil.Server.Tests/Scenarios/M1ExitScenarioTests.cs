using System.Net;
using System.Reflection;
using Bunit;
using MudBlazor;
using Tahsil.Contracts;
using Tahsil.Server.Tests.Admin;
using ApiKeysPage = Tahsil.Admin.Components.Pages.Integrations.ApiKeys;
using CustomersPage = Tahsil.Admin.Components.Pages.Customers.Customers;

namespace Tahsil.Server.Tests.Scenarios;

/// <summary>
/// M1's exit criteria: a developer provisions a catalog from a file, creates an API key in the admin panel, creates
/// customers through the API with it, and sees everything in the admin panel.
/// </summary>
public sealed class M1ExitScenarioTests(TahsilServerFixture server)
{
    private static readonly string ProjectDirectory = typeof(M1ExitScenarioTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(metadata => metadata.Key == "ProjectDirectory").Value!;

    [Fact]
    public async Task ACatalogFromAFile_AKeyFromThePanel_CustomersFromTheApi_AllInThePanel()
    {
        await using var host = server.WithSettings(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Tahsil"] = server.NewDatabase("m1exit"),
            ["Tahsil:Provisioning:FilePath"] = Path.Combine(ProjectDirectory, "Provisioning", "example.json"),
        });

        // An administrator creates the application's key in the panel and copies its secret.
        await using var admin = new AdminComponents(host);
        var keys = admin.Render<ApiKeysPage>();
        keys.WaitForElement("#create-api-key").Click();
        admin.Fill("Name", "Shop");
        foreach (var permission in new[] { TahsilPermissions.CatalogRead, TahsilPermissions.CustomersRead, TahsilPermissions.CustomersWrite })
        {
            admin.Dialogs.WaitForAssertion(() => admin.Dialogs.FindComponents<MudCheckBox<bool>>().ShouldNotBeEmpty());
            var box = admin.Dialogs.FindComponents<MudCheckBox<bool>>().Single(candidate => candidate.Instance.Label == permission);
            await box.InvokeAsync(() => box.Instance.ValueChanged.InvokeAsync(true));
        }

        admin.Confirm("Create");
        admin.Dialogs.WaitForAssertion(() => admin.Dialogs.Find(".secret input").GetAttribute("value")!.ShouldStartWith("ths_test_"));
        var secret = admin.Dialogs.Find(".secret input").GetAttribute("value")!;
        admin.Confirm("Done");

        // The application reads the provisioned catalog and syncs its customers with the key.
        using var shop = host.CreateClient(secret);
        (await (await shop.GetPathAsync("/api/v1/prices/pro-monthly-try")).ReadAsync<PriceResponse>()).UnitAmount.ShouldBe(29900);
        foreach (var (externalId, email) in new[] { ("user-1", "ayse@example.com"), ("user-2", "ahmet@example.com") })
        {
            using var created = await shop.PutAsync(
                new Uri($"/api/v1/customers/by-external-id/{externalId}", UriKind.Relative),
                System.Net.Http.Json.JsonContent.Create(new { email }, options: ApiClients.Json),
                Xunit.TestContext.Current.CancellationToken);
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var found = await (await shop.GetPathAsync("/api/v1/customers?q=AYSE")).ReadAsync<Contracts.Page<CustomerResponse>>();
        found.Items.ShouldHaveSingleItem().ExternalId.ShouldBe("user-1");

        // The administrator sees the customers, and the trail names the key as their creator.
        var customers = admin.Render<CustomersPage>();
        customers.WaitForAssertion(() => customers.FindAll("#customers tbody tr").Count.ShouldBe(2));
        var trail = await host.QueryAsync<string>($"SELECT actor_type || ':' || action AS \"Value\" FROM tahsil.audit_events WHERE action = 'customer.created'");
        trail.ShouldBe(["api_key:customer.created", "api_key:customer.created"]);
    }
}
