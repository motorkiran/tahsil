using Tahsil.Contracts;

namespace Tahsil.Client.Resources;

/// <summary>Usage reporting for metered prices. Requires <c>tahsil.usage:write</c>.</summary>
public sealed class UsageEventsClient
{
    private readonly TahsilHttp _http;

    internal UsageEventsClient(TahsilHttp http) => _http = http;

    /// <summary>Report usage events. Stores a batch of usage events for metered prices. Each event is accepted, a duplicate or rejected by itself, and the answer says which: an event whose <c>id</c> is stored already is a duplicate whatever it says, so retry a batch with the same IDs. An event of a period that is already invoiced is rejected with <c>usage_event.period_closed</c>. This endpoint has a rate limit of its own.</summary>
    public Task<ReportUsageResponse> ReportAsync(ReportUsageRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _http.SendAsync<ReportUsageResponse>(HttpMethod.Post, "usage-events", request, options, cancellationToken);
}
