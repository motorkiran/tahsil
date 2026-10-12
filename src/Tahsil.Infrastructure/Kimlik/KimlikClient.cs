using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Tahsil.Application.Kimlik;
using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;

namespace Tahsil.Infrastructure.Kimlik;

/// <summary>
/// Kimlik's Management API over HTTP (KML-11), with Tahsil's own small contracts rather than Kimlik's packages, so the
/// two products release independently. A request Kimlik refuses with 401 is sent once more with a new token.
/// </summary>
internal sealed class KimlikClient(IHttpClientFactory clients, KimlikTokens tokens) : IKimlikClient
{
    public const string HttpClientName = "kimlik";

    private const string Api = "api/v1/";

    /// <summary>Kimlik's JSON: camelCase properties, and enums as their camelCase names.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public Task<Result<KimlikSubscription>> GetSubscriptionAsync(Guid id, CancellationToken cancellationToken) =>
        SendAsync<KimlikSubscription>(HttpMethod.Get, $"subscriptions/{id}", null, cancellationToken);

    public async Task<Result<IReadOnlyList<KimlikSubscription>>> ListSubscriptionsAsync(KimlikSubscriber subscriber, CancellationToken cancellationToken)
    {
        // Newest first: a subscriber's current subscription is among the first page.
        var page = await SendAsync<Page<KimlikSubscription>>(
            HttpMethod.Get, $"subscriptions?subscriberType={Wire(subscriber.Type)}&subscriberId={subscriber.Id}&limit=50", null, cancellationToken);
        return page.IsFailure ? page.Error : Result.Success(page.Value.Items);
    }

    public Task<Result<KimlikSubscription>> CreateSubscriptionAsync(KimlikSubscriptionDraft draft, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["subscriberType"] = Wire(draft.Subscriber.Type),
            ["subscriberId"] = draft.Subscriber.Id,
            ["plan"] = draft.Plan,
            ["externalReference"] = draft.ExternalReference,
        };
        SetTime(body, "trialEndsAt", draft.TrialEndsAt);
        SetTime(body, "currentPeriodEnd", draft.CurrentPeriodEnd);
        return SendAsync<KimlikSubscription>(HttpMethod.Post, "subscriptions", body, cancellationToken);
    }

    public Task<Result<KimlikSubscription>> UpdateSubscriptionAsync(Guid id, KimlikSubscriptionChange change, CancellationToken cancellationToken)
    {
        // Only the fields that change: Kimlik reads an explicit null as "clear".
        var body = new JsonObject();
        if (change.Plan is { } plan)
        {
            body["plan"] = plan;
        }

        if (change.Status is { } status)
        {
            body["status"] = JsonNamingPolicy.CamelCase.ConvertName(status.ToString());
        }

        SetTime(body, "trialEndsAt", change.TrialEndsAt);
        SetTime(body, "currentPeriodEnd", change.CurrentPeriodEnd);
        if (change.ExternalReference is { } reference)
        {
            body["externalReference"] = reference;
        }

        return SendAsync<KimlikSubscription>(HttpMethod.Patch, $"subscriptions/{id}", body, cancellationToken);
    }

    public Task<Result<KimlikSubscription>> CancelSubscriptionAsync(Guid id, CancellationToken cancellationToken) =>
        SendAsync<KimlikSubscription>(HttpMethod.Post, $"subscriptions/{id}/cancel", null, cancellationToken);

    public async Task<Result<IReadOnlyList<KimlikPlan>>> ListPlansAsync(CancellationToken cancellationToken)
    {
        var plans = new List<KimlikPlan>();
        string? cursor = null;
        do
        {
            var page = await SendAsync<Page<KimlikPlan>>(
                HttpMethod.Get, cursor is null ? "plans?limit=200" : $"plans?limit=200&cursor={Uri.EscapeDataString(cursor)}", null, cancellationToken);
            if (page.IsFailure)
            {
                return page.Error;
            }

            plans.AddRange(page.Value.Items);
            cursor = page.Value.NextCursor;
        }
        while (cursor is not null);

        return Result.Success<IReadOnlyList<KimlikPlan>>(plans);
    }

    public Task<Result<KimlikUser>> GetUserAsync(Guid id, CancellationToken cancellationToken) =>
        SendAsync<KimlikUser>(HttpMethod.Get, $"users/{id}", null, cancellationToken);

    public Task<Result<KimlikOrganization>> GetOrganizationAsync(Guid id, CancellationToken cancellationToken) =>
        SendAsync<KimlikOrganization>(HttpMethod.Get, $"organizations/{id}", null, cancellationToken);

    /// <summary>Kimlik cannot be reached, timed out or answered something unreadable; the caller's own cancellation is not.</summary>
    internal static bool IsUnreachable(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or JsonException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private async Task<Result<T>> SendAsync<T>(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        var token = await tokens.GetAsync(null, cancellationToken);
        if (token.IsFailure)
        {
            return token.Error;
        }

        try
        {
            var client = clients.CreateClient(HttpClientName);
            using (var request = Request(method, path, body, token.Value))
            using (var response = await client.SendAsync(request, cancellationToken))
            {
                if (response.StatusCode != HttpStatusCode.Unauthorized)
                {
                    return await ReadAsync<T>(response, cancellationToken);
                }
            }

            // The token was revoked or the signing key rotated: once more with a new one.
            var renewed = await tokens.GetAsync(token.Value, cancellationToken);
            if (renewed.IsFailure)
            {
                return renewed.Error;
            }

            using var retry = Request(method, path, body, renewed.Value);
            using var retried = await client.SendAsync(retry, cancellationToken);
            return await ReadAsync<T>(retried, cancellationToken);
        }
        catch (Exception exception) when (IsUnreachable(exception, cancellationToken))
        {
            return KimlikErrors.Unavailable;
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, JsonObject? body, string token)
    {
        var request = new HttpRequestMessage(method, new Uri(Api + path, UriKind.Relative))
        {
            Content = body is null ? null : JsonContent.Create(body, options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<Result<T>> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken) is { } value ? value : throw new JsonException("Kimlik answered without a body.");
        }

        var status = (int)response.StatusCode;
        if (status >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return KimlikErrors.Unavailable;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return KimlikErrors.Unauthorized;
        }

        var problem = response.Content.Headers.ContentType?.MediaType is "application/problem+json" or "application/json"
            ? await response.Content.ReadFromJsonAsync<Problem>(Json, cancellationToken)
            : null;
        var code = problem?.Code ?? "kimlik.error";
        var message = problem?.Detail ?? problem?.Title ?? $"Kimlik answered {status.ToString(CultureInfo.InvariantCulture)}.";
        return response.StatusCode switch
        {
            HttpStatusCode.NotFound => Error.NotFound(code, message),
            HttpStatusCode.Conflict => Error.Conflict(code, message),
            HttpStatusCode.Forbidden => Error.Forbidden(code, message),
            _ => Error.Validation(code, message),
        };
    }

    /// <summary>Kimlik fails on offsets other than UTC, so instants go as UTC with <c>Z</c> (KML-11).</summary>
    private static void SetTime(JsonObject body, string name, DateTimeOffset? value)
    {
        if (value is { } instant)
        {
            body[name] = instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);
        }
    }

    private static string Wire(KimlikSubscriberType type) => JsonNamingPolicy.CamelCase.ConvertName(type.ToString());

    private sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

    private sealed record Problem(string? Title, string? Detail, string? Code);
}
