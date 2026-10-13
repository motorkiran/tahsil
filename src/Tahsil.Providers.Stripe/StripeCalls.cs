using System.Net;
using Stripe;
using Tahsil.Providers.Abstractions;

namespace Tahsil.Providers.Stripe;

/// <summary>How a call to Stripe ended: its result, Stripe's error, or no answer (timeout, network, Stripe down).</summary>
internal readonly record struct StripeAnswer<T>(T? Value, StripeException? Error, bool Unanswered)
    where T : class
{
    public bool Succeeded => Value is not null;

    /// <summary>The failure code of an answer that did not succeed.</summary>
    public FailureCode Failure => Unanswered ? FailureCode.ProviderUnavailable : StripeFailures.Map(Error?.StripeError, Error?.HttpStatusCode ?? HttpStatusCode.InternalServerError);

    public string? Message => Unanswered ? "Stripe did not answer." : Error?.StripeError?.Message ?? Error?.Message;
}

/// <summary>
/// One <see cref="StripeClient"/> per connection and call (STR-2), on the adapter's own HTTP client, with Stripe.net's
/// two network retries reusing Tahsil's idempotency key (STR-3), telemetry off and Tahsil's own timeouts.
/// </summary>
internal static class StripeCalls
{
    public const string SecretKey = "secretKey";

    public static StripeClient Client(ProviderContext context) => new(new StripeClientOptions
    {
        ApiKey = context.Credential(SecretKey),
        HttpClient = new SystemNetHttpClient(context.Http, maxNetworkRetries: 2, appInfo: new AppInfo { Name = "Tahsil" }, enableTelemetry: false),
    });

    /// <summary>Tahsil's idempotency key for an operation on a reference (STR-3).</summary>
    public static RequestOptions Idempotent(ProviderContext context, string operation, string reference) =>
        new() { IdempotencyKey = $"tahsil:{context.ConnectionId}:{operation}:{reference}" };

    public static async Task<StripeAnswer<T>> CallAsync<T>(Func<CancellationToken, Task<T>> call, TimeSpan timeout, CancellationToken cancellationToken)
        where T : class
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return new StripeAnswer<T>(await call(deadline.Token), null, false);
        }
        catch (StripeException exception) when (exception.StripeError is not null || exception.HttpStatusCode != 0)
        {
            // 429 and 5xx answers say nothing about what happened.
            var unanswered = exception.HttpStatusCode == HttpStatusCode.TooManyRequests || (int)exception.HttpStatusCode >= 500;
            return new StripeAnswer<T>(null, exception, unanswered);
        }
        catch (Exception exception) when (exception is HttpRequestException or StripeException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new StripeAnswer<T>(null, null, true);
        }
    }
}
