using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Application.Common;
using Tahsil.Infrastructure.Persistence;

namespace Tahsil.Server.Tests.Workers;

/// <summary>Hosts whose workers do not run on their own: tests drive them, on a fake billing clock.</summary>
internal static class WorkerHosts
{
    public static WebApplicationFactory<Program> Create(
        TahsilServerFixture server, string database, FakeTimeProvider time, Action<IServiceCollection>? services = null) =>
        server.WithSettings(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Tahsil"] = database,
                ["Tahsil:Workers:Enabled"] = "false",
            },
            collection =>
            {
                collection.AddSingleton<BillingClock>(new FakeBillingClock(time));
                services?.Invoke(collection);
            });

    public static async Task ExecuteSqlAsync(this WebApplicationFactory<Program> host, FormattableString sql)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TahsilDbContext>().Database.ExecuteSqlAsync(sql, TestContext.Current.CancellationToken);
    }

    /// <summary>Runs SQL that takes no parameters, such as DDL.</summary>
    public static async Task ExecuteRawAsync(this WebApplicationFactory<Program> host, string sql)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TahsilDbContext>().Database.ExecuteSqlRawAsync(sql, TestContext.Current.CancellationToken);
    }

    public static async Task<List<T>> QueryAsync<T>(this WebApplicationFactory<Program> host, FormattableString sql)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TahsilDbContext>().Database.SqlQuery<T>(sql).ToListAsync(TestContext.Current.CancellationToken);
    }
}
