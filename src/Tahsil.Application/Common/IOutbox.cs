namespace Tahsil.Application.Common;

/// <summary>
/// Queues side effects (emails, webhooks, Kimlik updates) in the current unit of work. They are delivered after
/// the transaction commits and never for changes that were rolled back.
/// </summary>
public interface IOutbox
{
    void Enqueue<TMessage>(TMessage message)
        where TMessage : class;
}

/// <summary>Delivers one kind of outbox message. Delivery is at least once, so handlers must tolerate repeats.</summary>
public interface IOutboxMessageHandler<in TMessage>
    where TMessage : class
{
    Task HandleAsync(TMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// The stable name under which a message is stored. It must never change once messages of the type may be pending,
/// so it is written out instead of derived from the class name.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class OutboxMessageAttribute(string type) : Attribute
{
    public string Type { get; } = type;
}
