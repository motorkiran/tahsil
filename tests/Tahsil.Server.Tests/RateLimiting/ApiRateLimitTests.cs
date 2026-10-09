using System.Globalization;
using System.Net;
using Tahsil.Contracts;

namespace Tahsil.Server.Tests.RateLimiting;

[Trait("Requirement", "API-14")]
public sealed class ApiRateLimitTests(TahsilServerFixture server)
{
    [Fact]
    public async Task AKeyOverItsLimit_IsToldWhenToRetry_WhileOtherKeysAreUnaffected()
    {
        await using var host = server.WithSettings(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Tahsil"] = server.NewDatabase("ratelimits"),
            ["Tahsil:RateLimits:ApiRequestsPerSecond"] = "1",
            ["Tahsil:RateLimits:ApiBurst"] = "2",
        });
        using var busy = await host.CreateClientWithAsync(TahsilPermissions.ApiKeysRead);
        using var quiet = await host.CreateClientWithAsync(TahsilPermissions.ApiKeysRead);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => busy.GetPathAsync("/api/v1/api-keys")));
        using var other = await quiet.GetPathAsync("/api/v1/api-keys");

        var limited = responses.Where(response => response.StatusCode == HttpStatusCode.TooManyRequests).ToList();
        limited.ShouldNotBeEmpty();
        responses.Count(response => response.StatusCode == HttpStatusCode.OK).ShouldBeLessThanOrEqualTo(3);
        int.Parse(limited[0].Headers.GetValues("Retry-After").Single(), CultureInfo.InvariantCulture).ShouldBeGreaterThanOrEqualTo(1);
        (await limited[0].ProblemCodeAsync()).ShouldBe("rate_limited");
        other.StatusCode.ShouldBe(HttpStatusCode.OK);

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }
}
