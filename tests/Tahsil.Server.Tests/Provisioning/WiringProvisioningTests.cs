using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Tahsil.Application.Provisioning;
using Tahsil.Contracts;
using Tahsil.Providers.Abstractions;
using Tahsil.Server.Tests.Connections;

namespace Tahsil.Server.Tests.Provisioning;

/// <summary>
/// Provisioning of the wiring: provider connections with the currencies routed to them, and webhook endpoints, whose
/// credentials and secrets come from configuration in the startup file and literally through the API.
/// </summary>
public sealed class WiringProvisioningTests(TahsilServerFixture server)
{
    private const string FirstSecret = "whsec_MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=";
    private const string SecondSecret = "whsec_YWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXphYmNkZWY=";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "PRV-2")]
    [Trait("Requirement", "PRV-4")]
    [Trait("Requirement", "PRV-10")]
    public async Task ConnectionsAndEndpoints_AreApplied_ThenFoundUnchanged_ThenUpdated()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);

        var first = await ApplyAsync(client, Document("sk_test_first_0000000000", FirstSecret));
        var again = await ApplyAsync(client, Document("sk_test_first_0000000000", FirstSecret));
        var omitted = await ApplyAsync(client, Document(apiKey: null, secret: null));
        var changed = await ApplyAsync(client, Document("sk_test_second_000000000", SecondSecret));

        (first.Created, first.Updated, first.Unchanged).ShouldBe((2, 0, 0));
        (again.Created, again.Updated, again.Unchanged).ShouldBe((0, 0, 2));
        (omitted.Created, omitted.Updated, omitted.Unchanged).ShouldBe((0, 0, 2), "omitted credentials and secrets keep their values");
        changed.Items.Select(item => (item.Path, item.Result)).ShouldBe([("providerConnections[0]", ProvisioningItemResult.Updated), ("webhookEndpoints[0]", ProvisioningItemResult.Updated)]);
        var connection = await GetAsync<ConnectionResponse>(client, "/api/v1/provider-connections/fake-main");
        (connection.Credentials["apiKey"].Last4, connection.Settings["region"].GetString()).ShouldBe(("0000", "eu"));
        connection.Currencies.ShouldBe(["USD"]);
        (await GetAsync<Dictionary<string, string>>(client, "/api/v1/provider-connections/routing"))["USD"].ShouldBe("fake-main");
        var endpoint = (await GetAsync<Page<WebhookEndpointResponse>>(client, "/api/v1/webhooks/endpoints")).Items.ShouldHaveSingleItem();
        endpoint.Key.ShouldBe("app");
        endpoint.EventTypes.ShouldBe(["invoice.paid"]);
        endpoint.PreviousSecretExpiresAt.ShouldNotBeNull("a new secret is a rotation: the old one keeps signing for the overlap");
    }

    [Fact]
    [Trait("Requirement", "PRV-5")]
    public async Task ChangingAConnectionsEnvironment_NamesTheItem_AndAppliesNothing()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var document = Document("sk_test_first_0000000000", FirstSecret);
        await ApplyAsync(client, document);

        using var response = await client.PostJsonAsync("/api/v1/provisioning", document with
        {
            ProviderConnections = [document.ProviderConnections![0] with { Name = "Renamed", Environment = ConnectionEnvironment.Production }],
        });
        var problem = await response.ReadAsync<JsonNode>();

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        problem["code"]!.GetValue<string>().ShouldBe("provisioning.fixed_property");
        problem["detail"]!.GetValue<string>().ShouldStartWith("providerConnections[0] 'fake-main': 'environment' cannot change");
        (await GetAsync<ConnectionResponse>(client, "/api/v1/provider-connections/fake-main")).Name.ShouldBe("Fake");
    }

    [Fact]
    [Trait("Requirement", "PRV-9")]
    public async Task ConfigurationReferences_AreRefusedThroughTheApi()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);

        using var response = await client.PostJsonAsync("/api/v1/provisioning", Document("${Fake:ApiKey}", FirstSecret));
        var problem = await response.ReadAsync<JsonNode>();

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        problem["code"]!.GetValue<string>().ShouldBe("provisioning.config_reference_not_allowed");
        problem["detail"]!.GetValue<string>().ShouldStartWith("providerConnections[0].credentials.apiKey 'fake-main':");
    }

    [Fact]
    [Trait("Requirement", "PRV-7")]
    [Trait("Requirement", "PRV-8")]
    [Trait("Requirement", "PRV-11")]
    public async Task AnExport_ShowsReferencesForSecrets_AndAStartupFileWithThoseValuesConfigured_ReproducesIt()
    {
        await using var source = Host();
        using var sourceClient = await ClientAsync(source);
        await ApplyAsync(sourceClient, Document("sk_test_first_0000000000", FirstSecret));
        var exported = await GetAsync<ProvisioningDocument>(sourceClient, "/api/v1/provisioning");
        var file = Path.Combine(Path.GetTempPath(), $"tahsil-provisioning-{Guid.CreateVersion7():N}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(exported, ProvisioningFile.SerializerOptions), CancellationToken);

        await using var target = Host(new Dictionary<string, string?>
        {
            ["Tahsil:Provisioning:FilePath"] = file,
            ["Connections:fake-main:apiKey"] = "sk_test_target_00000000",
            ["Connections:fake-main:secretKey"] = "target-secret",
            ["Webhooks:app:Secret"] = SecondSecret,
        });
        using var targetClient = await ClientAsync(target);
        var reproduced = await GetAsync<ProvisioningDocument>(targetClient, "/api/v1/provisioning");
        File.Delete(file);

        var connection = exported.ProviderConnections!.Single(candidate => candidate.Key == "fake-main");
        connection.Credentials.ShouldBe(new Dictionary<string, string>
        {
            ["apiKey"] = "${Connections:fake-main:apiKey}",
            ["secretKey"] = "${Connections:fake-main:secretKey}",
        });
        connection.Currencies.ShouldBe(["USD"]);
        connection.Settings!["region"].GetString().ShouldBe("eu");
        exported.ProviderConnections!.Single(candidate => candidate.Key == "simulator").Currencies.ShouldBe(["EUR", "TRY"]);
        exported.WebhookEndpoints.ShouldHaveSingleItem().Secret.ShouldBe("${Webhooks:app:Secret}");
        JsonSerializer.Serialize(reproduced, ApiClients.Json).ShouldBe(JsonSerializer.Serialize(exported, ApiClients.Json));
        (await GetAsync<ConnectionResponse>(targetClient, "/api/v1/provider-connections/fake-main")).Credentials["apiKey"].Last4.ShouldBe("0000");
    }

    [Theory]
    [Trait("Requirement", "PRV-7")]
    [InlineData("sk_test_literal_00000000", "provisioning.literal_secret")]
    [InlineData("${Fake:Missing}", "'Fake:Missing', which is not set (environment variable Fake__Missing)")]
    public async Task AStartupFileWithALiteralOrMissingSecret_StopsStartup(string apiKey, string message)
    {
        var file = Path.Combine(Path.GetTempPath(), $"tahsil-provisioning-{Guid.CreateVersion7():N}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(Document(apiKey, secret: null), ProvisioningFile.SerializerOptions), CancellationToken);
        await using var host = Host(new Dictionary<string, string?> { ["Tahsil:Provisioning:FilePath"] = file });

        var failure = Should.Throw<Exception>(() => host.CreateClient());
        File.Delete(file);

        failure.ToString().ShouldContain(message);
    }

    private static ProvisioningDocument Document(string? apiKey, string? secret) => new()
    {
        ProviderConnections =
        [
            new ProvisionedConnection
            {
                Key = "fake-main",
                Provider = "fakepay",
                Name = "Fake",
                Environment = ConnectionEnvironment.Sandbox,
                Currencies = ["usd"],
                Credentials = apiKey is null ? null : new Dictionary<string, string> { ["apiKey"] = apiKey, ["secretKey"] = "secret-key" },
                Settings = new Dictionary<string, JsonElement> { ["region"] = JsonSerializer.SerializeToElement("eu") },
            },
        ],
        WebhookEndpoints =
        [
            new ProvisionedWebhookEndpoint { Key = "app", Url = "https://app.example.com/webhooks/tahsil", EventTypes = ["invoice.paid"], Secret = secret },
        ],
    };

    private static async Task<ProvisioningResult> ApplyAsync(HttpClient client, ProvisioningDocument document)
    {
        using var response = await client.PostAsJsonAsync(new Uri("/api/v1/provisioning", UriKind.Relative), document, ApiClients.Json, CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<ProvisioningResult>();
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string path)
    {
        using var response = await client.GetPathAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<T>();
    }

    private static Task<HttpClient> ClientAsync(WebApplicationFactory<Program> host) => host.CreateClientWithAsync(
        TahsilPermissions.ProvisioningRead, TahsilPermissions.ProvisioningWrite, TahsilPermissions.ConnectionsRead, TahsilPermissions.ConnectionsWrite,
        TahsilPermissions.WebhooksRead, TahsilPermissions.WebhooksWrite);

    private WebApplicationFactory<Program> Host(Dictionary<string, string?>? settings = null)
    {
        settings ??= [];
        settings["ConnectionStrings:Tahsil"] = server.NewDatabase("provisioning");
        return server.WithSettings(settings, services => services.AddSingleton<IPaymentProvider>(new FakeProvider("fakepay", "TRY", "USD")));
    }
}
