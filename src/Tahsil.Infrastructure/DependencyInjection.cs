using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tahsil.Application.Common;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Security;

namespace Tahsil.Infrastructure;

public static class DependencyInjection
{
    private const string MigrationsHistoryTable = "__ef_migrations_history";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        AddPersistence(services);
        AddSecurity(services);

        return services;
    }

    private static void AddPersistence(IServiceCollection services)
    {
        services.AddOptions<DatabaseOptions>()
            .BindConfiguration(DatabaseOptions.SectionName)
            .Validate<IConfiguration>(
                (_, configuration) => !string.IsNullOrWhiteSpace(GetConnectionString(configuration)),
                $"The connection string 'ConnectionStrings:{DatabaseOptions.ConnectionStringName}' is not configured.")
            .ValidateOnStart();

        // The connection string is resolved when a context is created, so configuration
        // added late (integration tests, design-time tools) is honored.
        services.AddDbContext<TahsilDbContext>((serviceProvider, options) => options
            .UseNpgsql(
                GetConnectionString(serviceProvider.GetRequiredService<IConfiguration>()),
                npgsql => npgsql.MigrationsHistoryTable(MigrationsHistoryTable, TahsilDbContext.Schema))
            .UseSnakeCaseNamingConvention());

        services.AddScoped<ITahsilDbContext>(provider => provider.GetRequiredService<TahsilDbContext>());
        services.AddScoped<DatabasePreparation>();
    }

    private static void AddSecurity(IServiceCollection services)
    {
        services.AddOptions<SecurityOptions>()
            .BindConfiguration(SecurityOptions.SectionName)
            .Validate(
                options => SecurityOptions.IsValidMasterKey(options.MasterKey),
                $"{SecurityOptions.SectionName}:MasterKey must be a base64-encoded 256-bit key. Generate one with 'openssl rand -base64 32'.")
            // Reads the mode from configuration: resolving the server options here would report their own
            // validation failures a second time.
            .Validate<IConfiguration>(
                (options, configuration) => ReadMode(configuration) != TahsilMode.Live || !options.IsDevelopmentMasterKey(),
                $"{SecurityOptions.SectionName}:MasterKey is the public development key, which Live mode refuses. Generate one with 'openssl rand -base64 32'.")
            .ValidateOnStart();
    }

    private static TahsilMode ReadMode(IConfiguration configuration) =>
        configuration.GetSection(ServerOptions.SectionName).GetValue<TahsilMode>(nameof(ServerOptions.Mode));

    private static string? GetConnectionString(IConfiguration configuration) =>
        configuration.GetConnectionString(DatabaseOptions.ConnectionStringName);
}
