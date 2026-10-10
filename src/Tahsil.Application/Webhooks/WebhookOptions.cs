using System.Security.Cryptography;

namespace Tahsil.Application.Webhooks;

/// <summary>Delivery settings from the <c>Tahsil:Webhooks</c> configuration section (EVT §3).</summary>
public sealed class WebhookOptions
{
    public const string SectionName = "Tahsil:Webhooks";

    private static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(12),
        TimeSpan.FromHours(24),
    ];

    /// <summary>How often due deliveries are looked for; new events on this instance are sent at once.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Lets endpoints on private networks receive webhooks, as when Tahsil and the application share an internal
    /// network. Off, webhooks only go to the public internet (EVT-12).
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }

    /// <summary>How long an endpoint has to answer an attempt (EVT-11).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The waits before each retry of a failed delivery, after which it fails for good: by default 10 seconds, 1, 5
    /// and 30 minutes, 2, 6, 12 and 24 hours, nine attempts over about 45 hours (EVT-11).
    /// </summary>
    public TimeSpan[]? RetryDelays { get; set; }

    public int BatchSize { get; set; } = 20;

    /// <summary>Deliveries to one endpoint at a time, across a batch (EVT-10).</summary>
    public int EndpointConcurrency { get; set; } = 4;

    /// <summary>How long a rotated secret keeps signing beside the new one (EVT-8).</summary>
    public TimeSpan SecretRotationOverlap { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How long the delivery log is kept (EVT-14).</summary>
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(30);

    /// <summary>The configured delays, or the defaults; configuration would add to a default list rather than replace it.</summary>
    public IReadOnlyList<TimeSpan> Delays => RetryDelays is { Length: > 0 } ? RetryDelays : DefaultRetryDelays;

    internal bool IsValid() =>
        PollingInterval > TimeSpan.Zero
        && Timeout > TimeSpan.Zero
        && Delays.All(delay => delay > TimeSpan.Zero)
        && BatchSize is > 0 and <= 100
        && EndpointConcurrency is > 0 and <= 64
        && SecretRotationOverlap >= TimeSpan.Zero
        && RetentionPeriod > TimeSpan.Zero;
}

/// <summary>
/// Signing secrets in the Standard Webhooks format: <c>whsec_</c> and 256 random bits in base64, which are the HMAC
/// key. They are stored encrypted, because Tahsil needs them to sign.
/// </summary>
public static class WebhookSecrets
{
    public const string Prefix = "whsec_";

    public static string Generate() => Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>The HMAC key in a secret.</summary>
    public static byte[] KeyOf(string secret) => Convert.FromBase64String(secret.StartsWith(Prefix, StringComparison.Ordinal) ? secret[Prefix.Length..] : secret);
}
