namespace Tahsil.Client;

/// <summary>Where the Tahsil API is and how the client calls it.</summary>
public sealed class TahsilClientOptions
{
    /// <summary>The installation's public URL, such as <c>https://billing.example.com/</c>.</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>An API key (<c>ths_live_…</c> or <c>ths_test_…</c>) with the permissions the application needs.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// How many times a call is retried after a connection error, <c>429</c> or <c>503</c> (SDK-6): reads and
    /// <c>POST</c>s, which always carry an idempotency key. 2 by default; 0 turns retries off.
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    internal bool IsValid() =>
        BaseUrl is { IsAbsoluteUri: true } && !string.IsNullOrWhiteSpace(ApiKey) && MaxRetries is >= 0 and <= 10;
}

/// <summary>
/// What a single call needs beyond its arguments, and what it tells back: the idempotency key of a <c>POST</c>, the
/// version a change expects, and the version of the resource the response holds.
/// </summary>
public sealed class RequestOptions
{
    /// <summary>The <c>Idempotency-Key</c> of a <c>POST</c>; a new one per call when not given, kept for its retries (SDK-5).</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>The <c>If-Match</c> of a change: the change fails with <c>412</c> if the resource changed since (SDK-9).</summary>
    public string? IfMatch { get; init; }

    /// <summary>The <c>ETag</c> of the response, once the call returned; pass it as <see cref="IfMatch"/> to a later change.</summary>
    public string? ETag { get; internal set; }
}
