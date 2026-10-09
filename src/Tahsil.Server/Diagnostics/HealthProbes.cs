using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Tahsil.Infrastructure.Persistence;

namespace Tahsil.Server.Diagnostics;

/// <summary>Liveness and readiness probes for orchestrators and load balancers.</summary>
internal static class HealthProbes
{
    /// <summary>Checks with this tag must pass before the instance receives traffic.</summary>
    public const string ReadinessTag = "ready";

    public static IServiceCollection AddHealthProbes(this IServiceCollection services)
    {
        services.AddSingleton<MigrationState>();
        services.AddHealthChecks()
            .AddCheck<DatabaseReadiness>("database", tags: [ReadinessTag]);

        return services;
    }

    /// <summary>Serves the probes before anything else, so they answer even while nothing else can.</summary>
    public static IApplicationBuilder UseHealthProbes(this IApplicationBuilder app)
    {
        // Liveness only reports that the process is up, so a database outage makes Tahsil unready instead of getting
        // it restarted.
        app.UseHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.UseHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadinessTag) });

        return app;
    }

    /// <summary>
    /// Ready when the database answers and has every migration this version needs; an instance that starts before
    /// another one finished migrating waits instead of failing requests.
    /// </summary>
    private sealed class DatabaseReadiness(TahsilDbContext context, MigrationState state) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext healthCheck, CancellationToken cancellationToken = default)
        {
            try
            {
                if (state.Migrated)
                {
                    return await context.Database.CanConnectAsync(cancellationToken)
                        ? HealthCheckResult.Healthy()
                        : HealthCheckResult.Unhealthy("The database is unreachable.");
                }

                var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
                if (pending > 0)
                {
                    return HealthCheckResult.Unhealthy($"{pending} database migrations are pending.");
                }

                state.Migrated = true;
                return HealthCheckResult.Healthy();
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return HealthCheckResult.Unhealthy("The database is unreachable.", exception);
            }
        }
    }

    /// <summary>Whether this instance has seen its migrations applied; once applied, they stay applied for its lifetime.</summary>
    private sealed class MigrationState
    {
        private volatile bool _migrated;

        public bool Migrated
        {
            get => _migrated;
            set => _migrated = value;
        }
    }
}
