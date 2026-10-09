namespace Tahsil.Server.Hosting;

/// <summary>Rate limits from the <c>Tahsil:RateLimits</c> configuration section. Limits are per instance.</summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "Tahsil:RateLimits";

    /// <summary>Hosted page requests per minute from one network address (IPv6 by /64).</summary>
    public int HostedPageRequestsPerMinute { get; set; } = 120;

    internal bool IsValid() => HostedPageRequestsPerMinute > 0;
}
