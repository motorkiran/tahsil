namespace Tahsil.Server.Hosting;

/// <summary>Rate limits from the <c>Tahsil:RateLimits</c> configuration section. Limits are per instance.</summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "Tahsil:RateLimits";

    /// <summary>Hosted page requests per minute from one network address (IPv6 by /64).</summary>
    public int HostedPageRequestsPerMinute { get; set; } = 120;

    /// <summary>Provider notifications per minute from one network address (SEC-4).</summary>
    public int ProviderNotificationsPerMinute { get; set; } = 600;

    /// <summary>API requests per second of one API key, on average.</summary>
    public int ApiRequestsPerSecond { get; set; } = 25;

    /// <summary>API requests one API key may make at once after a quiet period.</summary>
    public int ApiBurst { get; set; } = 50;

    /// <summary>Batches of usage events per second of one API key, which is also what it may send at once; apart from its other requests.</summary>
    public int UsageBatchesPerSecond { get; set; } = 10;

    internal bool IsValid() =>
        HostedPageRequestsPerMinute > 0 && ProviderNotificationsPerMinute > 0 && ApiRequestsPerSecond > 0 && ApiBurst >= ApiRequestsPerSecond
        && UsageBatchesPerSecond > 0;
}
