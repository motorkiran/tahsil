using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Tahsil.Application.Common;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Server.Tests;

/// <summary>
/// Hosts with a database of their own and a billing clock the test moves, whose workers do not run on their own:
/// the test drives them, so what happens in time is deterministic.
/// </summary>
internal static class IsolatedHosts
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

    /// <summary>
    /// Runs every worker until none finds anything due at the billing clock's current time, as the hosted workers
    /// would, but within the test.
    /// </summary>
    public static async Task RunWorkersUntilIdleAsync(this WebApplicationFactory<Program> host)
    {
        var workers = host.Services.GetServices<IHostedService>().OfType<IRunnableWorker>().ToList();
        int processed;
        do
        {
            processed = 0;
            foreach (var worker in workers)
            {
                processed += await worker.RunBatchAsync(TestContext.Current.CancellationToken);
            }
        }
        while (processed > 0);
    }

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
