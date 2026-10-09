using System.Diagnostics;
using System.Net;
using Tahsil.Application.Common;

namespace Tahsil.Server.Hosting;

/// <summary>
/// Describes the caller from the current HTTP request; outside of a request Tahsil itself is the caller. A request
/// that reaches a use case without an authenticated caller is a bug in its endpoint, so it fails instead of being
/// treated as anyone.
/// </summary>
internal sealed class HttpRequestContext(IHttpContextAccessor accessor) : IRequestContext
{
    public Caller Caller => accessor.HttpContext switch
    {
        null => SystemCaller.Instance,
        var context => context.Features.Get<ICallerFeature>()?.Caller
            ?? throw new InvalidOperationException($"The request to {context.Request.Path} has no authenticated caller."),
    };

    public IPAddress? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress;

    public string? UserAgent => accessor.HttpContext?.Request.Headers.UserAgent.ToString() is { Length: > 0 } userAgent ? userAgent : null;

    public string? CorrelationId => Activity.Current?.TraceId.ToString() ?? accessor.HttpContext?.TraceIdentifier;
}

/// <summary>The caller that authentication established for a request (API key, admin session or hosted page session).</summary>
internal interface ICallerFeature
{
    Caller Caller { get; }
}

internal sealed record CallerFeature(Caller Caller) : ICallerFeature;
