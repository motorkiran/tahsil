using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Domain.Common;
using Tahsil.Server.Hosting;

namespace Tahsil.Server.Api;

/// <summary>
/// A token bucket per API key, so one application cannot crowd out the others. Limits are per instance. Requests
/// that fail authentication are refused before they reach the limiter.
/// </summary>
internal static class ApiRateLimiting
{
    public const string PolicyName = "api";

    /// <summary>The bucket of usage reporting, which is apart from an API key's other requests (API-14).</summary>
    public const string UsagePolicyName = "usage";

    private static readonly Error RateLimited = Error.RateLimited("rate_limited", "Too many requests for this API key; retry after the time in Retry-After.");

    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(PolicyName, context =>
            {
                var limits = context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
                var caller = context.Features.Get<ICallerFeature>()?.Caller as ApiKeyCaller;
                return RateLimitPartition.GetTokenBucketLimiter(caller?.ApiKeyId.ToString() ?? "anonymous", _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = limits.ApiBurst,
                    TokensPerPeriod = limits.ApiRequestsPerSecond,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });

            options.AddPolicy(UsagePolicyName, context =>
            {
                var limits = context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
                var caller = context.Features.Get<ICallerFeature>()?.Caller as ApiKeyCaller;
                return RateLimitPartition.GetTokenBucketLimiter($"usage:{caller?.ApiKeyId.ToString() ?? "anonymous"}", _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = limits.UsageBatchesPerSecond,
                    TokensPerPeriod = limits.UsageBatchesPerSecond,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });

            options.OnRejected = async (context, cancellationToken) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : TimeSpan.FromSeconds(1);
                context.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                await ApiResults.Problem(RateLimited).ExecuteAsync(context.HttpContext);
            };
        });

        return services;
    }
}
