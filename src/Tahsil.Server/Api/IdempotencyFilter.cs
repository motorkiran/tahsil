using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tahsil.Application.Common;
using Tahsil.Infrastructure.Idempotency;
using Tahsil.Server.Hosting;

namespace Tahsil.Server.Api;

/// <summary>
/// Reads the resource a crashed request created, to answer its retry with the resource as it is now (API-13). Create
/// endpoints register one per resource type they record.
/// </summary>
internal sealed record IdempotentResourceReader(string ResourceType, Func<HttpContext, Guid, Task<IResult>> ReadAsync);

/// <summary>
/// The <c>Idempotency-Key</c> protocol of every <c>POST</c>: the first request runs and its response is kept for
/// 24 hours; a repeat gets that response with <c>Idempotent-Replayed: true</c>; the same key with another request is
/// refused; a duplicate arriving while the first still runs is told to retry. Server errors are not kept, so they can
/// be retried.
/// </summary>
internal sealed partial class IdempotencyFilter(
    IdempotencyStore store,
    IdempotencyScope scope,
    IEnumerable<IdempotentResourceReader> readers,
    ILogger<IdempotencyFilter> logger) : IEndpointFilter
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!HttpMethods.IsPost(http.Request.Method) || !http.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return await next(context);
        }

        var key = values.ToString();
        if (!IsValidKey(key))
        {
            return ApiResults.Problem(IdempotencyErrors.InvalidKey);
        }

        // Keys are scoped to the API key that sends them.
        if (http.Features.Get<ICallerFeature>()?.Caller is not ApiKeyCaller caller)
        {
            return await next(context);
        }

        var cancellationToken = http.RequestAborted;
        var decision = await store.BeginAsync(
            caller.ApiKeyId, key, http.Request.Method, http.Request.Path.Value ?? "/", await FingerprintAsync(http.Request), cancellationToken);

        switch (decision)
        {
            case IdempotencyDecision.Refuse refuse:
                return ApiResults.Problem(refuse.Error);

            case IdempotencyDecision.Replay replay:
                return new ReplayResult(replay);

            case IdempotencyDecision.Recover recover:
                var reader = readers.SingleOrDefault(candidate => candidate.ResourceType == recover.ResourceType)
                    ?? throw new InvalidOperationException($"No reader is registered for the idempotent resource type '{recover.ResourceType}'.");
                return await KeepAsync(http, recover.RecordId, await reader.ReadAsync(http, recover.ResourceId), cancellationToken);

            case IdempotencyDecision.Proceed proceed:
                scope.RecordId = proceed.RecordId;
                try
                {
                    return await KeepAsync(http, proceed.RecordId, await next(context), cancellationToken);
                }
                catch
                {
                    await store.DeleteAsync(proceed.RecordId, CancellationToken.None);
                    throw;
                }

            default:
                throw new InvalidOperationException($"Unknown idempotency decision {decision}.");
        }
    }

    public static bool IsValidKey(string key) => key.Length is >= 1 and <= IdempotencyRecord.KeyMaxLength && key.All(character => character is >= ' ' and <= '~');

    /// <summary>
    /// Writes the result into a buffer to keep it, then sends it. If keeping it fails, the client still gets its
    /// response; a retry then finds the record in progress and, once its lease ends, the resource it created.
    /// </summary>
    private async Task<object?> KeepAsync(HttpContext http, Guid recordId, object? result, CancellationToken cancellationToken)
    {
        if (result is not IResult executable)
        {
            await store.DeleteAsync(recordId, cancellationToken);
            return result;
        }

        var original = http.Response.Body;
        using var buffer = new MemoryStream();
        http.Response.Body = buffer;
        try
        {
            await executable.ExecuteAsync(http);
        }
        finally
        {
            http.Response.Body = original;
        }

        var body = buffer.ToArray();
        try
        {
            if (http.Response.StatusCode >= StatusCodes.Status500InternalServerError)
            {
                await store.DeleteAsync(recordId, cancellationToken);
            }
            else
            {
                await store.CompleteAsync(recordId, http.Response.StatusCode, StoredHeaders(http.Response), body, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogKeepFailed(logger, exception, recordId);
        }

        await original.WriteAsync(body, cancellationToken);
        return Results.Empty;
    }

    private static async Task<byte[]> FingerprintAsync(HttpRequest request)
    {
        // The body was buffered when the request arrived (UseRequestBuffering), so binding left it readable.
        request.Body.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{request.Method}\n{request.Path}\n"));
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, request.HttpContext.RequestAborted)) > 0)
        {
            hash.AppendData(chunk, 0, read);
        }

        request.Body.Position = 0;
        return hash.GetHashAndReset();
    }

    private static string StoredHeaders(HttpResponse response) => JsonSerializer.Serialize(new Dictionary<string, string?>
    {
        ["Content-Type"] = response.ContentType,
        ["Location"] = response.Headers.Location.ToString() is { Length: > 0 } location ? location : null,
    });

    [LoggerMessage(LogLevel.Error, "Keeping the response of idempotency record {RecordId} failed; a retry will find it in progress")]
    private static partial void LogKeepFailed(ILogger logger, Exception exception, Guid recordId);

    /// <summary>The kept response of an earlier request with the same key.</summary>
    private sealed class ReplayResult(IdempotencyDecision.Replay replay) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.StatusCode = replay.Status;
            if (replay.Headers is { } headers && JsonSerializer.Deserialize<Dictionary<string, string?>>(headers) is { } stored)
            {
                foreach (var (name, value) in stored.Where(header => header.Value is not null))
                {
                    response.Headers[name] = value;
                }
            }

            response.Headers[ReplayedHeader] = "true";
            await response.Body.WriteAsync(replay.Body, httpContext.RequestAborted);
        }
    }
}
