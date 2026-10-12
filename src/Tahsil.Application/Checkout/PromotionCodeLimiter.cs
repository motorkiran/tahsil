using System.Collections.Concurrent;

namespace Tahsil.Application.Checkout;

/// <summary>
/// Limits promotion code attempts on checkout pages against guessing (CPN §7): at most
/// <see cref="MaxPerSession"/> per session, counted on the session, and <see cref="MaxPerNetworkPerHour"/> per network
/// across sessions, counted here in memory, per instance, like the API's rate limits.
/// </summary>
public sealed class PromotionCodeLimiter
{
    public const int MaxPerSession = 10;
    public const int MaxPerNetworkPerHour = 30;

    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _attempts = new(StringComparer.Ordinal);

    /// <summary>Counts an attempt from <paramref name="network"/>, unless it already made as many as an hour allows.</summary>
    public bool TryAttempt(string? network, DateTimeOffset now)
    {
        if (network is null)
        {
            return true;
        }

        var attempts = _attempts.GetOrAdd(network, _ => new Queue<DateTimeOffset>());
        lock (attempts)
        {
            while (attempts.TryPeek(out var oldest) && now - oldest >= TimeSpan.FromHours(1))
            {
                attempts.Dequeue();
            }

            if (attempts.Count >= MaxPerNetworkPerHour)
            {
                return false;
            }

            attempts.Enqueue(now);
            return true;
        }
    }
}
