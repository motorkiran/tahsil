using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Infrastructure.Auditing;
using Tahsil.Infrastructure.Clock;
using Tahsil.Infrastructure.Outbox;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Security;
using Tahsil.Infrastructure.Workers;

namespace Tahsil.Infrastructure;

public static class DependencyInjection
{
    private const string MigrationsHistoryTable = "__ef_migrations_history";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        AddPersistence(services);
        AddSecurity(services);
        AddBillingClock(services);
        AddWorkers(services);
        AddOutbox(services);
        AddAuditing(services);

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
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(serviceProvider.GetRequiredService<OutboxSignalInterceptor>()));

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
            .Validate(
                options => SecurityOptions.IsValidKeyId(options.MasterKeyId),
                $"{SecurityOptions.SectionName}:MasterKeyId must be between 1 and 255.")
            .Validate(
                options => options.HasValidPreviousKeys(),
                $"{SecurityOptions.SectionName}:PreviousMasterKeys must map IDs between 1 and 255, other than MasterKeyId, to base64-encoded 256-bit keys.")
            // Reads the mode from configuration: resolving the server options here would report their own
            // validation failures a second time.
            .Validate<IConfiguration>(
                (options, configuration) => ReadMode(configuration) != TahsilMode.Live || !options.IsDevelopmentMasterKey(),
                $"{SecurityOptions.SectionName}:MasterKey is the public development key, which Live mode refuses. Generate one with 'openssl rand -base64 32'.")
            .ValidateOnStart();

        services.AddSingleton<ISecretProtector, SecretProtector>();
        services.AddSingleton<ISecretEncryption, SecretEncryption>();
        services.AddSingleton<IEncryptedStore, DataProtectionKeyRingStore>();
        services.AddScoped<SecretReencryption>();

        // Every instance shares one key ring, stored in PostgreSQL and encrypted with the master key.
        services.AddDataProtection()
            .SetApplicationName("Tahsil")
            .PersistKeysToDbContext<TahsilDbContext>();
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>, ConfigureKeyRingEncryption>();
    }

    private static void AddBillingClock(IServiceCollection services)
    {
        services.AddSingleton<TestClockOffset>();
        services.AddSingleton<BillingClock, OffsetBillingClock>();
        services.AddHostedService<TestClockRefresher>();
    }

    private static void AddWorkers(IServiceCollection services)
    {
        services.AddOptions<WorkerOptions>()
            .BindConfiguration(WorkerOptions.SectionName)
            .Validate(
                options => options.IsValid(),
                $"{WorkerOptions.SectionName}: the polling interval must be positive, the batch size between 1 and 1000, the lease at least 30 seconds and the payment concurrency between 1 and 64.")
            .ValidateOnStart();

        services.AddSingleton<WorkerSignals>();
        services.AddSingleton<IWorkerSignals>(provider => provider.GetRequiredService<WorkerSignals>());
    }

    private static void AddOutbox(IServiceCollection services)
    {
        services.AddOptions<OutboxOptions>()
            .BindConfiguration(OutboxOptions.SectionName)
            .Validate(options => options.IsValid(), $"{OutboxOptions.SectionName}: intervals must be positive and the batch size between 1 and 1000.")
            .ValidateOnStart();

        services.AddSingleton(new OutboxMessageTypes([typeof(Application.DependencyInjection).Assembly]));
        services.AddScoped<OutboxSignalInterceptor>();
        services.AddScoped<IOutbox, Outbox.Outbox>();
        AddHostedSingleton<OutboxDispatcher>(services);
        AddHostedSingleton<OutboxCleanup>(services);
    }

    private static void AddAuditing(IServiceCollection services)
    {
        services.AddOptions<AuditOptions>()
            .BindConfiguration(AuditOptions.SectionName)
            .Validate(options => options.IsValid(), $"{AuditOptions.SectionName}:RetentionPeriod must be at least a day.")
            .ValidateOnStart();

        services.AddScoped<IAuditLog, AuditLog>();
        AddHostedSingleton<AuditRetention>(services);
    }

    /// <summary>Registers a background service that can also be resolved by its type, so tests can run it directly.</summary>
    private static void AddHostedSingleton<TService>(IServiceCollection services)
        where TService : class, IHostedService
    {
        services.AddSingleton<TService>();
        services.AddHostedService(provider => provider.GetRequiredService<TService>());
    }

    private static TahsilMode ReadMode(IConfiguration configuration) =>
        configuration.GetSection(ServerOptions.SectionName).GetValue<TahsilMode>(nameof(ServerOptions.Mode));

    private static string? GetConnectionString(IConfiguration configuration) =>
        configuration.GetConnectionString(DatabaseOptions.ConnectionStringName);
}
