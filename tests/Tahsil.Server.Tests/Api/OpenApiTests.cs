using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;

namespace Tahsil.Server.Tests.Api;

[Trait("Requirement", "FND-54")]
public sealed class OpenApiTests
{
    /// <summary>Set to rewrite the snapshot after an intended change to the API.</summary>
    private const string UpdateSnapshotVariable = "TAHSIL_UPDATE_SNAPSHOTS";

    [Fact]
    [Trait("Requirement", "API-15")]
    public async Task Document_MatchesTheSnapshot()
    {
        using var host = new TestHost();
        using var http = host.CreateClient();
        var document = await http.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        var snapshotPath = SnapshotPath();

        if (Environment.GetEnvironmentVariable(UpdateSnapshotVariable) is not null)
        {
            await File.WriteAllTextAsync(snapshotPath, document, TestContext.Current.CancellationToken);
        }

        File.Exists(snapshotPath).ShouldBeTrue($"No snapshot yet; run the tests with {UpdateSnapshotVariable}=1 to create it.");
        var snapshot = await File.ReadAllTextAsync(snapshotPath, TestContext.Current.CancellationToken);

        JsonNode.DeepEquals(JsonNode.Parse(document), JsonNode.Parse(snapshot)).ShouldBeTrue(
            $"The Tahsil API changed. Make sure the change is not breaking, then run the tests with {UpdateSnapshotVariable}=1 "
            + "and commit the new snapshot.");
    }

    [Fact]
    [Trait("Requirement", "API-2")]
    [Trait("Requirement", "API-16")]
    public async Task Document_UsesSnakeCaseEnums_AndNamesThePermissionOfEachOperation()
    {
        using var host = new TestHost();
        using var http = host.CreateClient();

        var document = JsonNode.Parse(await http.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken))!;

        var enumValues = document["components"]!["schemas"]!.AsObject()
            .SelectMany(schema => schema.Value!["enum"]?.AsArray() ?? [])
            .OfType<JsonNode>()
            .Select(value => value.GetValue<string>());
        enumValues.ShouldNotBeEmpty();
        enumValues.ShouldAllBe(value => System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-z][a-z0-9_]*$"));

        var operations = document["paths"]!.AsObject().SelectMany(path => path.Value!.AsObject().Select(operation => operation.Value!));
        operations.ShouldAllBe(operation => operation["description"]!.GetValue<string>().Contains("Requires the `tahsil.")
            && operation["security"]![0]!["ApiKey"] != null);
    }

    [Fact]
    [Trait("Requirement", "CAT-3")]
    [Trait("Requirement", "CAT-12")]
    [Trait("Requirement", "CPN-3")]
    public async Task PricesTaxRatesAndCoupons_CannotBeDeleted()
    {
        using var host = new TestHost();
        using var http = host.CreateClient();

        var document = JsonNode.Parse(await http.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken))!;

        // Products without history are deleted with their prices (DOM-3); nothing deletes a price by itself.
        string[] kept = ["/api/v1/prices", "/api/v1/tax-rates", "/api/v1/coupons"];
        var paths = document["paths"]!.AsObject().Where(path => kept.Any(prefix => path.Key.StartsWith(prefix, StringComparison.Ordinal))).ToList();
        paths.Count.ShouldBeGreaterThanOrEqualTo(kept.Length);
        paths.Where(path => path.Value!.AsObject().ContainsKey("delete")).Select(path => path.Key).ShouldBeEmpty();
    }

    [Fact]
    public async Task ApiReference_IsServed()
    {
        using var host = new TestHost();
        using var http = host.CreateClient();

        using var response = await http.GetAsync(new Uri("/scalar/v1", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
    }

    private static string SnapshotPath() => Path.Combine(
        typeof(OpenApiTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(metadata => metadata.Key == "ProjectDirectory").Value!,
        "Api",
        "openapi-v1.json");
}
