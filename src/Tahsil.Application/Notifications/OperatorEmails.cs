using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Domain.Notifications;

namespace Tahsil.Application.Notifications;

/// <summary>An email to the operator (NTF-7): an attention item that appeared, or the daily digest of those still open.</summary>
[OutboxMessage("notifications.operator_email")]
public sealed record SendOperatorEmail(Guid? AttentionItemId, DateOnly? DigestDate);

/// <summary>
/// Emails <c>Tahsil:Notifications:OperatorEmail</c> about attention items (NTF-7), once each and once a day for the
/// digest, in the installation's default language. Nothing is sent without the address or SMTP.
/// </summary>
public sealed class OperatorEmailSender(
    ITahsilDbContext context,
    IEmailSender sender,
    IEmailTemplateRenderer templates,
    IPublicUrls urls,
    IOptions<NotificationOptions> notifications,
    IOptions<ServerOptions> server,
    IOptions<BillingOptions> billing,
    BillingClock clock) : IOutboxMessageHandler<SendOperatorEmail>
{
    public async Task HandleAsync(SendOperatorEmail message, CancellationToken cancellationToken)
    {
        if (notifications.Value.OperatorEmail is not { } to || !sender.IsConfigured)
        {
            return;
        }

        var key = message.AttentionItemId is { } id ? $"operator_attention:{id}" : $"operator_digest:{message.DigestDate:yyyy-MM-dd}";
        if (await context.NotificationLogs.AnyAsync(log => log.OccurrenceKey == key, cancellationToken))
        {
            return;
        }

        var items = await context.AttentionItems.AsNoTracking()
            .Where(item => message.AttentionItemId == null ? item.ResolvedAt == null : item.Id == message.AttentionItemId)
            .OrderBy(item => item.RaisedAt)
            .ToListAsync(cancellationToken);
        var now = clock.GetUtcNow();
        if (items.Count == 0 || items.All(item => item.ResolvedAt is not null))
        {
            context.NotificationLogs.Add(NotificationLog.Skipped("operator", key, null, "no_longer_applies", now));
            await context.SaveChangesAsync(cancellationToken);
            return;
        }

        var locale = server.Value.DefaultLocale;
        var culture = Formatting.CultureFor(locale);
        var rendered = await templates.RenderAsync(message.AttentionItemId is null ? "operator-digest" : "operator-attention", locale, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["items"] = items.Select(item => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["kind"] = System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(item.Kind.ToString()),
                ["subject"] = $"{item.SubjectType} {item.SubjectId}",
                ["raised_at"] = Formatting.Date(item.RaisedAt, billing.Value.Zone, culture),
                ["details"] = item.Details.Select(detail => $"{detail.Key}: {detail.Value}").ToList(),
            }).ToList(),
            ["admin_url"] = urls.Build("admin/attention").AbsoluteUri,
        }, cancellationToken);
        await sender.SendAsync(new EmailMessage(to, null, rendered.Subject, rendered.Html, rendered.Text, []), cancellationToken);
        context.NotificationLogs.Add(NotificationLog.Sent("operator", key, null, now));
        await context.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Queues the daily digest of open attention items once a day, from 08:00 in the billing time zone (NTF-7).</summary>
public sealed class OperatorDigest(ITahsilDbContext context, IOutbox outbox, IOptions<NotificationOptions> notifications, IOptions<BillingOptions> billing, BillingClock clock)
{
    public async Task QueueIfDueAsync(CancellationToken cancellationToken)
    {
        var local = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), billing.Value.Zone);
        var date = DateOnly.FromDateTime(local.DateTime);
        var key = $"operator_digest:{date:yyyy-MM-dd}";
        if (notifications.Value.OperatorEmail is null || local.Hour < 8
            || await context.NotificationLogs.AnyAsync(log => log.OccurrenceKey == key, cancellationToken)
            || !await context.AttentionItems.AnyAsync(item => item.ResolvedAt == null, cancellationToken))
        {
            return;
        }

        outbox.Enqueue(new SendOperatorEmail(null, date));
        await context.SaveChangesAsync(cancellationToken);
    }
}
