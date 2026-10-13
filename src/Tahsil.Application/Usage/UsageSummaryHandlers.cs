using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Catalog;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Contracts;
using Tahsil.Domain.Catalog;
using Tahsil.Domain.Common;
using Tahsil.Domain.Customers;
using Tahsil.Domain.Subscriptions;

namespace Tahsil.Application.Usage;

/// <summary>A subscription's metered usage in its current period (USG §3), for the API, the admin panel and the customer.</summary>
public sealed class GetSubscriptionUsageHandler(ITahsilDbContext context, IAuthorizer authorizer, SubscriptionUsage usage)
{
    public async Task<Result<SubscriptionUsageResponse>> HandleAsync(Guid subscriptionId, CancellationToken cancellationToken)
    {
        if (authorizer.RequireOrCustomer(TahsilPermissions.UsageRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (await context.Subscriptions.AsNoTracking().Include(row => row.Items).SingleOrDefaultAsync(row => row.Id == subscriptionId, cancellationToken) is not { } subscription
            || !authorizer.Owns(subscription.CustomerId))
        {
            return SubscriptionErrors.NotFound;
        }

        return new SubscriptionUsageResponse(ToResponses(subscription, await usage.CurrentAsync(subscription, cancellationToken)));
    }

    internal static List<SubscriptionItemUsageResponse> ToResponses(Subscription subscription, IEnumerable<CurrentUsage> current) =>
        [.. current.Select(item => new SubscriptionItemUsageResponse(
            item.Item.Id,
            new SubscriptionPriceResponse(item.Price.Id, item.Price.Key, item.Price.Name, item.Product.ToSummary()),
            item.Meter.ToSummary(),
            item.From,
            item.Until,
            item.Quantity,
            item.Amount,
            subscription.Currency))];
}

public sealed record CustomerUsageQuery(string? Meter, DateTimeOffset? From = null, DateTimeOffset? To = null, UsageInterval Interval = UsageInterval.Day);

/// <summary>
/// A customer's usage on a meter per day or month of the billing time zone (USG §3), whether a subscription bills it or
/// not. Without a range: the last 30 days, or the last 12 months.
/// </summary>
public sealed class GetCustomerUsageHandler(ITahsilDbContext context, IAuthorizer authorizer, IUsageStore store, IOptions<BillingOptions> billing, BillingClock clock)
{
    /// <summary>The most intervals one answer has.</summary>
    public const int MaxIntervals = 400;

    public async Task<Result<CustomerUsageResponse>> HandleAsync(Guid customerId, CustomerUsageQuery query, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.UsageRead) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (!await context.Customers.AnyAsync(customer => customer.Id == customerId, cancellationToken))
        {
            return CustomerErrors.NotFound;
        }

        if (string.IsNullOrEmpty(query.Meter))
        {
            return CommonErrors.InvalidParameter("meter");
        }

        if (await context.FindMeterAsync(query.Meter, cancellationToken) is not { } meter)
        {
            return MeterErrors.NotFound;
        }

        var zone = billing.Value.Zone;
        var to = (query.To ?? clock.GetUtcNow()).ToUniversalTime();
        var from = query.From?.ToUniversalTime() ?? (query.Interval == UsageInterval.Day ? to.AddDays(-30) : Next(StartOf(to, UsageInterval.Month, zone), UsageInterval.Month, zone, -11));
        if (from >= to)
        {
            return CommonErrors.InvalidParameter("from");
        }

        var starts = new List<DateTimeOffset> { from };
        for (var next = Next(StartOf(from, query.Interval, zone), query.Interval, zone, 1); next < to; next = Next(next, query.Interval, zone, 1))
        {
            if (starts.Count == MaxIntervals)
            {
                return Error.Validation(CommonErrors.InvalidRequestCode, $"The range has more than {MaxIntervals} intervals; narrow it or ask for months.", "from");
            }

            starts.Add(next);
        }

        var quantities = await store.BucketsAsync(customerId, meter, starts, to, cancellationToken);
        var buckets = starts.Select((start, index) => new UsageBucketResponse(start, index + 1 < starts.Count ? starts[index + 1] : to, quantities.GetValueOrDefault(index))).ToList();
        return new CustomerUsageResponse(meter.ToSummary(), from, to, query.Interval, await store.AggregateAsync(customerId, meter, from, to, cancellationToken), buckets);
    }

    /// <summary>The start of the day or month of <paramref name="instant"/> in the billing time zone.</summary>
    private static DateTimeOffset StartOf(DateTimeOffset instant, UsageInterval interval, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        return At(interval == UsageInterval.Day ? local.Date : new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified), zone);
    }

    private static DateTimeOffset Next(DateTimeOffset start, UsageInterval interval, TimeZoneInfo zone, int count)
    {
        var local = TimeZoneInfo.ConvertTime(start, zone).DateTime;
        return At(interval == UsageInterval.Day ? local.AddDays(count) : local.AddMonths(count), zone);
    }

    private static DateTimeOffset At(DateTime local, TimeZoneInfo zone) =>
        new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone.GetUtcOffset(local)).ToUniversalTime();
}
