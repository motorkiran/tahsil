using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tahsil.Application.Common;
using Tahsil.Application.Connections;
using Tahsil.Contracts;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Server.Tests.Connections;

public sealed class ConnectionApiTests(TahsilServerFixture server)
{
    private const string ApiKey = "sk_test_0123456789abcdef";
    private const string SecretKey = "s3cr3t-v4l";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "PAY-1")]
    [Trait("Requirement", "SEC-9")]
    public async Task Credentials_AreValidatedAndEncrypted_AndNeverAppearInResponsesOrLogs()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = Host(services => services.AddSingleton<ILoggerProvider>(logs));
        using var client = await ClientAsync(host);

        var connection = await CreateAsync(client, "fake-main", new Dictionary<string, string> { ["apiKey"] = ApiKey, ["secretKey"] = SecretKey });
        using var listed = await client.GetPathAsync("/api/v1/provider-connections");
        var listedBody = await listed.Content.ReadAsStringAsync(CancellationToken);

        connection.Credentials["apiKey"].ShouldBe(new CredentialState(true, "cdef"));
        connection.Credentials["secretKey"].ShouldBe(new CredentialState(true, null));
        connection.Credentials["webhookSecret"].ShouldBe(new CredentialState(false, null));
        connection.NotificationUrl.ShouldBe(new Uri($"http://localhost/provider-notifications/{connection.Id}"));
        listedBody.ShouldNotContain(ApiKey);
        listedBody.ShouldNotContain(SecretKey);
        var stored = await host.QueryAsync<byte[]>($"SELECT credentials AS \"Value\" FROM tahsil.provider_connections WHERE key = 'fake-main'");
        Encoding.UTF8.GetString(stored.ShouldHaveSingleItem()).ShouldNotContain(ApiKey);
        var audit = await host.QueryAsync<string>($"SELECT data::text AS \"Value\" FROM tahsil.audit_events WHERE action = 'connection.created' AND data->>'key' = 'fake-main'");
        audit.ShouldHaveSingleItem().ShouldContain("apiKey");
        audit.Single().ShouldNotContain(ApiKey);
        logs.Entries.ShouldAllBe(entry => !entry.Message.Contains(ApiKey) && !entry.Message.Contains(SecretKey));
    }

    [Theory]
    [Trait("Requirement", "PAY-1")]
    [InlineData("missing", "credentials.secretKey")]
    [InlineData("unknown", "credentials.password")]
    [InlineData("pattern", "credentials.apiKey")]
    public async Task InvalidCredentials_AreRejected_WithoutEchoingThem(string problem, string field)
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var credentials = problem switch
        {
            "missing" => new Dictionary<string, string> { ["apiKey"] = ApiKey },
            "unknown" => new Dictionary<string, string> { ["apiKey"] = ApiKey, ["secretKey"] = SecretKey, ["password"] = "hunter2-hunter2" },
            _ => new Dictionary<string, string> { ["apiKey"] = "pk_wrong_kind_of_key", ["secretKey"] = SecretKey },
        };

        using var response = await client.PostJsonAsync("/api/v1/provider-connections", Request("fake-main", credentials));
        var body = await response.Content.ReadAsStringAsync(CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonNode.Parse(body)!["code"]!.GetValue<string>().ShouldBe("connection.invalid_credentials");
        JsonNode.Parse(body)!["errors"]!.AsObject().Select(error => error.Key).ShouldBe([field]);
        body.ShouldNotContain("hunter2");
        body.ShouldNotContain("pk_wrong");
    }

    [Fact]
    [Trait("Requirement", "PAY-1")]
    public async Task Settings_FollowTheDescriptor_AndKeepTheirTypes()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);

        var connection = await CreateAsync(client, "fake-main", settings: new { region = "eu", timeoutSeconds = 20, allowDirectForm = true });
        using var invalid = await client.PostJsonAsync("/api/v1/provider-connections", Request("fake-two", settings: new { region = "us" }));
        using var unknown = await client.PostJsonAsync("/api/v1/provider-connections", Request("fake-three", settings: new { color = "blue" }));

        connection.Settings["region"].GetString().ShouldBe("eu");
        connection.Settings["timeoutSeconds"].GetDecimal().ShouldBe(20);
        connection.Settings["allowDirectForm"].GetBoolean().ShouldBeTrue();
        (await invalid.ProblemCodeAsync()).ShouldBe("connection.invalid");
        (await unknown.ProblemCodeAsync()).ShouldBe("connection.invalid");
    }

    [Fact]
    [Trait("Requirement", "PAY-6")]
    public async Task Updates_MergeCredentials_AreAudited_AndNeedTheCurrentVersion()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var created = await CreateAsync(client, "fake-main");
        using var current = await client.GetPathAsync($"/api/v1/provider-connections/fake-main");
        var etag = current.Headers.ETag!.Tag;

        using var updated = await PatchAsync(client, "/api/v1/provider-connections/fake-main", new { credentials = new { secretKey = "another-secret" }, enabled = false }, etag);
        using var stale = await PatchAsync(client, "/api/v1/provider-connections/fake-main", new { name = "Again" }, etag);
        var connection = await updated.ReadAsync<ConnectionResponse>();

        updated.StatusCode.ShouldBe(HttpStatusCode.OK);
        connection.Credentials["apiKey"].ShouldBe(new CredentialState(true, "cdef"));
        connection.Enabled.ShouldBeFalse();
        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        var audit = await host.QueryAsync<string>($"SELECT data::text AS \"Value\" FROM tahsil.audit_events WHERE action = 'connection.updated'");
        var data = JsonNode.Parse(audit.ShouldHaveSingleItem())!;
        data["credentials"]!.AsArray().Select(name => name!.GetValue<string>()).ShouldBe(["secretKey"]);
        data["enabled"]!.GetValue<bool>().ShouldBeFalse();
        audit.Single().ShouldNotContain("another-secret");
        await using var scope = host.Services.CreateAsyncScope();
        var secrets = scope.ServiceProvider.GetRequiredService<ConnectionSecrets>();
        var stored = scope.ServiceProvider.GetRequiredService<ITahsilDbContext>().ProviderConnections.Single(row => row.Id == created.Id);
        secrets.Decrypt(stored).ShouldBe(new Dictionary<string, string> { ["apiKey"] = ApiKey, ["secretKey"] = "another-secret" });
    }

    [Fact]
    [Trait("Requirement", "PAY-2")]
    [Trait("Requirement", "DOM-8")]
    public async Task TestMode_TakesSandboxConnectionsOnly()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);

        using var production = await client.PostJsonAsync("/api/v1/provider-connections", Request("fake-live", environment: ConnectionEnvironment.Production));
        using var routingKey = await client.PostJsonAsync("/api/v1/provider-connections", Request("routing"));

        (await production.ProblemCodeAsync()).ShouldBe("connection.not_allowed_in_mode");
        (await routingKey.ProblemCodeAsync()).ShouldBe("connection.invalid");
    }

    [Fact]
    [Trait("Requirement", "PAY-2")]
    [Trait("Requirement", "DOM-8")]
    public async Task LiveMode_TakesProductionConnectionsOnly_AndHidesTheSimulator()
    {
        await using var host = Host(mode: "Live");
        await using var scope = host.Services.CreateAsyncScope();
        var create = scope.ServiceProvider.GetRequiredService<CreateConnectionHandler>();

        var sandbox = await create.HandleAsync(Request("fake-sandbox"), CancellationToken);
        var simulator = await create.HandleAsync(Request("sim", provider: "simulator", environment: ConnectionEnvironment.Production), CancellationToken);
        var production = await create.HandleAsync(Request("fake-live", environment: ConnectionEnvironment.Production), CancellationToken);

        sandbox.Error!.Code.ShouldBe("connection.not_allowed_in_mode");
        simulator.Error!.Code.ShouldBe("connection.unknown_provider");
        production.IsSuccess.ShouldBeTrue();
        scope.ServiceProvider.GetRequiredService<ListProvidersHandler>().Handle().Value.Items.Select(provider => provider.Key).ShouldBe(["fakepay", "iyzico", "paytr", "stripe"]);
    }

    [Fact]
    [Trait("Requirement", "DOM-8")]
    public async Task Startup_Fails_WhenConnectionsBreakTheModeRule()
    {
        var database = server.NewDatabase("connections");
        await using (var test = Host(database: database))
        {
            using var client = await ClientAsync(test);
            await CreateAsync(client, "fake-sandbox");
        }

        await using var live = Host(database: database, mode: "Live");
        var failure = Should.Throw<Exception>(() => live.CreateClient());

        failure.ToString().ShouldContain("The provider connections fake-sandbox, simulator are not allowed in Live mode");
    }

    [Fact]
    [Trait("Requirement", "DOM-8")]
    public async Task ConnectionsThatBreakTheModeRule_NeverReachTheirAdapter_WhenMigrationsRunSeparately()
    {
        var database = server.NewDatabase("connections");
        await using (var test = Host(database: database))
        {
            using var client = await ClientAsync(test);
            await CreateAsync(client, "fake-sandbox");
        }

        var fake = new FakeProvider("fakepay", "TRY");
        await using var live = Host(database: database, mode: "Live", provider: fake, migrateOnStartup: false);
        await using var scope = live.Services.CreateAsyncScope();

        var tested = await scope.ServiceProvider.GetRequiredService<TestConnectionHandler>().HandleAsync("fake-sandbox", CancellationToken);

        tested.Value.Value.LastTest.ShouldNotBeNull().Message.ShouldBe("The connection is not allowed in Live mode.");
        fake.Contexts.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Requirement", "PAY-2")]
    public async Task AFreshTestModeInstallation_GetsASimulatorConnection_Once()
    {
        var database = server.NewDatabase("connections");
        await using (var first = Host(database: database))
        {
            using var client = await ClientAsync(first);
            var connections = await ListAsync(client);
            var routing = await (await client.GetPathAsync("/api/v1/provider-connections/routing")).ReadAsync<Dictionary<string, string>>();

            var simulator = connections.ShouldHaveSingleItem();
            (simulator.Key, simulator.Provider, simulator.Environment, simulator.Enabled).ShouldBe(("simulator", "simulator", ConnectionEnvironment.Sandbox, true));
            simulator.Currencies.ShouldBe(["EUR", "TRY", "USD"]);
            routing.ShouldBe(new Dictionary<string, string> { ["EUR"] = "simulator", ["TRY"] = "simulator", ["USD"] = "simulator" });

            await CreateAsync(client, "fake-main");
            (await client.DeleteAsync(new Uri("/api/v1/provider-connections/simulator", UriKind.Relative), CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        await using var restarted = Host(database: database);
        using var again = await ClientAsync(restarted);
        (await ListAsync(again)).Select(connection => connection.Key).ShouldBe(["fake-main"]);
    }

    [Fact]
    [Trait("Requirement", "PAY-3")]
    public async Task Testing_RunsTheAdaptersCheck_WithTheConnectionsValues_AndRecordsTheResult()
    {
        var fake = new FakeProvider("fakepay", "TRY", "USD");
        await using var host = Host(provider: fake);
        using var client = await ClientAsync(host);
        var connection = await CreateAsync(client, "fake-main", new Dictionary<string, string> { ["apiKey"] = FakeProvider.BadApiKey, ["secretKey"] = SecretKey });

        var failed = await (await client.PostPathAsync("/api/v1/provider-connections/fake-main/test")).ReadAsync<ConnectionResponse>();
        using var fixedKey = await PatchAsync(client, "/api/v1/provider-connections/fake-main", new { credentials = new { apiKey = ApiKey } });
        var succeeded = await (await client.PostPathAsync("/api/v1/provider-connections/fake-main/test")).ReadAsync<ConnectionResponse>();
        using var throwing = await PatchAsync(client, "/api/v1/provider-connections/fake-main", new { credentials = new { apiKey = "sk_test_throws_000000000" } });
        var crashed = await (await client.PostPathAsync("/api/v1/provider-connections/fake-main/test")).ReadAsync<ConnectionResponse>();

        failed.LastTest.ShouldNotBeNull().Status.ShouldBe(ConnectionTestStatus.Failed);
        failed.LastTest.Message.ShouldBe("invalid_credentials: The provider rejected the API key.");
        succeeded.LastTest.ShouldNotBeNull().Status.ShouldBe(ConnectionTestStatus.Succeeded);
        crashed.LastTest.ShouldNotBeNull().Message.ShouldBe("The adapter failed; the server log has the details.");
        fake.Contexts.Count.ShouldBe(3);
        var context = fake.Contexts.ElementAt(1);
        (context.ConnectionId, context.Environment, context.Credential("apiKey"), context.Setting("region")).ShouldBe((connection.Id, ProviderEnvironment.Sandbox, ApiKey, "tr"));
        context.NotificationUrl.ShouldBe(connection.NotificationUrl);
    }

    [Fact]
    [Trait("Requirement", "PAY-4")]
    public async Task Routing_GivesEachCurrencyOneEnabledConnection_ThatSupportsIt()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        await CreateAsync(client, "fake-main");
        await CreateAsync(client, "fake-off");
        using var disable = await PatchAsync(client, "/api/v1/provider-connections/fake-off", new { enabled = false });

        using var moved = await PutAsync(client, new() { ["TRY"] = "fake-main", ["USD"] = "simulator" });
        using var unsupported = await PutAsync(client, new() { ["GBP"] = "fake-main" });
        using var unknownCurrency = await PutAsync(client, new() { ["XYZ"] = "fake-main" });
        using var unknownConnection = await PutAsync(client, new() { ["TRY"] = "nothing" });
        using var disabled = await PutAsync(client, new() { ["TRY"] = "fake-off" });
        var routing = await (await client.GetPathAsync("/api/v1/provider-connections/routing")).ReadAsync<Dictionary<string, string>>();
        var main = await (await client.GetPathAsync("/api/v1/provider-connections/fake-main")).ReadAsync<ConnectionResponse>();

        moved.StatusCode.ShouldBe(HttpStatusCode.OK, await moved.Content.ReadAsStringAsync(CancellationToken));
        routing.ShouldBe(new Dictionary<string, string> { ["TRY"] = "fake-main", ["USD"] = "simulator" });
        main.Currencies.ShouldBe(["TRY"]);
        (await unsupported.ProblemCodeAsync()).ShouldBe("connection.currency_not_supported");
        (await unknownCurrency.ProblemCodeAsync()).ShouldBe("currency.unsupported");
        (await unknownConnection.ProblemCodeAsync()).ShouldBe("connection.not_found");
        (await disabled.ProblemCodeAsync()).ShouldBe("connection.disabled");
        var audit = await host.QueryAsync<string>($"SELECT data::text AS \"Value\" FROM tahsil.audit_events WHERE action = 'connection.routing_updated'");
        var change = JsonNode.Parse(audit.ShouldHaveSingleItem())!;
        change["before"]!["TRY"]!.GetValue<string>().ShouldBe("simulator");
        change["after"]!["TRY"]!.GetValue<string>().ShouldBe("fake-main");
        change["after"]!.AsObject().ContainsKey("EUR").ShouldBeFalse();
    }

    [Fact]
    [Trait("Requirement", "PAY-6")]
    [Trait("Requirement", "DOM-9")]
    public async Task UsedConnections_CannotBeDeleted()
    {
        var usage = new FakeUsage();
        await using var host = Host(services => services.AddSingleton<IConnectionUsage>(usage));
        using var client = await ClientAsync(host);
        var used = await CreateAsync(client, "fake-used");
        await CreateAsync(client, "fake-unused");
        using var routed = await PutAsync(client, new() { ["TRY"] = "fake-unused" });
        usage.Used.Add(used.Id);

        using var refused = await client.DeleteAsync(new Uri("/api/v1/provider-connections/fake-used", UriKind.Relative), CancellationToken);
        using var deleted = await client.DeleteAsync(new Uri("/api/v1/provider-connections/fake-unused", UriKind.Relative), CancellationToken);

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.ProblemCodeAsync()).ShouldBe("connection.in_use");
        routed.StatusCode.ShouldBe(HttpStatusCode.OK);
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await (await client.GetPathAsync("/api/v1/provider-connections/routing")).ReadAsync<Dictionary<string, string>>()).ContainsKey("TRY").ShouldBeFalse();
        (await host.QueryAsync<string>($"SELECT data->>'key' AS \"Value\" FROM tahsil.audit_events WHERE action = 'connection.deleted'")).ShouldBe(["fake-unused"]);
    }

    [Fact]
    public async Task Providers_DescribeTheirFieldsAndCapabilities()
    {
        await using var host = Host();
        using var client = await host.CreateClientWithAsync(TahsilPermissions.ConnectionsRead);

        var providers = await (await client.GetPathAsync("/api/v1/providers")).ReadAsync<Page<ProviderResponse>>();

        providers.Items.Select(provider => provider.Key).ShouldBe(["fakepay", "iyzico", "paytr", "simulator", "stripe"]);
        var fake = providers.Items[0];
        fake.Currencies.ShouldBe(["TRY", "USD"]);
        fake.Credentials.Select(field => (field.Key, field.Type, field.Required)).ShouldBe([("apiKey", "secret", true), ("secretKey", "secret", true), ("webhookSecret", "secret", false)]);
        fake.Settings.Select(field => (field.Key, field.Required)).ShouldBe([("region", false), ("timeoutSeconds", false), ("allowDirectForm", false)]);
        fake.Capabilities.Entry.ShouldBe(["redirect", "direct_form"]);
        fake.Capabilities.Setup.ShouldBe("verification_charge");
        fake.Environments.ShouldBe([ConnectionEnvironment.Sandbox, ConnectionEnvironment.Production]);
    }

    private static async Task<HttpClient> ClientAsync(WebApplicationFactory<Program> host) =>
        await host.CreateClientWithAsync(TahsilPermissions.ConnectionsRead, TahsilPermissions.ConnectionsWrite);

    private static CreateConnectionRequest Request(
        string key,
        IReadOnlyDictionary<string, string>? credentials = null,
        object? settings = null,
        ConnectionEnvironment environment = ConnectionEnvironment.Sandbox,
        string provider = "fakepay") => new()
        {
            Key = key,
            Provider = provider,
            Name = $"Connection {key}",
            Environment = environment,
            Credentials = credentials ?? new Dictionary<string, string> { ["apiKey"] = ApiKey, ["secretKey"] = SecretKey },
            Settings = settings is null ? null : JsonSerializer.SerializeToElement(settings).Deserialize<Dictionary<string, JsonElement>>(),
        };

    private static async Task<ConnectionResponse> CreateAsync(
        HttpClient client, string key, IReadOnlyDictionary<string, string>? credentials = null, object? settings = null)
    {
        using var response = await client.PostJsonAsync("/api/v1/provider-connections", Request(key, credentials, settings));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<ConnectionResponse>();
    }

    private static async Task<IReadOnlyList<ConnectionResponse>> ListAsync(HttpClient client) =>
        (await (await client.GetPathAsync("/api/v1/provider-connections")).ReadAsync<Page<ConnectionResponse>>()).Items;

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Dictionary<string, string> routing) =>
        client.PutAsJsonAsync(new Uri("/api/v1/provider-connections/routing", UriKind.Relative), routing, ApiClients.Json, CancellationToken);

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, string path, object body, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body, options: ApiClients.Json) };
        if (ifMatch is not null)
        {
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(ifMatch));
        }

        return await client.SendAsync(request, CancellationToken);
    }

    private WebApplicationFactory<Program> Host(
        Action<IServiceCollection>? services = null, string? database = null, string mode = "Test", FakeProvider? provider = null, bool migrateOnStartup = true)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Tahsil"] = database ?? server.NewDatabase("connections"),
            ["Tahsil:Server:Mode"] = mode,
            ["Tahsil:Database:MigrateOnStartup"] = migrateOnStartup ? "true" : "false",
        };
        if (mode == "Live")
        {
            settings["Tahsil:Server:PublicUrl"] = "https://billing.example.com/";
            settings["Tahsil:Server:RequireHttps"] = "true";
        }

        return server.WithSettings(settings, collection =>
        {
            collection.AddSingleton<IPaymentProvider>(provider ?? new FakeProvider("fakepay", "TRY", "USD"));
            services?.Invoke(collection);
        });
    }

    private sealed class FakeUsage : IConnectionUsage
    {
        public HashSet<Guid> Used { get; } = [];

        public Task<bool> IsUsedAsync(Guid connectionId, CancellationToken cancellationToken) => Task.FromResult(Used.Contains(connectionId));
    }
}
