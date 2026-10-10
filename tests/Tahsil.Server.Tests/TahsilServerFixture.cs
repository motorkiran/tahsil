using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Server.Hosting;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Tahsil.Server.Tests.TahsilServerFixture))]

namespace Tahsil.Server.Tests;

/// <summary>
/// Hosts Tahsil in memory against a disposable PostgreSQL container shared by every test in the assembly.
/// </summary>
public sealed class TahsilServerFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string PostgreSqlImage = "docker.io/library/postgres:18-alpine";

    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(30);

    // Tests in parallel each open a few connections, many to databases of their own.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(PostgreSqlImage)
        .WithCommand("-c", "max_connections=1000")
        .Build();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        // Starting the host migrates the database; tests begin once the instance reports ready.
        await WaitUntilReadyAsync(this);
    }

    /// <summary>Waits until <paramref name="factory"/> answers its readiness probe; also for hosts derived from this one.</summary>
    public static async Task WaitUntilReadyAsync(WebApplicationFactory<Program> factory)
    {
        using var client = factory.CreateClient();
        using var timeout = new CancellationTokenSource(ReadinessTimeout);

        while (true)
        {
            using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), timeout.Token);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
        }
    }

    /// <summary>A connection string for another database on the same server.</summary>
    /// <summary>
    /// A connection string for one of the tests' databases. Hundreds of hosts each get a pool of their own, so idle
    /// connections are closed within seconds instead of the default five minutes, before the server runs out of them.
    /// </summary>
    public string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            Database = database,
            ConnectionIdleLifetime = 2,
            ConnectionPruningInterval = 1,
        }.ConnectionString;

    /// <summary>A connection string for a database no other test uses; it is created by whoever connects first.</summary>
    public string NewDatabase(string prefix) => ConnectionStringFor($"{prefix}_{Guid.CreateVersion7():N}");

    /// <summary>Runs <paramref name="action"/> with a fresh database context in its own scope.</summary>
    public async Task<T> QueryDatabaseAsync<T>(Func<TahsilDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TahsilDbContext>());
    }

    /// <summary>
    /// A host on the same PostgreSQL server with settings of its own (such as another database or master key) and
    /// optional service replacements. The caller disposes it.
    /// </summary>
    public WebApplicationFactory<Program> WithSettings(
        IReadOnlyDictionary<string, string?> settings,
        Action<IServiceCollection>? services = null,
        Action<IEndpointRouteBuilder>? endpoints = null,
        string? environment = null) =>
        WithWebHostBuilder(builder =>
        {
            if (environment is not null)
            {
                builder.UseEnvironment(environment);
            }

            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            builder.ConfigureTestServices(collection =>
            {
                services?.Invoke(collection);
                if (endpoints is not null)
                {
                    collection.AddSingleton<IEndpointMapper>(new TestEndpoints(endpoints));
                }
            });
        });

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
        .UseEnvironment(TestConfiguration.Environment)
        .ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(TestConfiguration.Create(_postgres.GetConnectionString())));

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
