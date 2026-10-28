using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Tahsil.Contracts;
using Tahsil.Contracts.Serialization;

namespace Tahsil.AspNetCore;

/// <summary>
/// Checks that a webhook comes from Tahsil, as the Standard Webhooks specification describes (SDK-10): one of its
/// signatures must match one of the endpoint's secrets (two during a rotation, EVT-8), and its timestamp must be within
/// five minutes, which turns replayed requests away. Delivery is at least once: handle each <see cref="TahsilEvent.Id"/> once.
/// </summary>
public static class TahsilWebhook
{
    /// <summary>How far the timestamp may be from the current time.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private const string SecretPrefix = "whsec_";
    private const string SignatureVersion = "v1,";
    private const int MinimumKeyLength = 16;

    private static readonly JsonSerializerOptions SerializerOptions = TahsilJson.Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>
    /// Reads the webhook in the request, or returns <see langword="null"/> if it is not one from Tahsil signed with one
    /// of <paramref name="secrets"/>, the endpoint's <c>whsec_…</c> secrets. The body is read raw, before anything binds it.
    /// </summary>
    public static async Task<TahsilEvent?> ReadAsync(HttpRequest request, params string[] secrets)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
        return TryVerify(request.Headers, body, secrets, out var webhookEvent) ? webhookEvent : null;
    }

    /// <summary>Whether the headers and body make a webhook from Tahsil signed with one of <paramref name="secrets"/>.</summary>
    public static bool TryVerify(IHeaderDictionary headers, string body, IReadOnlyList<string> secrets, [NotNullWhen(true)] out TahsilEvent? webhookEvent) =>
        TryVerify(headers, body, secrets, TimeProvider.System, out webhookEvent);

    /// <inheritdoc cref="TryVerify(IHeaderDictionary, string, IReadOnlyList{string}, out TahsilEvent?)"/>
    public static bool TryVerify(
        IHeaderDictionary headers, string body, IReadOnlyList<string> secrets, TimeProvider timeProvider, [NotNullWhen(true)] out TahsilEvent? webhookEvent)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(timeProvider);
        webhookEvent = null;
        var keys = secrets.Count > 0 ? secrets.Select(KeyOf).ToList() : throw new ArgumentException("Give the endpoint's secret.", nameof(secrets));
        string? id = headers["webhook-id"];
        string? timestamp = headers["webhook-timestamp"];
        string? signatures = headers["webhook-signature"];

        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(signatures)
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || (timeProvider.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > Tolerance)
        {
            return false;
        }

        var signed = Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}");
        var expected = keys.Select(key => HMACSHA256.HashData(key, signed)).ToList();

        // The header carries one signature per secret Tahsil signs with, two while a secret is being replaced.
        var valid = signatures.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(signature =>
            signature.StartsWith(SignatureVersion, StringComparison.Ordinal)
            && TryDecode(signature[SignatureVersion.Length..], out var candidate)
            && expected.Any(hash => CryptographicOperations.FixedTimeEquals(candidate, hash)));
        if (!valid)
        {
            return false;
        }

        var parsed = JsonSerializer.Deserialize<WebhookEvent>(body, SerializerOptions);
        webhookEvent = parsed is null ? null : new TahsilEvent(id, parsed.Type, parsed.Timestamp, parsed.Mode, parsed.Data);
        return webhookEvent is not null;
    }

    /// <summary>
    /// The key of a secret, which must be one: a key shorter than 16 bytes, such as an empty one from a missing setting,
    /// would let anyone sign.
    /// </summary>
    private static byte[] KeyOf(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var encoded = secret.StartsWith(SecretPrefix, StringComparison.Ordinal) ? secret[SecretPrefix.Length..] : secret;
        var key = new byte[encoded.Length];
        return Convert.TryFromBase64String(encoded, key, out var length) && length >= MinimumKeyLength
            ? key[..length]
            : throw new ArgumentException("A webhook secret is the endpoint's whsec_… secret: base64 of at least 16 bytes.", nameof(secret));
    }

    private static bool TryDecode(string value, out byte[] bytes)
    {
        bytes = new byte[value.Length];
        if (Convert.TryFromBase64String(value, bytes, out var written))
        {
            bytes = bytes[..written];
            return true;
        }

        return false;
    }
}
