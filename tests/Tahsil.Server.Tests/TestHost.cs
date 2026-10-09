using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Tahsil.Server.Tests;

/// <summary>
/// An in-memory Tahsil host with the test configuration and per-test overrides, for tests of startup, configuration
/// and request plumbing. It does not touch the database.
/// </summary>
internal sealed class TestHost(IReadOnlyDictionary<string, string?>? overrides = null, Action<IEndpointRouteBuilder>? endpoints = null)
    : WebApplicationFactory<Program>
{
    private const string UnusedDatabase = "Host=localhost;Port=1;Database=unused;Username=unused;Password=unused";

    public CapturingLoggerProvider Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var settings = TestConfiguration.Create(UnusedDatabase);
        settings["Tahsil:Database:MigrateOnStartup"] = "false";
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        builder.UseEnvironment(TestConfiguration.Environment);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));

        if (endpoints is not null)
        {
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new TestEndpoints(endpoints)));
        }
    }
}
