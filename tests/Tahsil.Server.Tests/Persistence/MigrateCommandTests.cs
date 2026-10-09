using System.Diagnostics;
using Npgsql;

namespace Tahsil.Server.Tests.Persistence;

[Trait("Requirement", "FND-18")]
public sealed class MigrateCommandTests(TahsilServerFixture server)
{
    [Fact]
    public async Task Migrate_AppliesMigrations_AndExits()
    {
        var database = server.NewDatabase("migrate");
        var start = new ProcessStartInfo("dotnet", [Path.Combine(AppContext.BaseDirectory, "Tahsil.Server.dll"), "migrate"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var (key, value) in TestConfiguration.Create(database))
        {
            start.Environment[key.Replace(":", "__", StringComparison.Ordinal)] = value;
        }

        start.Environment["ASPNETCORE_ENVIRONMENT"] = TestConfiguration.Environment;

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(1));
        await process.WaitForExitAsync(timeout.Token);

        process.ExitCode.ShouldBe(0, await output + await errors);
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM tahsil.__ef_migrations_history", connection);
        ((long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBeGreaterThan(0);
    }
}
