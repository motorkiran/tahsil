using System.Reflection;
using System.Text.RegularExpressions;
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
internal static partial class TelemetryExtensions
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
                {
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health");

                    // Hosted pages carry their access token in the path; traces keep the path without it (SEC-15).
                    options.EnrichWithHttpRequest = (activity, request) => activity.SetTag("url.path", WithoutTokens(request.Path.Value));
                })
                .AddHttpClientInstrumentation()
                .AddNpgsql());

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            openTelemetry.UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>
    /// The path with every segment that looks like an access token replaced by <c>{token}</c>: the checkout's
    /// <c>cs_…</c>, the portal's <c>ps_…</c>, and the long random segments of hosted invoice and simulator pages.
    /// IDs, which grant nothing by themselves, stay.
    /// </summary>
    internal static string WithoutTokens(string? path) =>
        string.IsNullOrEmpty(path) ? "/" : string.Join('/', path.Split('/').Select(segment => TokenSegment().IsMatch(segment) ? "{token}" : segment));

    [GeneratedRegex("^(?:(?:cs|ps)_[A-Za-z0-9_-]{16,}|[A-Za-z0-9_-]{40,})$")]
    private static partial Regex TokenSegment();

    /// <summary>The release version (<c>0.1.0</c>), without the commit suffix the SDK appends.</summary>
    public static string ServiceVersion()
    {
        var version = typeof(TelemetryExtensions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        return version.Split('+')[0];
    }
}
