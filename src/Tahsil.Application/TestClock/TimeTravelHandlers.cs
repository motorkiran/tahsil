using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tahsil.Application.Common;
using Tahsil.Application.Invoices;
using Tahsil.Contracts;
using Tahsil.Domain.Common;

namespace Tahsil.Application.TestClock;

/// <summary>Runs time travel: the billing clock moves to each due instant in turn and the workers process it (ENG-23).</summary>
public interface ITimeTravel
{
    /// <summary>Travels to <paramref name="target"/>; <see langword="null"/> when another time travel is under way.</summary>
    Task<TimeTravelSummary?> TravelAsync(DateTimeOffset target, Actor actor, CancellationToken cancellationToken);
}

/// <summary>What a time travel did (ENG-23).</summary>
public sealed record TimeTravelSummary(
    DateTimeOffset From, DateTimeOffset To, int Steps, int InvoicesCreated, int PaymentsAttempted, int PaymentsSucceeded, int PaymentsFailed, int SubscriptionsCanceled);

public static class TestClockErrors
{
    public static readonly Error NotTestMode = Error.NotFound("request.not_found", "The test API exists in Test mode only.");
    public static readonly Error InvalidTarget = Error.Validation(
        "test_clock.invalid", "Give advanceBy (an ISO 8601 duration such as P1M) or advanceTo (a later instant), up to 400 days ahead.", "advanceBy");
    public static readonly Error Busy = Error.Conflict("test_clock.busy", "Another time travel is under way; try again when it is done.");
}

public sealed class GetTestClockHandler(ITahsilDbContext context, IAuthorizer authorizer, IOptions<ServerOptions> server, BillingClock clock)
{
    public async Task<Result<TestClockResponse>> HandleAsync(CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.TestWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (!server.Value.IsTestMode)
        {
            return TestClockErrors.NotTestMode;
        }

        var stored = await context.TestClocks.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return new TestClockResponse(clock.GetUtcNow(), stored?.OffsetSeconds ?? 0, stored?.UpdatedAt);
    }
}

/// <summary>Moves the billing clock forward, never back, at most 400 days per call (ENG-22).</summary>
public sealed class AdvanceTestClockHandler(
    IAuthorizer authorizer, IRequestContext request, ITimeTravel travel, IOptions<ServerOptions> server, IOptions<BillingOptions> billing, BillingClock clock)
{
    private static readonly TimeSpan MaxTravel = TimeSpan.FromDays(400);

    public async Task<Result<TimeTravelResponse>> HandleAsync(AdvanceClockRequest advance, CancellationToken cancellationToken)
    {
        if (authorizer.Require(TahsilPermissions.TestWrite) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        if (!server.Value.IsTestMode)
        {
            return TestClockErrors.NotTestMode;
        }

        var now = clock.GetUtcNow();
        DateTimeOffset? target = (advance.AdvanceBy, advance.AdvanceTo) switch
        {
            ({ } duration, null) => IsoDuration.Add(now, duration, billing.Value.Zone),
            (null, { } instant) => instant,
            _ => null,
        };
        if (target is not { } to || to <= now || to - now > MaxTravel)
        {
            return TestClockErrors.InvalidTarget;
        }

        return await travel.TravelAsync(to, request.Caller.ToActor(), cancellationToken) is { } summary
            ? new TimeTravelResponse(
                summary.From, summary.To, summary.Steps, summary.InvoicesCreated, summary.PaymentsAttempted, summary.PaymentsSucceeded, summary.PaymentsFailed, summary.SubscriptionsCanceled)
            : TestClockErrors.Busy;
    }
}

/// <summary>ISO 8601 durations such as <c>P1Y2M10DT2H30M</c>; years and months are calendar steps in the billing time zone.</summary>
public static partial class IsoDuration
{
    public static DateTimeOffset? Add(DateTimeOffset from, string duration, TimeZoneInfo zone)
    {
        var match = Pattern().Match(duration);
        if (!match.Success || duration is "P" or "PT")
        {
            return null;
        }

        int Part(string name) => match.Groups[name].Success ? int.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture) : 0;
        try
        {
            var local = TimeZoneInfo.ConvertTime(from, zone).DateTime.AddYears(Part("years")).AddMonths(Part("months")).AddDays(Part("weeks") * 7 + Part("days"));
            var calendar = new DateTimeOffset(local, zone.GetUtcOffset(local));
            return calendar + new TimeSpan(Part("hours"), Part("minutes"), Part("seconds"));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^P(?:(?<years>\d{1,3})Y)?(?:(?<months>\d{1,4})M)?(?:(?<weeks>\d{1,4})W)?(?:(?<days>\d{1,5})D)?(?:T(?:(?<hours>\d{1,6})H)?(?:(?<minutes>\d{1,7})M)?(?:(?<seconds>\d{1,9})S)?)?$")]
    private static partial Regex Pattern();
}
