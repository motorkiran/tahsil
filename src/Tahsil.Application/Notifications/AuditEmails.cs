using Tahsil.Domain.Auditing;

namespace Tahsil.Application.Notifications;

/// <summary>
/// Customer emails that follow from what happened, whatever path it took (NTF §1): queued with the audit event, in
/// its transaction, so a receipt or a cancellation notice is never forgotten by one of the paths that pay or cancel.
/// </summary>
public static class AuditEmails
{
    public static SendCustomerEmail? From(string action, AuditSubject? subject, IReadOnlyDictionary<string, object?>? data)
    {
        if (subject is null || !Guid.TryParse(subject.Id, out var id))
        {
            return null;
        }

        var type = action switch
        {
            // A checkout's invoice is confirmed by the order confirmation (NTF-3).
            AuditActions.InvoicePaid when data?.GetValueOrDefault("billingReason") is not "checkout" => EmailTypes.Receipt,
            AuditActions.SubscriptionCanceled => EmailTypes.SubscriptionCanceled,
            AuditActions.RefundSucceeded => EmailTypes.RefundIssued,
            _ => null,
        };
        return type is null ? null : new SendCustomerEmail(type, id, $"{type}:{id}");
    }
}
