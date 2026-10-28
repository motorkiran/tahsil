using System.Collections.Concurrent;

namespace Tahsil.AspNetCore;

/// <summary>
/// Remembers which webhooks were handled, so that a redelivery is not handled twice (SDK-12). The default keeps them
/// in memory, which suits an application with one instance; with several, store the IDs where every instance sees
/// them, such as in the database the handler writes to, and register that implementation.
/// </summary>
public interface ITahsilWebhookDeduplicator
{
    /// <summary>Claims the webhook; <see langword="false"/> when it was handled already, or is being handled.</summary>
    ValueTask<bool> TryBeginAsync(string webhookId, CancellationToken cancellationToken);

    /// <summary>Gives the webhook back after its handler failed, so that Tahsil's retry handles it.</summary>
    ValueTask ForgetAsync(string webhookId, CancellationToken cancellationToken);
}

/// <summary>
/// The in-memory deduplicator: webhook IDs are kept three days, longer than Tahsil retries a delivery (about 45 hours).
/// </summary>
public sealed class InMemoryTahsilWebhookDeduplicator(TimeProvider timeProvider) : ITahsilWebhookDeduplicator
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(3);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private DateTimeOffset _pruned = DateTimeOffset.MinValue;

    public InMemoryTahsilWebhookDeduplicator()
        : this(TimeProvider.System)
    {
    }

    public ValueTask<bool> TryBeginAsync(string webhookId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (now - _pruned > TimeSpan.FromHours(1))
        {
            _pruned = now;
            foreach (var (id, at) in _seen)
            {
                if (now - at > Retention)
                {
                    _seen.TryRemove(id, out _);
                }
            }
        }

        return ValueTask.FromResult(_seen.TryAdd(webhookId, now));
    }

    public ValueTask ForgetAsync(string webhookId, CancellationToken cancellationToken)
    {
        _seen.TryRemove(webhookId, out _);
        return ValueTask.CompletedTask;
    }
}
