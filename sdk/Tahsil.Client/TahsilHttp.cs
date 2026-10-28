using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tahsil.Contracts;

namespace Tahsil.Client;

/// <summary>
/// Sends API calls: an idempotency key on every <c>POST</c>, kept across retries (SDK-5); retries of reads and keyed
/// writes after connection errors, <c>429</c> and <c>503</c> (SDK-6); problem details as <see cref="TahsilApiException"/> (SDK-7).
/// </summary>
internal sealed class TahsilHttp(HttpClient http, int maxRetries)
{
    private const string BasePath = "api/v1/";
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    public async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, RequestOptions? options, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(method, path, body, options, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(TahsilClientJson.Options, cancellationToken)
            ?? throw new TahsilApiException(response.StatusCode, code: null, "Tahsil returned an empty response.");
    }

    /// <summary>A call whose answer may have no body, such as a deletion: <see langword="default"/> then.</summary>
    public async Task<T?> SendOptionalAsync<T>(HttpMethod method, string path, object? body, RequestOptions? options, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(method, path, body, options, cancellationToken);
        return response.StatusCode == HttpStatusCode.NoContent ? default : await response.Content.ReadFromJsonAsync<T>(TahsilClientJson.Options, cancellationToken);
    }

    public async Task SendAsync(HttpMethod method, string path, object? body, RequestOptions? options, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(method, path, body, options, cancellationToken);
    }

    /// <summary>Every item of a list, page after page (SDK-8).</summary>
    public static async IAsyncEnumerable<T> AllAsync<T>(Func<string?, Task<Page<T>>> page, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await page(cursor);
            foreach (var item in current.Items)
            {
                yield return item;
            }

            cursor = current.NextCursor;
        }
        while (cursor is not null);
    }

    /// <summary>A path with query parameters; parameters without a value are left out.</summary>
    public static string WithQuery(string path, params (string Name, object? Value)[] parameters)
    {
        var query = new StringBuilder();
        foreach (var (name, value) in parameters)
        {
            var text = value switch
            {
                null => null,
                Enum enumValue => JsonNamingPolicy.SnakeCaseLower.ConvertName(enumValue.ToString()),
                bool flag => flag ? "true" : "false",
                DateTimeOffset time => time.ToString("O", CultureInfo.InvariantCulture),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString(),
            };

            if (!string.IsNullOrEmpty(text))
            {
                query.Append(query.Length == 0 ? '?' : '&').Append(name).Append('=').Append(Uri.EscapeDataString(text));
            }
        }

        return path + query;
    }

    public static string Segment(string value) => Uri.EscapeDataString(value);

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string path, object? body, RequestOptions? options, CancellationToken cancellationToken)
    {
        var idempotencyKey = method == HttpMethod.Post ? options?.IdempotencyKey ?? Guid.NewGuid().ToString() : null;
        var retryable = method == HttpMethod.Get || idempotencyKey is not null;
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, BasePath + path)
            {
                Content = body is null ? null : JsonContent.Create(body, body.GetType(), options: TahsilClientJson.Options),
            };
            if (idempotencyKey is not null)
            {
                request.Headers.Add("Idempotency-Key", idempotencyKey);
            }

            if (options?.IfMatch is { } version)
            {
                request.Headers.TryAddWithoutValidation("If-Match", version);
            }

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, cancellationToken);
            }
            catch (Exception exception) when (retryable && attempt < maxRetries && !cancellationToken.IsCancellationRequested && exception is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(Backoff(attempt), cancellationToken);
                continue;
            }

            if (response.IsSuccessStatusCode)
            {
                options?.ETag = response.Headers.ETag?.ToString();

                return response;
            }

            if (retryable && attempt < maxRetries && response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? Backoff(attempt);
                response.Dispose();
                await Task.Delay(wait < MaxDelay ? wait : MaxDelay, cancellationToken);
                continue;
            }

            using (response)
            {
                throw await ProblemAsync(response, cancellationToken);
            }
        }
    }

    /// <summary>Half a second, doubling, with up to a quarter more at random, so that clients that failed together do not retry together.</summary>
    private static TimeSpan Backoff(int attempt)
    {
        var delay = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt));
        return delay + (delay * (RandomNumberGenerator.GetInt32(0, 250) / 1000.0));
    }

    private static async Task<TahsilApiException> ProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        Problem? problem = null;
        if (response.Content.Headers.ContentType?.MediaType is "application/problem+json" or "application/json")
        {
            try
            {
                problem = await response.Content.ReadFromJsonAsync<Problem>(TahsilClientJson.Options, cancellationToken);
            }
            catch (JsonException)
            {
                // Not problem details after all: the status says enough.
            }
        }

        return new TahsilApiException(
            response.StatusCode,
            problem?.Code,
            problem?.Detail ?? problem?.Title ?? $"Tahsil answered {(int)response.StatusCode} {response.ReasonPhrase}.",
            problem?.Errors,
            problem?.TraceId);
    }

    private sealed record Problem(string? Title, string? Detail, string? Code, Dictionary<string, string[]>? Errors, string? TraceId);
}
