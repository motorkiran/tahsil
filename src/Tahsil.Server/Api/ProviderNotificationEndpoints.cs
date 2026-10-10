using Tahsil.Application.Payments;
using Tahsil.Providers.Abstractions;
using Tahsil.Server.Hosting;

namespace Tahsil.Server.Api;

/// <summary>
/// Where providers send notifications (PAY-23): public, limited per network address and to 64 KB bodies (SEC-4),
/// answered as the connection's adapter says, with all work beyond verification and storage left to a worker.
/// </summary>
internal static class ProviderNotificationEndpoints
{
    public const int MaxBodyBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapProviderNotifications(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/provider-notifications/{connectionId:guid}", [HttpMethods.Post, HttpMethods.Get], ReceiveAsync)
            .DisableAntiforgery()
            .ExcludeFromDescription();
        return endpoints;
    }

    private static async Task<IResult> ReceiveAsync(Guid connectionId, HttpContext http, RequestThrottle throttle, ProviderNotifications notifications)
    {
        if (!throttle.TryAcquire(ThrottledAction.ProviderNotification, http))
        {
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        if (http.Request.ContentLength > MaxBodyBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var body = await ReadBodyAsync(http.Request, http.RequestAborted);
        if (body is null)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var answer = await notifications.ReceiveAsync(connectionId, new IncomingNotification
        {
            Method = http.Request.Method,
            Headers = http.Request.Headers.ToDictionary(header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase),
            Query = http.Request.Query.ToDictionary(value => value.Key, value => value.Value.ToString(), StringComparer.Ordinal),
            Body = body,
            RemoteAddress = http.Connection.RemoteIpAddress,
        }, http.RequestAborted);
        return Results.Text(answer.Body, answer.ContentType, statusCode: answer.Status);
    }

    /// <summary>Reads the body, or returns <see langword="null"/> when it is larger than allowed, whatever the request claimed.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
