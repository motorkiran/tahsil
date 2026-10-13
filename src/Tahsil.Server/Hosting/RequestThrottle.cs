using System.Collections.Frozen;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Tahsil.Server.Hosting;

/// <summary>Actions of the hosted pages with their own per-address limit. Features add theirs.</summary>
public enum ThrottledAction
{
    /// <summary>Any request to a hosted page.</summary>
    HostedPage,

    /// <summary>A provider notification (SEC-4).</summary>
    ProviderNotification,

    /// <summary>A webhook from Kimlik, limited like provider notifications.</summary>
    KimlikWebhook,
}

/// <summary>
/// Per-address limits for the hosted pages. Pages check them before doing any work, so a rejected attempt costs no
/// provider call, and the person sees the reason on the page itself.
/// </summary>
public sealed class RequestThrottle : IDisposable
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly FrozenDictionary<ThrottledAction, PartitionedRateLimiter<string>> _limiters;

    public RequestThrottle(IOptions<RateLimitOptions> options)
    {
        var limits = options.Value;
        _limiters = new Dictionary<ThrottledAction, PartitionedRateLimiter<string>>
        {
            [ThrottledAction.HostedPage] = CreateLimiter(limits.HostedPageRequestsPerMinute),
            [ThrottledAction.ProviderNotification] = CreateLimiter(limits.ProviderNotificationsPerMinute),
            [ThrottledAction.KimlikWebhook] = CreateLimiter(limits.ProviderNotificationsPerMinute),
        }.ToFrozenDictionary();
    }

    public bool TryAcquire(ThrottledAction action, HttpContext context) => TryAcquire(action, PartitionKey(context));

    /// <summary>The same limit for something else than an address, such as a portal session.</summary>
    public bool TryAcquire(ThrottledAction action, string key)
    {
        using var lease = _limiters[action].AttemptAcquire(key);
        return lease.IsAcquired;
    }

    /// <summary>The caller's address; IPv6 addresses are reduced to their /64 network.</summary>
    public static string PartitionKey(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    public void Dispose()
    {
        foreach (var limiter in _limiters.Values)
        {
            limiter.Dispose();
        }
    }

    private static PartitionedRateLimiter<string> CreateLimiter(int permitsPerMinute) =>
        PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = permitsPerMinute,
            Window = Window,
            SegmentsPerWindow = 6,
            QueueLimit = 0,
        }));
}
