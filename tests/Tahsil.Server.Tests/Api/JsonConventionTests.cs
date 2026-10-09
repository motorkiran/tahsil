using System.Text.Json;
using Tahsil.Contracts.Serialization;
using Tahsil.Server.Tests.Persistence;

namespace Tahsil.Server.Tests.Api;

[Trait("Requirement", "API-2")]
public sealed class JsonConventionTests
{
    private static readonly JsonSerializerOptions Options = TahsilJson.Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public void Enums_AreSnakeCaseStrings_AndPropertiesCamelCase()
    {
        JsonSerializer.Serialize(new Sample(TestStatus.PastDue), Options).ShouldBe("{\"currentStatus\":\"past_due\"}");
        JsonSerializer.Deserialize<Sample>("{\"currentStatus\":\"past_due\"}", Options)!.CurrentStatus.ShouldBe(TestStatus.PastDue);
    }

    [Theory]
    [InlineData("{\"currentStatus\":1}")]
    [InlineData("{\"currentStatus\":\"1\"}")]
    [InlineData("{\"currentStatus\":\"unknown\"}")]
    public void EnumsAsNumbers_OrUnknownValues_AreRejected(string json)
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<Sample>(json, Options));
    }

    private sealed record Sample(TestStatus CurrentStatus);
}
