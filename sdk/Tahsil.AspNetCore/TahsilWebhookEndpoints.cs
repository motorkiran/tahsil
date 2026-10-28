using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Tahsil.AspNetCore;

public static class TahsilWebhookEndpoints
{
    /// <summary>Registers the in-memory <see cref="ITahsilWebhookDeduplicator"/>, unless another one is registered.</summary>
    public static IServiceCollection AddTahsilWebhooks(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ITahsilWebhookDeduplicator, InMemoryTahsilWebhookDeduplicator>();
        return services;
    }

    /// <summary>
    /// Maps a <c>POST</c> endpoint for the webhooks of one Tahsil endpoint (SDK-11): it reads the raw body once,
    /// verifies it with <paramref name="secret"/>, answers <c>401</c> to anything else, skips a webhook it handled
    /// already, and calls <paramref name="handler"/>. A handler that throws makes Tahsil deliver the webhook again.
    /// </summary>
    /// <param name="endpoints">The application's routes.</param>
    /// <param name="pattern">The path Tahsil posts to, such as <c>/webhooks/tahsil</c>.</param>
    /// <param name="secret">The endpoint's <c>whsec_…</c> secret; during a rotation, give both with the other overload.</param>
    /// <param name="handler">What the application does with an event.</param>
    public static RouteHandlerBuilder MapTahsilWebhooks(
        this IEndpointRouteBuilder endpoints, string pattern, string secret, Func<TahsilEvent, CancellationToken, Task> handler) =>
        endpoints.MapTahsilWebhooks(pattern, [secret], handler);

    /// <inheritdoc cref="MapTahsilWebhooks(IEndpointRouteBuilder, string, string, Func{TahsilEvent, CancellationToken, Task})"/>
    /// <param name="endpoints">The application's routes.</param>
    /// <param name="pattern">The path Tahsil posts to.</param>
    /// <param name="secrets">The endpoint's secrets: the current one, and the previous one during a rotation.</param>
    /// <param name="handler">What the application does with an event.</param>
    public static RouteHandlerBuilder MapTahsilWebhooks(
        this IEndpointRouteBuilder endpoints, string pattern, IReadOnlyList<string> secrets, Func<TahsilEvent, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(handler);
        var verifiedSecrets = secrets?.ToArray() ?? throw new ArgumentNullException(nameof(secrets));
        return endpoints.MapPost(pattern, async (HttpContext context) =>
        {
            var cancellationToken = context.RequestAborted;
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(cancellationToken);
            var time = context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
            if (!TahsilWebhook.TryVerify(context.Request.Headers, body, verifiedSecrets, time, out var webhookEvent))
            {
                return Results.Unauthorized();
            }

            var deduplicator = context.RequestServices.GetService<ITahsilWebhookDeduplicator>() ?? Fallback;
            if (!await deduplicator.TryBeginAsync(webhookEvent.Id, cancellationToken))
            {
                return Results.Ok();
            }

            try
            {
                await handler(webhookEvent, cancellationToken);
            }
            catch
            {
                await deduplicator.ForgetAsync(webhookEvent.Id, CancellationToken.None);
                throw;
            }

            return Results.Ok();
        }).ExcludeFromDescription();
    }

    /// <summary>For applications that map webhooks without <see cref="AddTahsilWebhooks"/>.</summary>
    private static readonly InMemoryTahsilWebhookDeduplicator Fallback = new();
}
