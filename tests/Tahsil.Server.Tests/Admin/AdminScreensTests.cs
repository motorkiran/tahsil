using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tahsil.Admin.Components;
using Tahsil.Admin.Components.Pages;
using Tahsil.Admin.Components.Pages.Catalog;
using Tahsil.Admin.Components.Pages.Customers;
using Tahsil.Admin.Security;
using Tahsil.Contracts;
using ApiKeysPage = Tahsil.Admin.Components.Pages.Integrations.ApiKeys;
using CustomersPage = Tahsil.Admin.Components.Pages.Customers.Customers;

namespace Tahsil.Server.Tests.Admin;

/// <summary>The admin panel's M1 screens: each renders, its changes land, and they are audited with the administrator as actor.</summary>
[Trait("Requirement", "ADM-1")]
public sealed class AdminScreensTests(TahsilServerFixture server)
{
    [Theory]
    [InlineData(typeof(Home), "Welcome")]
    [InlineData(typeof(CustomersPage), "Customers")]
    [InlineData(typeof(Products), "Products")]
    [InlineData(typeof(TaxRates), "Tax rates")]
    [InlineData(typeof(Meters), "Meters")]
    [InlineData(typeof(ApiKeysPage), "API keys")]
    [InlineData(typeof(AuditLog), "Audit log")]
    public async Task Screen_Renders(Type page, string title)
    {
        await using var admin = new AdminComponents(server);

        var rendered = admin.Render(builder =>
        {
            builder.OpenComponent(0, page);
            builder.CloseComponent();
        });

        rendered.WaitForAssertion(() => rendered.Find("h1").TextContent.Trim().ShouldBe(title));
    }

    [Fact]
    [Trait("Requirement", "ADM-4")]
    public async Task Viewers_CannotOpenAdminOnlyPages_AndDoNotSeeThem()
    {
        await using var viewer = new AdminComponents(server, AdminRoles.Viewer, withProviders: false);
        var navigation = viewer.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/admin/api-keys");
        var routes = viewer.Render<Routes>();

        routes.WaitForAssertion(() => routes.Markup.ShouldContain("Your role does not include this page."));
        routes.FindAll("h1").ShouldBeEmpty();
        routes.Find("nav").TextContent.ShouldNotContain("API keys");
        routes.Find("nav").TextContent.ShouldContain("Customers");
    }

    [Fact]
    [Trait("Requirement", "ADM-1")]
    [Trait("Requirement", "ADM-4")]
    public async Task Settings_ShowTheEffectiveConfiguration_WithoutItsSecrets_ToAdminsOnly()
    {
        await using var admin = new AdminComponents(server, withProviders: false);
        admin.Services.GetRequiredService<NavigationManager>().NavigateTo("/admin/settings");
        var routes = admin.Render<Routes>();
        routes.WaitForAssertion(() => routes.Find("h1").TextContent.Trim().ShouldBe("Settings"));

        string Value(string section, string key) => routes.Find($".settings-section[data-section='{section}'] tr[data-key='{key}'] td").TextContent.Trim();
        routes.Find("#mode").TextContent.ShouldBe("Test");
        Value("Tahsil:Server", "Mode").ShouldBe("Test");
        Value("Tahsil:Usage", "MaxBatchSize").ShouldBe("1000", "defaults are part of the effective configuration");
        Value("Tahsil:Usage", "RetentionPeriod").ShouldBe("400.00:00:00");
        Value("Tahsil:Security", "MasterKey").ShouldBe("•••••• (set)");
        Value("Tahsil:Security", "MasterKeyId").ShouldBe("1");
        Value("ConnectionStrings", "Tahsil").ShouldBe("•••••• (set)");
        routes.Markup.ShouldNotContain(Tahsil.Infrastructure.Security.SecurityOptions.DevelopmentMasterKey);
        routes.Markup.ShouldNotContain("Password=");

        await using var viewer = new AdminComponents(server, AdminRoles.Viewer, withProviders: false);
        viewer.Services.GetRequiredService<NavigationManager>().NavigateTo("/admin/settings");
        var refused = viewer.Render<Routes>();
        refused.WaitForAssertion(() => refused.Markup.ShouldContain("Your role does not include this page."));
        refused.Find("nav").TextContent.ShouldNotContain("Settings");
    }

    [Fact]
    [Trait("Requirement", "ADM-1")]
    [Trait("Requirement", "PRT-1")]
    public async Task ACustomersPortalLink_IsShownOnce_AndTheirTimelineTellsOfIt()
    {
        using var api = await server.CreateClientWithAsync(TahsilPermissions.CustomersWrite);
        using var created = await api.PostJsonAsync("/api/v1/customers", new CreateCustomerRequest { Name = "Ada Lovelace", Email = $"{Guid.CreateVersion7():N}@example.com" });
        var customer = (await created.ReadAsync<CustomerResponse>()).Id;
        await using var admin = new AdminComponents(server, AdminRoles.Operator);
        var page = admin.Render<CustomerDetail>(parameters => parameters.Add(detail => detail.Id, customer));

        page.WaitForElement("#portal-link").Click();
        admin.Dialogs.WaitForAssertion(() => admin.Dialogs.Find("#portal-return-url").GetAttribute("value").ShouldBe(TestConfiguration.PublicUrl));
        admin.Fill("Where the portal leads back to", "https://app.example.com/billing");
        admin.Confirm("Create link");

        admin.WaitForNotification("The portal link was created.");
        admin.Dialogs.WaitForAssertion(() => admin.Dialogs.Find(".secret input").GetAttribute("value")!.ShouldStartWith($"{TestConfiguration.PublicUrl}portal/ps_"));
        admin.Confirm("Done");
        page.WaitForAssertion(() => page.FindAll("#timeline tbody tr").Select(row => row.QuerySelector("code")!.TextContent).ShouldBe(["portal_session.created", "customer.created"]));
        page.Find("#timeline-all").GetAttribute("href").ShouldBe($"/admin/audit-log?customer={customer}");
        await AuditedByAsync(admin, "portal_session.created");
    }

    [Fact]
    [Trait("Requirement", "ADM-4")]
    public async Task Admins_OpenAdminOnlyPages()
    {
        await using var admin = new AdminComponents(server, withProviders: false);
        admin.Services.GetRequiredService<NavigationManager>().NavigateTo("/admin/api-keys");

        var routes = admin.Render<Routes>();

        routes.WaitForAssertion(() => routes.Find("h1").TextContent.Trim().ShouldBe("API keys"));
    }

    [Fact]
    [Trait("Requirement", "ADM-4")]
    public async Task AnApiKey_IsCreated_AndItsSecretShownOnce()
    {
        await using var admin = new AdminComponents(server);
        var name = $"Key {Guid.CreateVersion7():N}";
        var page = admin.Render<ApiKeysPage>();

        page.WaitForElement("#create-api-key").Click();
        admin.Fill("Name", name);
        admin.Dialogs.WaitForAssertion(() => admin.Dialogs.FindComponents<MudBlazor.MudCheckBox<bool>>().ShouldNotBeEmpty());
        var customersRead = admin.Dialogs.FindComponents<MudBlazor.MudCheckBox<bool>>().Single(box => box.Instance.Label == TahsilPermissions.CustomersRead);
        await customersRead.InvokeAsync(() => customersRead.Instance.ValueChanged.InvokeAsync(true));
        admin.Confirm("Create");

        admin.WaitForNotification("The key was created.");
        admin.Dialogs.WaitForAssertion(() => admin.Dialogs.Find(".secret input").GetAttribute("value")!.ShouldStartWith("ths_test_"));
        await AuditedByAsync(admin, "api_key.created");
    }

    [Fact]
    public async Task ATaxRate_IsCreated_ThenArchived()
    {
        await using var admin = new AdminComponents(server);
        var key = $"kdv-{Guid.CreateVersion7():N}"[..30];
        var page = admin.Render<TaxRates>();

        page.WaitForElement("#create-tax-rate").Click();
        admin.Fill("Key", key);
        admin.Fill("Name on invoices", "KDV");
        admin.Confirm("Create");
        admin.WaitForNotification("The tax rate was created.");
        page.WaitForAssertion(() => page.Markup.ShouldContain(key));
        page.FindAll("tr").Single(row => row.TextContent.Contains(key)).QuerySelector("button.archive")!.Click();

        admin.WaitForNotification("The tax rate was archived.");
        var rate = await server.QueryDatabaseAsync(context => context.TaxRates.SingleAsync(candidate => candidate.Key == key));
        (rate.Percentage, rate.IsInclusive, rate.IsArchived).ShouldBe((20m, true, true));
        await AuditedByAsync(admin, "tax_rate.created");
    }

    [Fact]
    [Trait("Requirement", "CAT-16")]
    public async Task AMeter_IsCreatedWithThePropertyItCounts_ThenArchived()
    {
        await using var admin = new AdminComponents(server);
        var key = $"seats_{Guid.CreateVersion7():N}"[..30];
        var page = admin.Render<Meters>();

        page.WaitForElement("#create-meter").Click();
        admin.Fill("Key", key);
        admin.Fill("Name on invoices", "Active users");
        var counts = admin.Dialogs.FindComponents<MudBlazor.MudSelect<MeterAggregation>>().Single();
        await counts.InvokeAsync(() => counts.Instance.ValueChanged.InvokeAsync(MeterAggregation.UniqueCount));
        admin.Dialogs.WaitForElement("#meter-unique-property");
        admin.Fill("Property", "user_id");
        admin.Confirm("Create");
        admin.WaitForNotification("The meter was created.");
        page.WaitForAssertion(() => page.FindAll("tr").Single(row => row.TextContent.Contains(key)).TextContent.ShouldContain("The distinct values of user_id"));
        page.FindAll("tr").Single(row => row.TextContent.Contains(key)).QuerySelector("button.archive")!.Click();

        admin.WaitForNotification("The meter was archived.");
        var meter = await server.QueryDatabaseAsync(context => context.Meters.SingleAsync(candidate => candidate.Key == key));
        (meter.Name, meter.Aggregation, meter.UniqueProperty, meter.IsArchived).ShouldBe(("Active users", Domain.Catalog.MeterAggregation.UniqueCount, "user_id", true));
        await AuditedByAsync(admin, "meter.created");
    }

    [Fact]
    [Trait("Requirement", "CAT-10")]
    public async Task APrice_IsAddedInMajorUnits_AndItsLookupKeySet()
    {
        var productKey = $"pro-{Guid.CreateVersion7():N}"[..30];
        await using var admin = new AdminComponents(server);
        var products = admin.Render<Products>();
        products.WaitForElement("#create-product").Click();
        admin.Fill("Key", productKey);
        admin.Fill("Name", "Pro");
        admin.Confirm("Create");
        admin.WaitForNotification("The product was created.");

        var page = admin.Render<ProductDetail>(parameters => parameters.Add(detail => detail.Key, productKey));
        page.WaitForElement("#create-price").Click();
        admin.Fill("Name", "Monthly");
        admin.Fill("Amount", "299.9");
        admin.Confirm("Add");
        admin.WaitForNotification("The price was added.");
        page.WaitForAssertion(() => page.Markup.ShouldContain("299.90 TRY"));
        page.Find("button.set-key").Click();
        admin.Fill("Lookup key", $"{productKey}-monthly");
        admin.Confirm("Save");

        admin.WaitForNotification("The lookup key was set.");
        var price = await server.QueryDatabaseAsync(context => context.Prices.SingleAsync(candidate => candidate.Key == $"{productKey}-monthly"));
        (price.UnitAmount, price.Currency, price.Interval, price.IntervalCount).ShouldBe((29990L, "TRY", Domain.Catalog.PriceInterval.Month, (short?)1));
    }

    [Fact]
    [Trait("Requirement", "CUS-7")]
    public async Task ACustomer_IsFound_Edited_AndDeleted()
    {
        var externalId = $"admin-{Guid.CreateVersion7():N}";
        var id = await server.QueryDatabaseAsync(async context =>
        {
            var customer = Domain.Customers.Customer.Create(externalId, new Domain.Customers.CustomerDetails { Email = $"{externalId}@example.com", Name = "Ayşe", Locale = "tr" }, (_, _) => [], DateTimeOffset.UtcNow).Value;
            context.Customers.Add(customer);
            await context.SaveChangesAsync();
            return customer.Id;
        });
        await using var admin = new AdminComponents(server);
        var list = admin.Render<CustomersPage>();
        list.WaitForElement("input#search").Change(externalId);
        list.Find("#search-button").Click();
        list.WaitForAssertion(() => list.FindAll("#customers tbody tr").Count.ShouldBe(1));

        var detail = admin.Render<CustomerDetail>(parameters => parameters.Add(page => page.Id, id));
        detail.WaitForElement("#edit-customer").Click();
        admin.Fill("Name", "Ayşe Yılmaz");
        admin.Confirm("Save");
        admin.WaitForNotification("The customer was saved.");
        detail.WaitForAssertion(() => detail.Find("h1").TextContent.ShouldBe("Ayşe Yılmaz"));
        detail.Find("#delete-customer").Click();
        admin.Confirm("Delete");

        admin.WaitForNotification("The customer was deleted.");
        (await server.QueryDatabaseAsync(context => context.Customers.AnyAsync(customer => customer.Id == id))).ShouldBeFalse();
        await AuditedByAsync(admin, "customer.updated");
    }

    [Fact]
    [Trait("Requirement", "ADM-2")]
    public async Task ViewersAreRefused_WhatTheirRoleDoesNotAllow_WithAMessage()
    {
        await using var viewer = new AdminComponents(server, AdminRoles.Viewer);
        var page = viewer.Render<TaxRates>();

        page.WaitForAssertion(() => page.Find("h1").TextContent.Trim().ShouldBe("Tax rates"));
        page.FindAll("#create-tax-rate").ShouldBeEmpty();
        var result = await viewer.Services.GetRequiredService<AdminOperations>().RunAsync<Application.Catalog.CreateTaxRateHandler, Domain.Common.Result<Application.Common.Versioned<TaxRateResponse>>>(
            handler => handler.HandleAsync(new CreateTaxRateRequest { Key = "nope", Name = "Nope", Percentage = 1, Inclusive = true }, Xunit.TestContext.Current.CancellationToken));
        result.Error.ShouldNotBeNull().Code.ShouldBe("auth.forbidden");
    }

    private async Task AuditedByAsync(AdminComponents admin, string action)
    {
        var actors = await server.QueryDatabaseAsync(context => context.AuditEvents
            .Where(auditEvent => auditEvent.Action == action && auditEvent.ActorId == admin.Subject)
            .Select(auditEvent => auditEvent.ActorType)
            .ToListAsync());
        actors.ShouldNotBeEmpty();
        actors.ShouldAllBe(type => type == Domain.Common.ActorType.Admin);
    }
}
