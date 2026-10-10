using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Tahsil.Application.Provisioning;
using Tahsil.Contracts;

namespace Tahsil.Server.Tests.Provisioning;

public sealed class ProvisioningTests(TahsilServerFixture server)
{
    /// <summary>Set to rewrite the snapshot after an intended change to the document.</summary>
    private const string UpdateSnapshotVariable = "TAHSIL_UPDATE_SNAPSHOTS";

    private static readonly string ProjectDirectory = typeof(ProvisioningTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(metadata => metadata.Key == "ProjectDirectory").Value!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requirement", "PRV-4")]
    [Trait("Requirement", "PRV-10")]
    public async Task ADocument_IsApplied_ThenFoundUnchanged_ThenUpdated()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var document = await ExampleAsync();

        var first = await ApplyAsync(client, document);
        var again = await ApplyAsync(client, document);
        var renamed = await ApplyAsync(client, document with
        {
            Products = [document.Products![0] with { Name = "Pro Plus" }, document.Products[1]],
        });

        (first.Created, first.Updated, first.Unchanged).ShouldBe((8, 0, 0));
        (again.Created, again.Updated, again.Unchanged).ShouldBe((0, 0, 8));
        (renamed.Updated, renamed.Unchanged).ShouldBe((1, 7));
        renamed.Items.Single(item => item.Result == ProvisioningItemResult.Updated).Path.ShouldBe("products[0]");
        var legacy = await (await client.GetPathAsync("/api/v1/products/legacy")).ReadAsync<ProductResponse>();
        legacy.IsArchived.ShouldBeTrue();
        (await (await client.GetPathAsync("/api/v1/prices/legacy-monthly-try")).ReadAsync<PriceResponse>()).IsArchived.ShouldBeTrue();
        (await host.QueryAsync<int>($"SELECT count(*)::int AS \"Value\" FROM tahsil.audit_events WHERE action = 'provisioning.applied'")).ShouldBe([2]);
    }

    [Fact]
    [Trait("Requirement", "PRV-5")]
    [Trait("Requirement", "PRV-6")]
    public async Task ChangingAFixedProperty_NamesTheItem_AndAppliesNothing()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);
        var document = await ExampleAsync();
        await ApplyAsync(client, document);
        var pro = document.Products![0];

        using var response = await client.PostJsonAsync("/api/v1/provisioning", document with
        {
            TaxRates = [document.TaxRates![0] with { Name = "KDV %20" }],
            Products = [pro with { Prices = [pro.Prices![0] with { UnitAmount = 34900 }, .. pro.Prices.Skip(1)] }, document.Products[1]],
        });
        var problem = await response.ReadAsync<JsonNode>();

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        problem["code"]!.GetValue<string>().ShouldBe("provisioning.fixed_property");
        problem["detail"]!.GetValue<string>().ShouldStartWith("products[0].prices[0] 'pro-monthly-try': 'unitAmount' cannot change");
        (await (await client.GetPathAsync("/api/v1/tax-rates/kdv-20")).ReadAsync<TaxRateResponse>()).Name.ShouldBe("KDV");
    }

    [Fact]
    [Trait("Requirement", "PRV-9")]
    public async Task ADryRun_ReportsTheChanges_AndKeepsNone()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);

        using var response = await client.PostJsonAsync("/api/v1/provisioning?dryRun=true", await ExampleAsync());
        var result = await response.ReadAsync<ProvisioningResult>();

        result.Created.ShouldBe(8);
        (await (await client.GetPathAsync("/api/v1/products")).ReadAsync<Page<ProductResponse>>()).Items.ShouldBeEmpty();
        (await host.QueryAsync<int>($"SELECT count(*)::int AS \"Value\" FROM tahsil.audit_events WHERE action NOT IN ('api_key.created', 'connection.created')")).ShouldBe([0]);
    }

    [Fact]
    [Trait("Requirement", "PRV-11")]
    public async Task AnExport_AppliedElsewhere_ExportsTheSame()
    {
        await using var source = Host();
        await using var target = Host();
        using var sourceClient = await ClientAsync(source);
        using var targetClient = await ClientAsync(target);
        await ApplyAsync(sourceClient, await ExampleAsync());

        var exported = await (await sourceClient.GetPathAsync("/api/v1/provisioning")).ReadAsync<ProvisioningDocument>();
        await ApplyAsync(targetClient, exported);
        var again = await (await targetClient.GetPathAsync("/api/v1/provisioning")).ReadAsync<ProvisioningDocument>();

        exported.Products!.Select(product => product.Key).ShouldBe(["legacy", "pro"]);
        exported.Products![1].Prices!.Select(price => price.Key).ShouldBe(["api-calls-try", "pro-monthly-try", "pro-yearly-try"]);
        JsonSerializer.Serialize(again, ApiClients.Json).ShouldBe(JsonSerializer.Serialize(exported, ApiClients.Json));
    }

    [Fact]
    [Trait("Requirement", "PRV-8")]
    public async Task TheStartupFile_IsAppliedWhenTheDatabaseIsPrepared()
    {
        await using var host = Host(new Dictionary<string, string?> { ["Tahsil:Provisioning:FilePath"] = ExamplePath });
        using var client = await host.CreateClientWithAsync(TahsilPermissions.CatalogRead);

        (await (await client.GetPathAsync("/api/v1/prices/pro-monthly-try")).ReadAsync<PriceResponse>()).UnitAmount.ShouldBe(29900);
    }

    [Fact]
    [Trait("Requirement", "PRV-1")]
    public async Task UnknownMembers_AreRejected()
    {
        await using var host = Host();
        using var client = await ClientAsync(host);

        using var response = await client.PostAsync(
            new Uri("/api/v1/provisioning", UriKind.Relative),
            new StringContent("""{ "taxRates": [{ "key": "kdv", "name": "KDV", "percentage": 20, "inclusive": true, "rate": 20 }] }""", System.Text.Encoding.UTF8, "application/json"),
            CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<ProvisioningDocument>("""{ "coupons": [] }""", ProvisioningFile.SerializerOptions));
    }

    [Fact]
    [Trait("Requirement", "PRV-3")]
    public async Task TheSchema_MatchesTheSnapshot()
    {
        var schema = ProvisioningFile.Schema().ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var path = Path.GetFullPath(Path.Combine(ProjectDirectory, "..", "..", "docs", "schemas", "provisioning.schema.json"));

        if (Environment.GetEnvironmentVariable(UpdateSnapshotVariable) is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, schema + "\n", CancellationToken);
        }

        File.Exists(path).ShouldBeTrue($"No schema yet; run the tests with {UpdateSnapshotVariable}=1 to create it.");
        JsonNode.DeepEquals(JsonNode.Parse(schema), JsonNode.Parse(await File.ReadAllTextAsync(path, CancellationToken))).ShouldBeTrue(
            $"The provisioning document changed. Run the tests with {UpdateSnapshotVariable}=1 and commit the new schema.");
    }

    private static string ExamplePath => Path.Combine(ProjectDirectory, "Provisioning", "example.json");

    private static Task<ProvisioningDocument> ExampleAsync() => ProvisioningFile.ReadAsync(ExamplePath, CancellationToken);

    private static async Task<ProvisioningResult> ApplyAsync(HttpClient client, ProvisioningDocument document)
    {
        using var response = await client.PostAsJsonAsync(new Uri("/api/v1/provisioning", UriKind.Relative), document, ApiClients.Json, CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CancellationToken));
        return await response.ReadAsync<ProvisioningResult>();
    }

    private static Task<HttpClient> ClientAsync(WebApplicationFactory<Program> host) => host.CreateClientWithAsync(
        TahsilPermissions.ProvisioningRead, TahsilPermissions.ProvisioningWrite, TahsilPermissions.CatalogRead, TahsilPermissions.CatalogWrite);

    private WebApplicationFactory<Program> Host(Dictionary<string, string?>? settings = null)
    {
        settings ??= [];
        settings["ConnectionStrings:Tahsil"] = server.NewDatabase("provisioning");
        return server.WithSettings(settings);
    }
}
