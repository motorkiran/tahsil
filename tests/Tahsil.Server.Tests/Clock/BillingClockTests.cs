using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tahsil.Application.Common;
using Tahsil.Infrastructure.Persistence;

namespace Tahsil.Server.Tests.Clock;

[Trait("Requirement", "ENG-21")]
[Trait("Requirement", "FND-34")]
public sealed class BillingClockTests(TahsilServerFixture server)
{
    private static readonly TimeSpan OneDay = TimeSpan.FromDays(1);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InTestMode_TheBillingClock_IncludesTheTestClockOffset_AndAspNetCoreTimeDoesNot()
    {
        await using var host = server.WithSettings(new Dictionary<string, string?> { ["ConnectionStrings:Tahsil"] = server.NewDatabase("clock") });
        await TravelAsync(host, OneDay);

        await WaitUntilAsync(() => Offset(host) > TimeSpan.FromHours(23));

        Offset(host).ShouldBe(OneDay, TimeSpan.FromMinutes(1));
        host.Services.GetRequiredService<TimeProvider>().ShouldNotBeAssignableTo<BillingClock>();
    }

    [Fact]
    public async Task InLiveMode_TheTestClockIsIgnored()
    {
        await using var host = server.WithSettings(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Tahsil"] = server.NewDatabase("liveclock"),
            ["Tahsil:Server:Mode"] = "Live",
            ["Tahsil:Server:PublicUrl"] = "https://billing.example.com/",
            ["Tahsil:Server:RequireHttps"] = "true",
        });
        await TravelAsync(host, OneDay);

        await Task.Delay(TimeSpan.FromSeconds(1.5), CancellationToken);

        Offset(host).ShouldBe(TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    private static TimeSpan Offset(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<BillingClock>().GetUtcNow() - host.Services.GetRequiredService<TimeProvider>().GetUtcNow();

    private static async Task TravelAsync(WebApplicationFactory<Program> host, TimeSpan offset)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TahsilDbContext>();
        var seconds = (long)offset.TotalSeconds;
        var actor = """{"type":"system","id":null}""";
        await context.Database.ExecuteSqlAsync(
            $"INSERT INTO tahsil.test_clock (id, offset_seconds, updated_at, actor) VALUES (1, {seconds}, now(), {actor}::jsonb)",
            CancellationToken);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
        }
    }
}
