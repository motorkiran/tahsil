using System.Text.Json;
using Tahsil.Application.Common;
using Tahsil.Infrastructure.Persistence;
using Tahsil.Infrastructure.Persistence.Conventions;

namespace Tahsil.Infrastructure.Outbox;

internal sealed class Outbox(TahsilDbContext context, OutboxMessageTypes types, BillingClock clock) : IOutbox
{
    public void Enqueue<TMessage>(TMessage message)
        where TMessage : class
    {
        var now = clock.GetUtcNow();

        context.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.CreateVersion7(now),
            Type = types.GetName(typeof(TMessage)),
            Payload = JsonSerializer.Serialize(message, StorageJson.Options),
            OccurredAt = now,
            Status = OutboxMessageStatus.Pending,
            NextAttemptAt = now,
        });
    }
}
