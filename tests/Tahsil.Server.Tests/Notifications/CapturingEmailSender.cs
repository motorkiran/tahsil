using System.Collections.Concurrent;
using Tahsil.Application.Notifications;

namespace Tahsil.Server.Tests.Notifications;

/// <summary>Keeps the emails Tahsil sends, for tests to read.</summary>
internal sealed class CapturingEmailSender : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();

    public bool IsConfigured => true;

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }
}
