using System.Net;

namespace Tahsil.Application.Common;

/// <summary>Information about the caller of the current operation; outside of a request Tahsil itself is the caller.</summary>
public interface IRequestContext
{
    Caller Caller { get; }

    IPAddress? IpAddress { get; }

    string? UserAgent { get; }

    string? CorrelationId { get; }
}
