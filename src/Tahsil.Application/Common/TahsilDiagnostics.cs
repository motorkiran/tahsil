using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Tahsil.Application.Common;

/// <summary>Tahsil's own traces and metrics, exported with the platform's through OpenTelemetry.</summary>
public static class TahsilDiagnostics
{
    public const string Name = "Tahsil";

    public static readonly ActivitySource ActivitySource = new(Name);

    public static readonly Meter Meter = new(Name);

    /// <summary>Worker items that failed ten times in a row and need an operator (ENG-6), by worker.</summary>
    public static readonly Counter<long> AttentionRaised = Meter.CreateCounter<long>(
        "tahsil.worker.attention_raised",
        unit: "{item}",
        description: "Worker items that failed ten times in a row and need an operator.");

    /// <summary>Worker items that failed and were postponed, by worker.</summary>
    public static readonly Counter<long> ItemFailures = Meter.CreateCounter<long>(
        "tahsil.worker.item_failures",
        unit: "{failure}",
        description: "Worker items that failed and were postponed.");
}
