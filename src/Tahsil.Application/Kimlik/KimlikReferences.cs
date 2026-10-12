using Microsoft.Extensions.Options;
using Tahsil.Domain.Catalog;
using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;

namespace Tahsil.Application.Kimlik;

/// <summary>
/// Checks what Tahsil records about Kimlik against Kimlik itself (KML-12): a product's Kimlik plan (CAT-5) and a
/// customer's Kimlik subscriber (CUS-3). With the connector off, or when Kimlik cannot be reached, values are accepted;
/// the connector reports them when it syncs. Plans are cached for five minutes, and read again when one is missing.
/// </summary>
public sealed class KimlikReferences(IKimlikClient kimlik, IOptions<KimlikOptions> options, TimeProvider time)
{
    private static readonly TimeSpan PlansLifetime = TimeSpan.FromMinutes(5);

    private volatile CachedPlans? _plans;

    public async Task<Result> CheckPlanAsync(string? plan, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled || plan is null)
        {
            return Result.Success();
        }

        var plans = await PlansAsync(fresh: false, cancellationToken);
        if (plans is not null && !plans.ContainsKey(plan))
        {
            // Created in Kimlik since the plans were read.
            plans = await PlansAsync(fresh: true, cancellationToken);
        }

        return plans is null || plans.GetValueOrDefault(plan) is { Kind: KimlikPlanKind.Base, IsArchived: false } ? Result.Success() : ProductErrors.UnknownKimlikPlan;
    }

    public async Task<Result> CheckSubscriberAsync(KimlikSubscriber? subscriber, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled || subscriber is null)
        {
            return Result.Success();
        }

        var found = subscriber.Type == KimlikSubscriberType.User
            ? (await kimlik.GetUserAsync(subscriber.Id, cancellationToken)).Error
            : (await kimlik.GetOrganizationAsync(subscriber.Id, cancellationToken)).Error;
        return found is { Type: ErrorType.NotFound } ? CustomerErrors.UnknownKimlikSubscriber : Result.Success();
    }

    /// <summary>Kimlik's plans by key, or <see langword="null"/> when Kimlik cannot tell.</summary>
    private async Task<IReadOnlyDictionary<string, KimlikPlan>?> PlansAsync(bool fresh, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (!fresh && _plans is { } cached && now < cached.Until)
        {
            return cached.Plans;
        }

        var read = await kimlik.ListPlansAsync(cancellationToken);
        if (read.IsFailure)
        {
            return null;
        }

        var plans = read.Value.ToDictionary(plan => plan.Key, StringComparer.Ordinal);
        _plans = new CachedPlans(plans, now + PlansLifetime);
        return plans;
    }

    private sealed record CachedPlans(IReadOnlyDictionary<string, KimlikPlan> Plans, DateTimeOffset Until);
}
