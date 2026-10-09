namespace Tahsil.Server.Tests.Hosting;

[Trait("Requirement", "FND-10")]
public sealed class ModeHeaderTests
{
    [Fact]
    public async Task TestMode_MarksResponses()
    {
        using var host = new TestHost();
        using var client = host.CreateClient();

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        response.Headers.GetValues("Tahsil-Mode").ShouldBe(["test"]);
    }

    [Fact]
    public async Task LiveMode_DoesNotMarkResponses()
    {
        using var host = new TestHost(new Dictionary<string, string?>
        {
            ["Tahsil:Server:Mode"] = "Live",
            ["Tahsil:Server:PublicUrl"] = "https://billing.example.com/",
            ["Tahsil:Server:RequireHttps"] = "true",
        });
        using var client = host.CreateClient();

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        response.Headers.Contains("Tahsil-Mode").ShouldBeFalse();
    }
}
