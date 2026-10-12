using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Tahsil.Application.Kimlik;
using Tahsil.Domain.Common;

namespace Tahsil.Infrastructure.Kimlik;

/// <summary>
/// Tahsil's access token for Kimlik's API (KML-11): client credentials with the <c>kimlik</c> scope, cached until a
/// minute before it expires (Kimlik issues 10-minute tokens without refresh tokens) and renewed by one caller at a time.
/// </summary>
internal sealed class KimlikTokens(IHttpClientFactory clients, IOptions<KimlikOptions> options, TimeProvider time) : IDisposable
{
    private static readonly TimeSpan RenewBefore = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _renewal = new(1, 1);
    private volatile CachedToken? _current;

    /// <summary>
    /// A token that is not <paramref name="rejected"/>: the cached one while it is fresh, else a new one. A caller whose
    /// token Kimlik refused passes it, so that concurrent callers renew it once.
    /// </summary>
    public async Task<Result<string>> GetAsync(string? rejected, CancellationToken cancellationToken)
    {
        if (Fresh(rejected) is { } cached)
        {
            return cached;
        }

        await _renewal.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have renewed it while this one waited.
            if (Fresh(rejected) is { } renewed)
            {
                return renewed;
            }

            var issued = await RequestAsync(cancellationToken);
            if (issued.IsFailure)
            {
                return issued.Error;
            }

            _current = issued.Value;
            return issued.Value.Value;
        }
        finally
        {
            _renewal.Release();
        }
    }

    public void Dispose() => _renewal.Dispose();

    private string? Fresh(string? rejected) =>
        _current is { } current && current.Value != rejected && time.GetUtcNow() < current.RenewAt ? current.Value : null;

    private async Task<Result<CachedToken>> RequestAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.Authority!, "connect/token"))
        {
            Content = new FormUrlEncodedContent([new("grant_type", "client_credentials"), new("scope", "kimlik")]),
        };

        // client_secret_basic: both parts form-encoded before Base64 (RFC 6749 §2.3.1).
        var credentials = $"{Uri.EscapeDataString(settings.ClientId!)}:{Uri.EscapeDataString(settings.ClientSecret!)}";
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        try
        {
            var client = clients.CreateClient(KimlikClient.HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                return KimlikErrors.Unauthorized;
            }

            if (!response.IsSuccessStatusCode
                || await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken) is not { AccessToken: { Length: > 0 } token, ExpiresIn: > 0 } issued)
            {
                return KimlikErrors.Unavailable;
            }

            return new CachedToken(token, time.GetUtcNow() + TimeSpan.FromSeconds(issued.ExpiresIn) - RenewBefore);
        }
        catch (Exception exception) when (KimlikClient.IsUnreachable(exception, cancellationToken))
        {
            return KimlikErrors.Unavailable;
        }
    }

    private sealed record CachedToken(string Value, DateTimeOffset RenewAt);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
