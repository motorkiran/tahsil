using System.Net;
using Tahsil.Infrastructure.Persistence;

namespace Tahsil.Server.Tests.Diagnostics;

[Trait("Requirement", "FND-46")]
[Trait("Requirement", "FND-51")]
public sealed class HealthTests(TahsilServerFixture server)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheInstance_IsLiveAndReady()
    {
        using var client = server.CreateClient();

        (await client.GetAsync(new Uri("/health/live", UriKind.Relative), CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(new Uri("/health/ready", UriKind.Relative), CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readiness_FailsWithoutTheDatabase_AndRecovers_WhileLivenessHolds()
    {
        await using var host = server.WithSettings(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Tahsil"] = server.NewDatabase("health"),
            ["Tahsil:Database:MigrateOnStartup"] = "false",
            ["Tahsil:Workers:Enabled"] = "false",
        });
        using var client = host.CreateClient();

        (await client.GetAsync(new Uri("/health/ready", UriKind.Relative), CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await client.GetAsync(new Uri("/health/live", UriKind.Relative), CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await host.Services.PrepareDatabaseAsync(CancellationToken);

        (await client.GetAsync(new Uri("/health/ready", UriKind.Relative), CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
