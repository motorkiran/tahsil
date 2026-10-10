using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;

namespace Tahsil.Server.Tests.Webhooks;

/// <summary>A request Tahsil sent to a webhook endpoint, and the status the endpoint answered with.</summary>
internal sealed record ReceivedWebhook(HttpStatusCode Answer, IHeaderDictionary Headers, string Body)
{
    public string Id => Headers["webhook-id"].ToString();
}

/// <summary>
/// Stands in for the applications' webhook endpoints, in front of the host's outgoing webhook requests (copied from
/// Kimlik, with a delay to observe concurrency). Each test gets URLs of its own, which answer with the statuses it
/// scripts, then with 200.
/// </summary>
internal sealed class TestWebhookReceiver
{
    private readonly ConcurrentDictionary<string, Endpoint> _endpoints = new(StringComparer.Ordinal);
    private int _inFlight;

    /// <summary>The most requests that were in flight at once.</summary>
    public int MaxInFlight { get; private set; }

    /// <summary>How long each answer takes.</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>A new endpoint URL that answers with <paramref name="answers"/> first, then with 200.</summary>
    public string NewEndpoint(params HttpStatusCode[] answers)
    {
        var url = $"https://hooks.test/{Guid.NewGuid():N}";
        _endpoints[url] = new Endpoint(new ConcurrentQueue<HttpStatusCode>(answers));
        return url;
    }

    /// <summary>From now on, the endpoint answers with <paramref name="answer"/>.</summary>
    public void AnswerWith(string url, HttpStatusCode answer) => _endpoints[url].Default = answer;

    public IReadOnlyList<ReceivedWebhook> ReceivedBy(string url) => [.. _endpoints[url].Received];

    public HttpMessageHandler CreateHandler() => new Handler(this);

    private sealed class Endpoint(ConcurrentQueue<HttpStatusCode> answers)
    {
        public HttpStatusCode Default { get; set; } = HttpStatusCode.OK;

        public ConcurrentQueue<ReceivedWebhook> Received { get; } = new();

        public HttpStatusCode NextAnswer() => answers.TryDequeue(out var answer) ? answer : Default;
    }

    private sealed class Handler(TestWebhookReceiver receiver) : HttpMessageHandler
    {
        private readonly Lock _gate = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!receiver._endpoints.TryGetValue(request.RequestUri!.ToString(), out var endpoint))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            lock (_gate)
            {
                receiver._inFlight++;
                receiver.MaxInFlight = Math.Max(receiver.MaxInFlight, receiver._inFlight);
            }

            try
            {
                if (receiver.Delay > TimeSpan.Zero)
                {
                    await Task.Delay(receiver.Delay, cancellationToken);
                }

                var headers = new HeaderDictionary();
                foreach (var (name, values) in request.Headers)
                {
                    headers[name] = values.ToArray();
                }

                var answer = endpoint.NextAnswer();
                endpoint.Received.Enqueue(new ReceivedWebhook(answer, headers, await request.Content!.ReadAsStringAsync(cancellationToken)));
                return new HttpResponseMessage(answer) { Content = new StringContent(answer == HttpStatusCode.OK ? "thanks" : "not now") };
            }
            finally
            {
                lock (_gate)
                {
                    receiver._inFlight--;
                }
            }
        }
    }
}
