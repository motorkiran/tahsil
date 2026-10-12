namespace Tahsil.Application.Notifications;

/// <summary>A file sent with an email, such as an accepted legal document.</summary>
public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

public sealed record EmailMessage(string To, string? ToName, string Subject, string Html, string Text, IReadOnlyList<EmailAttachment> Attachments);

/// <summary>Sends email, through SMTP in production (NTF-13).</summary>
public interface IEmailSender
{
    /// <summary>Whether email can be sent; when not, customer emails are recorded as skipped.</summary>
    bool IsConfigured { get; }

    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

public sealed record RenderedEmail(string Subject, string Html, string Text);

/// <summary>Renders an email template in a locale (NTF-8): its subject, HTML and text parts.</summary>
public interface IEmailTemplateRenderer
{
    Task<RenderedEmail> RenderAsync(string templateName, string locale, IReadOnlyDictionary<string, object?> model, CancellationToken cancellationToken);
}

/// <summary>From the <c>Tahsil:Notifications</c> configuration section.</summary>
public sealed class NotificationOptions
{
    public const string SectionName = "Tahsil:Notifications";

    /// <summary>Email types by name, such as <c>order_confirmation</c>; every type is on unless turned off.</summary>
    public Dictionary<string, NotificationTypeOptions> Types { get; set; } = [];

    /// <summary>A directory whose <c>.liquid</c> files replace the built-in templates of the same name (NTF-9).</summary>
    public string? TemplatesPath { get; set; }

    /// <summary>Where operator emails go: attention items when they appear, and a daily digest of those still open (NTF-7).</summary>
    public string? OperatorEmail { get; set; }

    /// <summary>Days before a trial's end that the customer is reminded of it (SUB-16).</summary>
    public int TrialEndingNoticeDays { get; set; } = 3;

    /// <summary>Days before a card used by a subscription expires that the customer is told (CUS-15).</summary>
    public int CardExpiringNoticeDays { get; set; } = 30;

    /// <summary>
    /// Days before a renewal that the customer is reminded of it, by the prices' interval (NTF-6): 14 for yearly
    /// subscriptions by default, 0 (none) for the others.
    /// </summary>
    public Dictionary<string, int> UpcomingRenewalNoticeDays { get; set; } = new(StringComparer.OrdinalIgnoreCase) { ["year"] = 14, ["month"] = 0, ["week"] = 0, ["day"] = 0 };

    public int RenewalNoticeDays(string interval) => UpcomingRenewalNoticeDays.GetValueOrDefault(interval);

    internal bool HasValidNotices() =>
        TrialEndingNoticeDays is >= 1 and <= 30 && CardExpiringNoticeDays is >= 1 and <= 90 && UpcomingRenewalNoticeDays.Values.All(days => days is >= 0 and <= 60);

    public bool IsEnabled(string type) => Types.GetValueOrDefault(type)?.Enabled ?? true;
}

public sealed class NotificationTypeOptions
{
    public bool Enabled { get; set; } = true;
}

public static class EmailTypes
{
    public const string OrderConfirmation = "order_confirmation";
    public const string InvoiceIssued = "invoice_issued";
    public const string InvoiceReminder = "invoice_reminder";
    public const string Receipt = "receipt";
    public const string PaymentFailed = "payment_failed";
    public const string PaymentActionRequired = "payment_action_required";
    public const string DunningFinalReminder = "dunning_final_reminder";
    public const string SubscriptionCanceled = "subscription_canceled";
    public const string RefundIssued = "refund_issued";
    public const string PriceChange = "price_change";
    public const string TrialEnding = "trial_ending";
    public const string UpcomingRenewal = "upcoming_renewal";
    public const string CardExpiring = "card_expiring";
    public const string SubscriptionUpdated = "subscription_updated";
    public const string WithdrawalConfirmed = "withdrawal_confirmed";

    /// <summary>Types that back legal duties (NTF-4): turning one off leaves the duty to the application.</summary>
    public static readonly IReadOnlyList<string> LegallyRelevant = [OrderConfirmation, SubscriptionCanceled, PriceChange, TrialEnding, WithdrawalConfirmed];
}
