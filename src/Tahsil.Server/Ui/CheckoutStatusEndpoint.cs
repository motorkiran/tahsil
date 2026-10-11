using Tahsil.Application.Checkout;

namespace Tahsil.Server.Ui;

/// <summary>What the checkout's pending page polls (CHK-19): whether the session completed, and where to go.</summary>
internal static class CheckoutStatusEndpoint
{
    public static IEndpointRouteBuilder MapCheckoutStatus(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/checkout/{token}/status", async (string token, CheckoutPages pages, HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await pages.StatusAsync(token, cancellationToken) is { } status
                ? Results.Json(new
                {
                    state = status.State.ToString().ToLowerInvariant(),
                    redirectUrl = status.RedirectUrl,
                    retryUrl = status.State == CheckoutReturnState.Failed ? $"/checkout/{token}?failed={Uri.EscapeDataString(status.FailureCode ?? "provider_error")}" : null,
                })
                : Results.NotFound();
        }).ExcludeFromDescription();

        return endpoints;
    }
}
