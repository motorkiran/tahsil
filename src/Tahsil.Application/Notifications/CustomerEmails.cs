using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Checkout;
using Tahsil.Application.Common;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Notifications;

namespace Tahsil.Application.Notifications;

/// <summary>
/// The intent to email a customer (NTF-11): the type, its subject, and the occurrence that makes it happen once
/// (NTF-12), recorded through the outbox in the transaction of the use case. <paramref name="Data"/> holds what the
/// occurrence adds, such as a reminder's offset.
/// </summary>
[OutboxMessage("notifications.customer_email")]
public sealed record SendCustomerEmail(string Type, Guid SubjectId, string OccurrenceKey, IReadOnlyDictionary<string, string>? Data = null);

/// <summary>What a type's email says now: its recipient, template and model.</summary>
public sealed record ComposedEmail(Customer Customer, string Template, Dictionary<string, object?> Model);

/// <summary>Builds one type's email when it is sent, so that links and amounts are current (NTF-10, NTF-11).</summary>
public interface ICustomerEmailComposer
{
    string Type { get; }

    /// <summary>The email, or <see langword="null"/> when it no longer applies, such as a reminder for an invoice paid meanwhile.</summary>
    Task<ComposedEmail?> ComposeAsync(SendCustomerEmail message, CancellationToken cancellationToken);
}

/// <summary>Records the intents of customer emails.</summary>
public static class CustomerEmails
{
    public static void Enqueue(IOutbox outbox, string type, Guid subjectId, string occurrenceKey, IReadOnlyDictionary<string, string>? data = null) =>
        outbox.Enqueue(new SendCustomerEmail(type, subjectId, $"{type}:{occurrenceKey}", data));
}

/// <summary>What every customer email's model holds about the merchant (CHK-30, NTF-5).</summary>
public static class EmailModels
{
    public static Dictionary<string, object?> Merchant(MerchantOptions merchant) => new(StringComparer.Ordinal)
    {
        ["name"] = merchant.DisplayName,
        ["legal_name"] = merchant.LegalName,
        ["mersis_number"] = merchant.MersisNumber,
        ["address"] = merchant.Address,
        ["phone"] = merchant.Phone,
        ["email"] = merchant.Email,
    };
}

/// <summary>
/// Sends customer emails once per occurrence (NTF-12): the type's composer builds the email when it is sent, and an
/// email turned off, without SMTP, without an address, or no longer applying is recorded as skipped (NTF-13, NTF-14).
/// </summary>
public sealed partial class CustomerEmailSender(
    ITahsilDbContext context,
    IEmailSender sender,
    IEmailTemplateRenderer templates,
    IEnumerable<ICustomerEmailComposer> composers,
    IOptions<NotificationOptions> notifications,
    IOptions<MerchantOptions> merchant,
    BillingClock clock,
    ILogger<CustomerEmailSender> logger) : IOutboxMessageHandler<SendCustomerEmail>
{
    public async Task HandleAsync(SendCustomerEmail message, CancellationToken cancellationToken)
    {
        if (await context.NotificationLogs.AnyAsync(log => log.OccurrenceKey == message.OccurrenceKey, cancellationToken))
        {
            return;
        }

        var now = clock.GetUtcNow();
        var composer = composers.Single(candidate => candidate.Type == message.Type);
        if (!notifications.Value.IsEnabled(message.Type))
        {
            await RecordAsync(NotificationLog.Skipped(message.Type, message.OccurrenceKey, null, "disabled", now), cancellationToken);
            return;
        }

        if (await composer.ComposeAsync(message, cancellationToken) is not { } email)
        {
            await RecordAsync(NotificationLog.Skipped(message.Type, message.OccurrenceKey, null, "no_longer_applies", now), cancellationToken);
            return;
        }

        if (email.Customer.Email is null)
        {
            LogNoAddress(logger, email.Customer.Id, message.Type);
            await RecordAsync(NotificationLog.Skipped(message.Type, message.OccurrenceKey, email.Customer.Id, "no_email_address", now), cancellationToken);
            return;
        }

        if (!sender.IsConfigured)
        {
            await RecordAsync(NotificationLog.Skipped(message.Type, message.OccurrenceKey, email.Customer.Id, "email_not_configured", now), cancellationToken);
            return;
        }

        email.Model["merchant"] = EmailModels.Merchant(merchant.Value);
        email.Model["customer"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = email.Customer.Name };
        var rendered = await templates.RenderAsync(email.Template, email.Customer.Locale, email.Model, cancellationToken);
        await sender.SendAsync(new EmailMessage(email.Customer.Email, email.Customer.Name, rendered.Subject, rendered.Html, rendered.Text, []), cancellationToken);
        await RecordAsync(NotificationLog.Sent(message.Type, message.OccurrenceKey, email.Customer.Id, now), cancellationToken);
    }

    private async Task RecordAsync(NotificationLog log, CancellationToken cancellationToken)
    {
        context.NotificationLogs.Add(log);
        await context.SaveChangesAsync(cancellationToken);
    }

    [LoggerMessage(LogLevel.Debug, "Customer {CustomerId} has no email address; their {Type} email is skipped")]
    private static partial void LogNoAddress(ILogger logger, Guid customerId, string type);
}
