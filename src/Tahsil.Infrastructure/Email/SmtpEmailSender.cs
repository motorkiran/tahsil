using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using Tahsil.Application.Checkout;
using Tahsil.Application.Notifications;

namespace Tahsil.Infrastructure.Email;

/// <summary>Sends email through an SMTP relay with MailKit (NTF-13), as Kimlik does.</summary>
internal sealed class SmtpEmailSender(IOptions<EmailOptions> options, IOptions<MerchantOptions> merchant) : IEmailSender
{
    public bool IsConfigured => options.Value.IsConfigured;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (settings is not { FromAddress: { } fromAddress, Smtp.Host: { } host } || !settings.IsConfigured)
        {
            throw new InvalidOperationException("Email delivery is not configured. Set Tahsil:Email:FromAddress and Tahsil:Email:Smtp:Host.");
        }

        using var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(settings.FromName ?? merchant.Value.DisplayName ?? "Tahsil", fromAddress));
        mime.To.Add(new MailboxAddress(message.ToName ?? string.Empty, message.To));
        mime.Subject = message.Subject;
        var body = new BodyBuilder { HtmlBody = message.Html, TextBody = message.Text };
        foreach (var attachment in message.Attachments)
        {
            body.Attachments.Add(attachment.FileName, attachment.Content, ContentType.Parse(attachment.ContentType));
        }

        mime.Body = body.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(host, settings.Smtp.Port, ToSocketOptions(settings.Smtp.Security), cancellationToken);
        if (!string.IsNullOrEmpty(settings.Smtp.Username))
        {
            await client.AuthenticateAsync(settings.Smtp.Username, settings.Smtp.Password ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }

    private static SecureSocketOptions ToSocketOptions(SmtpSecurity security) => security switch
    {
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SmtpSecurity.None => SecureSocketOptions.None,
        _ => SecureSocketOptions.Auto,
    };
}
