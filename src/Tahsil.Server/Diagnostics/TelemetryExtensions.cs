using System.Reflection;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Tahsil.Application.Common;

namespace Tahsil.Server.Diagnostics;

/// <summary>
/// OpenTelemetry traces, metrics and logs. Export is switched on by the standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>
/// setting; without it nothing is exported.
/// </summary>
internal static class TelemetryExtensions
{
    private const string ServiceName = "tahsil";

    public static WebApplicationBuilder AddTelemetry(this WebApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        var openTelemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName, serviceVersion: ServiceVersion()))
            .WithMetrics(metrics => metrics
                .AddMeter(TahsilDiagnostics.Name)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter("System.Runtime")
                .AddNpgsqlInstrumentation())
            .WithTracing(tracing => tracing
                .AddSource(TahsilDiagnostics.Name)
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddNpgsql());

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            openTelemetry.UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>The release version (<c>0.1.0</c>), without the commit suffix the SDK appends.</summary>
    public static string ServiceVersion()
    {
        var version = typeof(TelemetryExtensions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        return version.Split('+')[0];
    }
}
