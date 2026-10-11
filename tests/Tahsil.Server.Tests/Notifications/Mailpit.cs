using System.Net.Http.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Tahsil.Server.Tests.Notifications;

/// <summary>
/// A Mailpit container that Tahsil sends real email to over SMTP, as in development (compose.yaml), read back through
/// Mailpit's API.
/// </summary>
public sealed class Mailpit : IAsyncLifetime
{
    private const int SmtpPort = 1025;
    private const int ApiPort = 8025;

    private readonly IContainer _container = new ContainerBuilder("docker.io/axllent/mailpit:v1.31.4")
        .WithPortBinding(SmtpPort, assignRandomHostPort: true)
        .WithPortBinding(ApiPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(ApiPort).ForPath("/readyz")))
        .Build();

    private HttpClient? _api;

    /// <summary>The settings that make Tahsil send through this Mailpit.</summary>
    public Dictionary<string, string?> Settings => new()
    {
        ["Tahsil:Email:FromAddress"] = "billing@shop.example",
        ["Tahsil:Email:Smtp:Host"] = _container.Hostname,
        ["Tahsil:Email:Smtp:Port"] = _container.GetMappedPublicPort(SmtpPort).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Tahsil:Email:Smtp:Security"] = "None",
    };

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        _api = new HttpClient { BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(ApiPort)}") };
    }

    /// <summary>The emails <paramref name="address"/> received, oldest first.</summary>
    public async Task<List<MailpitMessage>> ReceivedByAsync(string address)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var api = _api!;
        var list = await api.GetFromJsonAsync<MessageList>(new Uri("/api/v1/messages?limit=1000", UriKind.Relative), cancellationToken);
        var received = new List<MailpitMessage>();
        foreach (var summary in list!.Messages.Where(message => message.To.Any(to => to.Address == address)).OrderBy(message => message.Created))
        {
            var message = await api.GetFromJsonAsync<MessageBody>(new Uri($"/api/v1/message/{summary.Id}", UriKind.Relative), cancellationToken);
            received.Add(new MailpitMessage(summary.Subject, message!.Text));
        }

        return received;
    }

    public async ValueTask DisposeAsync()
    {
        _api?.Dispose();
        await _container.DisposeAsync();
    }

    private sealed record MessageList(List<MessageSummary> Messages);

    private sealed record MessageSummary(string Id, string Subject, List<Recipient> To, DateTimeOffset Created);

    private sealed record Recipient(string Address);

    private sealed record MessageBody(string Text);
}

public sealed record MailpitMessage(string Subject, string Text);
