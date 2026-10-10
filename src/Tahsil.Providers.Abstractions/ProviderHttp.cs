using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace Tahsil.Providers.Abstractions;

/// <summary>A provider's answer, read in full.</summary>
public sealed record ProviderResponse(int Status, string Body, HttpResponseHeaders Headers)
{
    public bool IsSuccess => Status is >= 200 and < 300;

    public bool IsServerError => Status >= 500;
}

/// <summary>Why a call has no answer; both mean the call may have changed state (ADP-4).</summary>
public enum ProviderCallFailure
{
    Timeout,
    ConnectionFailed,
}

public sealed record ProviderCallResult(ProviderResponse? Response, ProviderCallFailure? Failure)
{
    public bool Answered => Response is not null;
}

/// <summary>Sends adapter requests once, with the operation's timeout and redacted Debug logging (ADP-5, ADP-9, ADP-10).</summary>
public static partial class ProviderHttp
{
    public static TimeSpan ChangeTimeout { get; } = TimeSpan.FromSeconds(30);

    public static TimeSpan LookupTimeout { get; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Sends a request and reads the answer. A timeout or a failed connection is a result, not an exception; the
    /// caller's own cancellation still throws.
    /// </summary>
    public static async Task<ProviderCallResult> SendAsync(
        ProviderContext context,
        HttpRequestMessage request,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? redact = null)
    {
        using var deadline = new CancellationTokenSource(timeout, context.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var logger = context.Logger;
        if (logger.IsEnabled(LogLevel.Debug))
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            LogRequest(logger, request.Method, PathOf(request.RequestUri), Redaction.Body(body, request.Content?.Headers.ContentType?.MediaType, redact));
        }

        try
        {
            using var response = await context.Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token);
            var text = await response.Content.ReadAsStringAsync(linked.Token);
            if (logger.IsEnabled(LogLevel.Debug))
            {
                LogResponse(logger, (int)response.StatusCode, Redaction.Body(text, response.Content.Headers.ContentType?.MediaType ?? "application/json", redact));
            }

            return new(new ProviderResponse((int)response.StatusCode, text, response.Headers), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogNoAnswer(logger, request.Method, PathOf(request.RequestUri), ProviderCallFailure.Timeout);
            return new(null, ProviderCallFailure.Timeout);
        }
        catch (HttpRequestException)
        {
            LogNoAnswer(logger, request.Method, PathOf(request.RequestUri), ProviderCallFailure.ConnectionFailed);
            return new(null, ProviderCallFailure.ConnectionFailed);
        }
    }

    /// <summary>The path only: query strings may carry credentials.</summary>
    private static string PathOf(Uri? uri) => uri switch
    {
        null => string.Empty,
        { IsAbsoluteUri: true } => uri.AbsolutePath,
        _ => uri.OriginalString.Split('?', 2)[0],
    };

    [LoggerMessage(LogLevel.Debug, "Provider request {Method} {Path}: {Body}")]
    private static partial void LogRequest(ILogger logger, HttpMethod method, string? path, string body);

    [LoggerMessage(LogLevel.Debug, "Provider response {Status}: {Body}")]
    private static partial void LogResponse(ILogger logger, int status, string body);

    [LoggerMessage(LogLevel.Warning, "Provider request {Method} {Path} got no answer: {Failure}")]
    private static partial void LogNoAnswer(ILogger logger, HttpMethod method, string? path, ProviderCallFailure failure);
}
