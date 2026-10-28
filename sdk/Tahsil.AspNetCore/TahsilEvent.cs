using Tahsil.Contracts;

namespace Tahsil.AspNetCore;

/// <summary>A verified webhook from Tahsil: its delivery's <c>webhook-id</c>, and the event (EVT-1).</summary>
/// <param name="Id">The <c>webhook-id</c>, the same for every delivery of the event: handle each once (SDK-12).</param>
/// <param name="Type">What happened, such as <see cref="TahsilEventTypes.InvoicePaid"/>.</param>
/// <param name="Timestamp">When it happened.</param>
/// <param name="Mode">The installation's mode, <c>test</c> or <c>live</c>.</param>
/// <param name="Data">To what, by whom, and the details.</param>
public sealed record TahsilEvent(string Id, string Type, DateTimeOffset Timestamp, string Mode, WebhookEventData Data)
{
    /// <summary>The details as their record, such as <see cref="InvoiceDetails"/> for <c>invoice.paid</c>.</summary>
    public T? DetailsAs<T>() => Data.As<T>();
}
