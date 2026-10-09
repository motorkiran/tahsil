using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Tahsil.Application.Customers;
using Tahsil.Contracts;

namespace Tahsil.Server.Tests.Customers;

public sealed class CustomerApiTests(TahsilServerFixture server)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "CUS-2")]
    public async Task AnExternalIdBelongsToOneCustomer()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var first = await CreateAsync(client, new CreateCustomerRequest { ExternalId = "user-42", Email = "ayse@example.com", Name = "Ayşe Yılmaz" });

        using var duplicate = await client.PostJsonAsync("/api/v1/customers", new CreateCustomerRequest { ExternalId = "user-42" });
        var problem = await duplicate.ReadAsync<JsonNode>();

        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        problem["code"]!.GetValue<string>().ShouldBe("customer.external_id_taken");
        problem["customerId"]!.GetValue<Guid>().ShouldBe(first.Id);
        (await (await client.GetPathAsync("/api/v1/customers/by-external-id/user-42")).ReadAsync<CustomerResponse>()).Id.ShouldBe(first.Id);
        first.Locale.ShouldBe("tr");
    }

    [Fact]
    [Trait("Requirement", "CUS-2")]
    public async Task UpsertingByExternalId_CreatesOnce_ThenUpdates_EvenWhenRacing()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);

        var racing = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => PutAsync(client, "/api/v1/customers/by-external-id/user-7", new { email = "can@example.com" })));
        using var updated = await PutAsync(client, "/api/v1/customers/by-external-id/user-7", new { name = "Can Demir" });
        var customer = await updated.ReadAsync<CustomerResponse>();

        racing.Count(response => response.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        racing.ShouldAllBe(response => response.IsSuccessStatusCode, string.Join(",", racing.Select(response => response.StatusCode + ":" + response.Content.ReadAsStringAsync(CancellationToken).Result)));
        updated.StatusCode.ShouldBe(HttpStatusCode.OK);
        (customer.Email, customer.Name, customer.ExternalId).ShouldBe(("can@example.com", "Can Demir", "user-7"));
        (await (await client.GetPathAsync("/api/v1/customers?externalId=user-7")).ReadAsync<Page<CustomerResponse>>()).Items.Count.ShouldBe(1);

        foreach (var response in racing)
        {
            response.Dispose();
        }
    }

    [Fact]
    [Trait("Requirement", "CUS-1")]
    public async Task Search_MatchesBeginningsIgnoringCase_AndEmailsExactly_NewestFirst()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var ayse = await CreateAsync(client, new CreateCustomerRequest { ExternalId = "acme-1", Email = "Ayse@Example.com", Name = "Ayşe Yılmaz" });
        var ahmet = await CreateAsync(client, new CreateCustomerRequest { ExternalId = "acme-2", Email = "ahmet@example.com", Name = "Ahmet Kaya" });
        await CreateAsync(client, new CreateCustomerRequest { ExternalId = "other-3", Email = "zeynep@example.com", Name = "Zeynep Acar" });

        var byPrefix = await ListAsync(client, "q=ACME");
        var byEmail = await ListAsync(client, "email=ayse@example.com");
        var byName = await ListAsync(client, "q=ahm");
        var notInside = await ListAsync(client, "q=example");

        byPrefix.Select(customer => customer.Id).ShouldBe([ahmet.Id, ayse.Id]);
        byEmail.ShouldHaveSingleItem().Id.ShouldBe(ayse.Id);
        byName.ShouldHaveSingleItem().Id.ShouldBe(ahmet.Id);
        notInside.ShouldBeEmpty();
        (await host.QueryAsync<int>($"SELECT count(*)::int AS \"Value\" FROM pg_indexes WHERE indexname = 'ix_customers_lower_email'")).ShouldBe([1]);
    }

    [Fact]
    [Trait("Requirement", "SEC-15")]
    [Trait("Requirement", "CUS-4")]
    public async Task TaxIds_AreValidated_AndUnreadableInTheDatabase()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);

        var customer = await CreateAsync(client, new CreateCustomerRequest { Name = "Ayşe Yılmaz", TaxId = "10000000146" });
        using var invalid = await client.PostJsonAsync("/api/v1/customers", new CreateCustomerRequest { TaxId = "11111111111" });
        var stored = (await host.QueryAsync<byte[]>($"SELECT tax_id AS \"Value\" FROM tahsil.customers WHERE id = {customer.Id}")).Single();

        customer.TaxId.ShouldBe("10000000146");
        Encoding.UTF8.GetString(stored).ShouldNotContain("10000000146");
        stored[0].ShouldBe((byte)2);
        (await invalid.ProblemCodeAsync()).ShouldBe("customer.invalid_tax_id");
    }

    [Fact]
    [Trait("Requirement", "CUS-3")]
    public async Task AKimlikSubscriber_BelongsToOneCustomer()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var subscriber = new KimlikSubscriber(KimlikSubscriberType.Organization, Guid.CreateVersion7());
        await CreateAsync(client, new CreateCustomerRequest { Name = "Acme", Type = CustomerType.Company, KimlikSubscriber = subscriber });

        using var second = await client.PostJsonAsync("/api/v1/customers", new CreateCustomerRequest { Name = "Acme 2", KimlikSubscriber = subscriber });

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.ProblemCodeAsync()).ShouldBe("customer.kimlik_subscriber_taken");
    }

    [Fact]
    [Trait("Requirement", "API-5")]
    public async Task Updates_ArePatches_RecordedByFieldName()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CreateAsync(client, new CreateCustomerRequest { Email = "ayse@example.com", Name = "Ayşe", TaxId = "10000000146" });
        var etag = (await client.GetPathAsync($"/api/v1/customers/{customer.Id}")).Headers.ETag!.Tag;

        using var updated = await PatchAsync(client, $"/api/v1/customers/{customer.Id}", new { name = "Ayşe Yılmaz", taxId = "12345678950", email = (string?)null }, etag);
        using var stale = await PatchAsync(client, $"/api/v1/customers/{customer.Id}", new { name = "Again" }, etag);
        var after = await updated.ReadAsync<CustomerResponse>();

        (after.Name, after.Email, after.TaxId).ShouldBe(("Ayşe Yılmaz", null, "12345678950"));
        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        var data = await host.QueryAsync<string>($"SELECT data::text AS \"Value\" FROM tahsil.audit_events WHERE action = 'customer.updated'");
        JsonNode.Parse(data.Single())!["changed"]!.AsArray().Select(field => field!.GetValue<string>()).ShouldBe(["email", "name", "taxId"]);
        data.Single().ShouldNotContain("12345678950");
    }

    [Fact]
    [Trait("Requirement", "CUS-7")]
    public async Task ACustomerWithoutHistory_IsDeleted()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CreateAsync(client, new CreateCustomerRequest { ExternalId = "user-1", Email = "ayse@example.com" });

        using var deleted = await client.DeleteAsync(new Uri($"/api/v1/customers/{customer.Id}", UriKind.Relative), CancellationToken);

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetPathAsync($"/api/v1/customers/{customer.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await host.QueryAsync<string>($"SELECT data->>'category' AS \"Value\" FROM tahsil.audit_events WHERE action = 'data.destroyed'")).ShouldBe(["customers"]);
    }

    [Fact]
    [Trait("Requirement", "CUS-8")]
    [Trait("Requirement", "CUS-9")]
    public async Task ACustomerWithHistory_IsAnonymized_AndStaysSo()
    {
        await using var host = Host(services => services.AddSingleton<ICustomerHistory>(new EveryoneHasHistory()));
        using var client = await ClientAsync(host);
        var customer = await CreateAsync(client, new CreateCustomerRequest { ExternalId = "user-1", Email = "ayse@example.com", Name = "Ayşe", TaxId = "10000000146" });

        using var deleted = await client.DeleteAsync(new Uri($"/api/v1/customers/{customer.Id}", UriKind.Relative), CancellationToken);
        var anonymized = await deleted.ReadAsync<CustomerResponse>();
        using var update = await PatchAsync(client, $"/api/v1/customers/{customer.Id}", new { name = "Back" });

        deleted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (anonymized.ExternalId, anonymized.Email, anonymized.Name, anonymized.TaxId).ShouldBe((null, null, null, null));
        anonymized.AnonymizedAt.ShouldNotBeNull();
        update.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await update.ProblemCodeAsync()).ShouldBe("customer.anonymized");
    }

    [Fact]
    [Trait("Requirement", "SEC-15")]
    public async Task AnExport_HoldsTheCustomerAndTheirTrail()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var customer = await CreateAsync(client, new CreateCustomerRequest { Email = "ayse@example.com", TaxId = "10000000146" });
        (await PatchAsync(client, $"/api/v1/customers/{customer.Id}", new { name = "Ayşe" })).Dispose();

        var export = await (await client.GetPathAsync($"/api/v1/customers/{customer.Id}/export")).ReadAsync<CustomerExportResponse>();

        export.Customer.TaxId.ShouldBe("10000000146");
        export.AuditEvents.Select(auditEvent => auditEvent.Action).ShouldBe(["customer.updated", "customer.created"]);
    }

    private static async Task<HttpClient> ClientAsync(WebApplicationFactory<Program> host) =>
        await host.CreateClientWithAsync(TahsilPermissions.CustomersRead, TahsilPermissions.CustomersWrite);

    private static async Task<CustomerResponse> CreateAsync(HttpClient client, CreateCustomerRequest request)
    {
        using var response = await client.PostJsonAsync("/api/v1/customers", request);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<CustomerResponse>();
    }

    private static async Task<IReadOnlyList<CustomerResponse>> ListAsync(HttpClient client, string query) =>
        (await (await client.GetPathAsync($"/api/v1/customers?{query}")).ReadAsync<Page<CustomerResponse>>()).Items;

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string path, object body) =>
        client.PutAsJsonAsync(new Uri(path, UriKind.Relative), body, ApiClients.Json, CancellationToken);

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, string path, object body, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body, options: ApiClients.Json) };
        if (ifMatch is not null)
        {
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(ifMatch));
        }

        return await client.SendAsync(request, CancellationToken);
    }

    private WebApplicationFactory<Program> Host(Action<IServiceCollection>? services = null) =>
        server.WithSettings(new Dictionary<string, string?> { ["ConnectionStrings:Tahsil"] = server.NewDatabase("customers") }, services);

    private sealed class EveryoneHasHistory : ICustomerHistory
    {
        public Task<bool> HasHistoryAsync(Guid customerId, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
