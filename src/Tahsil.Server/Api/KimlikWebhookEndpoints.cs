using Tahsil.Application.Kimlik;
using Tahsil.Server.Hosting;

namespace Tahsil.Server.Api;

/// <summary>Where Kimlik sends its webhooks when the connector is on (KML-22): public, limited per address and to 64 KB bodies.</summary>
internal static class KimlikWebhookEndpoints
{
    public static IEndpointRouteBuilder MapKimlikWebhooks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/webhooks/kimlik", ReceiveAsync).DisableAntiforgery().ExcludeFromDescription();
        return endpoints;
    }

    private static async Task<IResult> ReceiveAsync(HttpContext http, RequestThrottle throttle, KimlikWebhooks webhooks)
    {
        if (!webhooks.IsEnabled)
        {
            return Results.NotFound();
        }

        if (!throttle.TryAcquire(ThrottledAction.KimlikWebhook, http))
        {
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        if (http.Request.ContentLength > ProviderNotificationEndpoints.MaxBodyBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        using var reader = new StreamReader(http.Request.Body);
        var body = await reader.ReadToEndAsync(http.RequestAborted);
        if (body.Length > ProviderNotificationEndpoints.MaxBodyBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var headers = http.Request.Headers;
        return await webhooks.ReceiveAsync(headers["webhook-id"], headers["webhook-timestamp"], headers["webhook-signature"], body, http.RequestAborted)
            ? Results.NoContent()
            : Results.Unauthorized();
    }
}
