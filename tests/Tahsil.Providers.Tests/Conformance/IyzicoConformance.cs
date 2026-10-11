using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Tahsil.Providers.Abstractions;
using Tahsil.Providers.Iyzico;

namespace Tahsil.Providers.Tests.Conformance;

/// <summary>
/// The conformance suite against iyzico's sandbox (IYZ §8), when <c>TAHSIL_SANDBOX_IYZICO_API_KEY</c> and
/// <c>TAHSIL_SANDBOX_IYZICO_SECRET_KEY</c> are set. The sandbox account needs card storage and NON-3DS enabled.
/// </summary>
public sealed class IyzicoConformance : ConformanceSuite, IDisposable
{
    private static readonly string? ApiKey = Environment.GetEnvironmentVariable("TAHSIL_SANDBOX_IYZICO_API_KEY");
    private static readonly string? SecretKey = Environment.GetEnvironmentVariable("TAHSIL_SANDBOX_IYZICO_SECRET_KEY");
    private readonly HttpClient _http = new();

    protected override IPaymentProvider Provider { get; } = new IyzicoProvider();

    protected override string? Unavailable =>
        string.IsNullOrEmpty(ApiKey) || string.IsNullOrEmpty(SecretKey) ? "Set TAHSIL_SANDBOX_IYZICO_API_KEY and TAHSIL_SANDBOX_IYZICO_SECRET_KEY to run against iyzico's sandbox." : null;

    public void Dispose() => _http.Dispose();

    protected override ProviderContext Context() => new()
    {
        ConnectionId = Guid.Empty,
        Environment = ProviderEnvironment.Sandbox,
        Credentials = new Dictionary<string, string> { ["apiKey"] = ApiKey!, ["secretKey"] = SecretKey! },
        Settings = new Dictionary<string, string>(),
        Http = _http,
        Time = TimeProvider.System,
        Logger = NullLogger.Instance,
        NotificationUrl = new Uri("https://billing.example.com/provider-notifications/conformance"),
    };

    /// <summary>Saves one of iyzico's documented test cards with the sandbox-only card storage call (IYZ §8).</summary>
    protected override async Task<SavedCardReference> SaveCardAsync(TestCardScenario scenario)
    {
        var number = scenario switch
        {
            TestCardScenario.InsufficientFunds => "4111111111111129",
            TestCardScenario.Declined => "4129111111111111",
            _ => "5528790000000008",
        };
        using var answer = await IyzicoApi.PostAsync(Context(), "/cardstorage/card", new JsonObject
        {
            ["locale"] = "tr",
            ["conversationId"] = Guid.CreateVersion7().ToString("N"),
            ["email"] = "ayse@example.com",
            ["externalId"] = Guid.CreateVersion7().ToString("N"),
            ["card"] = new JsonObject
            {
                ["cardAlias"] = "Tahsil conformance",
                ["cardHolderName"] = "Ayse Yilmaz",
                ["cardNumber"] = number,
                ["expireMonth"] = "12",
                ["expireYear"] = "2030",
            },
        }, ProviderHttp.LookupTimeout, CancellationToken);
        answer.Succeeded.ShouldBeTrue(answer.ErrorMessage);
        return new SavedCardReference(answer.Root.TextAt("cardUserKey"), answer.Root.TextAt("cardToken")!);
    }
}
