using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tahsil.Application.Notifications;

namespace Tahsil.Infrastructure.Email;

/// <summary>
/// Says at startup what email will not do: without SMTP, customer emails are recorded as skipped (NTF-13); with a
/// legally relevant type turned off, the application must send it itself (NTF-4). Template overrides that do not
/// parse stop Tahsil, naming the template and the error (NTF-9).
/// </summary>
internal sealed partial class EmailStartupWarnings(IOptions<EmailOptions> email, IOptions<NotificationOptions> notifications, ILogger<EmailStartupWarnings> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (notifications.Value.TemplatesPath is { } directory)
        {
            if (!Directory.Exists(directory))
            {
                throw new InvalidOperationException($"Tahsil:Notifications:TemplatesPath '{directory}' does not exist.");
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.liquid"))
            {
                FluidEmailTemplateRenderer.Parse(file, File.ReadAllText(file));
            }
        }

        if (!email.Value.IsConfigured)
        {
            LogNotConfigured(logger);
        }

        foreach (var type in EmailTypes.LegallyRelevant.Where(type => !notifications.Value.IsEnabled(type)))
        {
            LogLegalTypeOff(logger, type);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(LogLevel.Warning, "Email is not configured (Tahsil:Email:FromAddress and Tahsil:Email:Smtp:Host): customer emails are recorded as skipped and not sent")]
    private static partial void LogNotConfigured(ILogger logger);

    [LoggerMessage(LogLevel.Warning, "The {Type} email is turned off; it backs a legal duty, so the application must send it itself from the matching webhook events")]
    private static partial void LogLegalTypeOff(ILogger logger, string type);
}
